using System.Text.Json;
using Npgsql;

namespace CarbonShift.Database.Tests;

public sealed class LocalDatabaseFactAttribute : FactAttribute
{
    public LocalDatabaseFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CARBONSHIFT_TEST_LOCAL_DB") != "1")
            Skip = "Opt in with CARBONSHIFT_TEST_LOCAL_DB=1 and CARBONSHIFT_TEST_LOCAL_CONFIG_FILE pointing to a localhost administrator config.";
    }
}
public class MigrationTests
{
    [LocalDatabaseFact]
    public async Task Migration_preserves_legacy_rows_blocks_takeover_and_enforces_RLS_and_atomic_save()
    {
        string path = Environment.GetEnvironmentVariable("CARBONSHIFT_TEST_LOCAL_CONFIG_FILE") ?? throw new InvalidOperationException("Local test config required.");
        var b = new NpgsqlConnectionStringBuilder(await File.ReadAllTextAsync(path));
        if (b.Host is not ("localhost" or "127.0.0.1" or "::1")) throw new InvalidOperationException("This test refuses remote databases.");
        b.Database = "postgres";
        await using var admin = new NpgsqlConnection(b.ConnectionString); await admin.OpenAsync();
        await Exec(admin, "SELECT pg_advisory_lock(hashtext('carbonshift_local_rls_tests'))");
        string name = "cs_auth_" + Guid.NewGuid().ToString("N"); var createdRoles = new List<string>();
        string root = FindRoot(); bool createdDatabase = false;
        try
        {
            foreach (string role in new[] { "anon", "authenticated", "service_role" })
            {
                await using var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=@role)", admin); exists.Parameters.AddWithValue("role", role);
                if (!(bool)(await exists.ExecuteScalarAsync())!) { await Exec(admin, "CREATE ROLE " + role + " NOLOGIN NOSUPERUSER NOBYPASSRLS"); createdRoles.Add(role); }
            }
            await Exec(admin, "CREATE DATABASE " + name); createdDatabase = true;
            b.Database = name;
            await using var conn = new NpgsqlConnection(b.ConnectionString); await conn.OpenAsync();
            await Exec(conn, """
                CREATE SCHEMA auth;
                CREATE TABLE auth.users(id uuid PRIMARY KEY DEFAULT gen_random_uuid(),email text NOT NULL,raw_app_meta_data jsonb DEFAULT '{}',raw_user_meta_data jsonb DEFAULT '{}');
                CREATE FUNCTION auth.uid() RETURNS uuid LANGUAGE sql STABLE AS 'SELECT (NULLIF(current_setting(''request.jwt.claims'',true),'''')::jsonb->>''sub'')::uuid';
                GRANT USAGE ON SCHEMA auth TO authenticated;
                """);
            await Exec(conn, await File.ReadAllTextAsync(Path.Combine(root,"database/schema.sql")));
            await Exec(conn, await File.ReadAllTextAsync(Path.Combine(root,"database/seed.sql")));
            await Exec(conn, "INSERT INTO public.\"user\"(name,email,password_hash) SELECT 'B','b@example.test',password_hash FROM public.\"user\" LIMIT 1");
            string before = (string)(await Scalar(conn, "SELECT jsonb_agg(to_jsonb(u) ORDER BY user_id)::text FROM public.\"user\" u"))!;
            string migration = await File.ReadAllTextAsync(Path.Combine(root,"supabase/migrations/20261122000100_auth_rpc_rls.sql"));
            await Exec(conn, migration); await Exec(conn, migration); // Must be repeatable without changing users.
            string after = (string)(await Scalar(conn, "SELECT jsonb_agg(to_jsonb(u)-'auth_user_id' ORDER BY user_id)::text FROM public.\"user\" u"))!;
            Assert.Equal(before, after);
            await Denied(conn, "INSERT INTO auth.users(email) VALUES('admin@carbonshift.com')", "P0001");
            await Denied(conn, "INSERT INTO auth.users(email,raw_user_meta_data) VALUES('admin@carbonshift.com','{\"carbonshift_legacy_user_id\":1}')", "P0001");
            Guid first = Guid.NewGuid(), second = Guid.NewGuid();
            await Exec(conn, $"INSERT INTO auth.users(id,email,raw_app_meta_data) VALUES ('{first}','admin@carbonshift.com','{{\"carbonshift_legacy_user_id\":1}}'),('{second}','b@example.test','{{\"carbonshift_legacy_user_id\":2}}')");
            Assert.Equal(2L, await Scalar(conn,"SELECT count(*) FROM public.\"user\""));
            await Exec(conn, "INSERT INTO auth.users(email,raw_user_meta_data) VALUES('new@example.test','{\"name\":\"New\"}')");
            Assert.Equal(3L, await Scalar(conn,"SELECT count(*) FROM public.\"user\""));
            await AsUser(conn, first);
            using (var profile = JsonDocument.Parse((await Scalar(conn,"SELECT public.carbonshift_profile()"))!.ToString()!)) Assert.Equal(1,profile.RootElement.GetProperty("userId").GetInt32());
            await Denied(conn,"SELECT password_hash FROM public.\"user\"","42501");
            await Denied(conn,"SELECT public.carbonshift_legacy_account('admin@carbonshift.com')","42501");
            await Denied(conn,$"SELECT public.carbonshift_create_preview('{first}','{{}}',now()+interval '15 minutes')","42501");
            await Denied(conn,"SELECT * FROM carbonshift_private.preview","42501");
            await Denied(conn,"DELETE FROM public.activity","42501");
            Assert.Equal(1L,await Scalar(conn,"SELECT count(*) FROM public.\"user\""));
            await Exec(conn,"RESET ROLE");
            var tomorrow=DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2);
            var payload=new { request=new {activityName="Test",durationHours=2,energyKwh=.12m,date=tomorrow.ToString("yyyy-MM-dd")},
                best=new {start=$"{tomorrow:yyyy-MM-dd}T06:00:00+07:00",end=$"{tomorrow:yyyy-MM-dd}T08:00:00+07:00",greenScore=82},
                fetchedAt=DateTimeOffset.UtcNow.ToString("O"),locationLabel="Yogyakarta [Asia/Jakarta]",timeZone="Asia/Jakarta",temperature=28,condition="Cerah",solarPotential=80,explanation="Test evidence"};
            async Task<Guid> Preview(Guid owner)
            {
                await Exec(conn,"RESET ROLE");
                await using var cmd=new NpgsqlCommand("SELECT public.carbonshift_create_preview(@owner,@payload,now()+interval '15 minutes')",conn);
                cmd.Parameters.AddWithValue("owner",owner);cmd.Parameters.AddWithValue("payload",NpgsqlTypes.NpgsqlDbType.Jsonb,JsonSerializer.Serialize(payload));
                return (Guid)(await cmd.ExecuteScalarAsync())!;
            }
            Guid preview=await Preview(first);
            await AsUser(conn,second); await Denied(conn,$"SELECT public.carbonshift_save_preview('{preview}')","P0002");
            await AsUser(conn,first);
            string saved=(await Scalar(conn,$"SELECT public.carbonshift_save_preview('{preview}')"))!.ToString()!;
            Assert.Equal(saved,(await Scalar(conn,$"SELECT public.carbonshift_save_preview('{preview}')"))!.ToString());
            Assert.Equal(1L,await Scalar(conn,"SELECT count(*) FROM public.activity"));
            await Exec(conn,"RESET ROLE");
            Assert.Equal(tomorrow.ToDateTime(new TimeOnly(6,0)),await Scalar(conn,"SELECT start_time FROM public.time_slot"));
            Guid other=await Preview(second); await AsUser(conn,second);await Scalar(conn,$"SELECT public.carbonshift_save_preview('{other}')");
            using (var rows=JsonDocument.Parse((await Scalar(conn,"SELECT public.carbonshift_history()"))!.ToString()!)) Assert.Equal(1,rows.RootElement.GetArrayLength());
            await AsUser(conn,first);await Exec(conn,"SELECT public.carbonshift_delete_history()");
            Assert.Equal("[]",(await Scalar(conn,"SELECT public.carbonshift_history()"))!.ToString());
            await AsUser(conn,second);
            using (var rows=JsonDocument.Parse((await Scalar(conn,"SELECT public.carbonshift_history()"))!.ToString()!)) Assert.Equal(1,rows.RootElement.GetArrayLength());
            Guid expired=await Preview(first);await Exec(conn,$"UPDATE carbonshift_private.preview SET expires_at=now()-interval '1 minute' WHERE id='{expired}'");
            await AsUser(conn,first);await Denied(conn,$"SELECT public.carbonshift_save_preview('{expired}')","P0002");
            await Exec(conn,"RESET ROLE; SET ROLE anon");await Denied(conn,"SELECT public.carbonshift_history()","42501");
            await Exec(conn,"RESET ROLE");
            Assert.Equal(1L,await Scalar(conn,"SELECT count(*) FROM public.recommendation")); // Only B remains. The expired preview never wrote data.
            await Exec(conn,"RESET ROLE");
            // Preserve native accounts even if a profile temporarily lacks its UUID mapping but matches Auth email.
            await Exec(conn,"INSERT INTO auth.users(email) VALUES('protected-native@example.test'); UPDATE public.\"user\" SET auth_user_id=NULL,password_hash=(SELECT password_hash FROM public.\"user\" WHERE user_id=1) WHERE email='protected-native@example.test'");
            string protectedSql="SELECT jsonb_agg(to_jsonb(u) ORDER BY user_id)::text FROM public.\"user\" u WHERE auth_user_id IS NOT NULL OR EXISTS(SELECT 1 FROM auth.users a WHERE lower(a.email)=lower(u.email))";
            string preserved=(string)(await Scalar(conn,protectedSql))!;
            await Exec(conn,"""
                DO $$ DECLARE uid integer; aid integer; tid integer; fid integer; BEGIN
                  INSERT INTO public."user"(name,email,password_hash) SELECT 'Obsolete','obsolete@example.test',password_hash FROM public."user" WHERE user_id=1 RETURNING user_id INTO uid;
                  INSERT INTO public.activity(user_id,activity_name,duration_minutes,power_consumption_kwh) VALUES(uid,'Old test',60,.1) RETURNING activity_id INTO aid;
                  SELECT forecast_id INTO fid FROM public.weather_forecast LIMIT 1;
                  INSERT INTO public.time_slot(activity_id,forecast_id,start_time,end_time,green_score) VALUES(aid,fid,now()::timestamp,(now()+interval '1 hour')::timestamp,50) RETURNING timeslot_id INTO tid;
                  INSERT INTO public.recommendation(timeslot_id,green_score) VALUES(tid,50);
                  INSERT INTO carbonshift_private.preview(user_id,payload,expires_at) VALUES(uid,'{}',now()+interval '15 minutes');
                END $$;
                """);
            string cleanup=await File.ReadAllTextAsync(Path.Combine(root,"supabase/migrations/20261122000200_remove_legacy_test_accounts.sql"));
            await Exec(conn,cleanup);await Exec(conn,cleanup);
            Assert.Equal(preserved,(string)(await Scalar(conn,protectedSql))!);
            Assert.Equal(0L,await Scalar(conn,"SELECT count(*) FROM public.\"user\" WHERE email='obsolete@example.test'"));
            Assert.Equal(1L,await Scalar(conn,"SELECT count(*) FROM public.recommendation"));
            Assert.Equal(true,await Scalar(conn,"SELECT to_regprocedure('public.carbonshift_legacy_account(text)') IS NULL"));
            await Exec(conn,"INSERT INTO auth.users(email) VALUES('fresh-auth@example.test'); UPDATE auth.users SET raw_user_meta_data='{\"name\":\"Metadata Updated\"}' WHERE email='fresh-auth@example.test'");
            Assert.Equal("Metadata Updated",await Scalar(conn,"SELECT name FROM public.\"user\" WHERE email='fresh-auth@example.test'"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if(createdDatabase) await Exec(admin,"DROP DATABASE "+name+" WITH (FORCE)");
            foreach(string role in createdRoles) await Exec(admin,"DROP ROLE "+role);
            await Exec(admin,"SELECT pg_advisory_unlock(hashtext('carbonshift_local_rls_tests'))");
        }
    }
    static async Task AsUser(NpgsqlConnection conn,Guid id)
    {
        await Exec(conn,"RESET ROLE; SET ROLE authenticated");
        await using var cmd=new NpgsqlCommand("SELECT set_config('request.jwt.claims',@claims,false)",conn);
        cmd.Parameters.AddWithValue("claims",JsonSerializer.Serialize(new{sub=id,role="authenticated"}));await cmd.ExecuteNonQueryAsync();
    }
    static async Task Exec(NpgsqlConnection conn,string sql) {await using var cmd=new NpgsqlCommand(sql,conn);await cmd.ExecuteNonQueryAsync();}
    static async Task<object?> Scalar(NpgsqlConnection conn,string sql) {await using var cmd=new NpgsqlCommand(sql,conn);return await cmd.ExecuteScalarAsync();}
    static async Task Denied(NpgsqlConnection conn,string sql,string state) {var ex=await Assert.ThrowsAsync<PostgresException>(()=>Exec(conn,sql));Assert.Equal(state,ex.SqlState);}
    static string FindRoot() {var path=new DirectoryInfo(AppContext.BaseDirectory);while(path!=null&&!Directory.Exists(Path.Combine(path.FullName,"supabase")))path=path.Parent;return path?.FullName??throw new InvalidOperationException("Repository root missing");}
}
