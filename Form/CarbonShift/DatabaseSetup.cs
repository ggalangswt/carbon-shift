using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace CarbonShift;

public sealed record DatabaseSettings(string Host, int Port, string DatabaseName, string Username, string Password,
    string? OriginalConnectionString = null)
{
    public static DatabaseSettings Default => new("localhost", 5432, "carbonshift", "postgres", "");
    public static DatabaseSettings Parse(string value)
    {
        var b = new NpgsqlConnectionStringBuilder(value);
        return new(b.Host ?? "", b.Port, b.Database ?? "", b.Username ?? "", b.Password ?? "", value);
    }
    public string ToConnectionString()
    {
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Username))
            throw new ArgumentException("Host dan username PostgreSQL wajib diisi.");
        if (Port is < 1 or > 65535) throw new ArgumentException("Port harus antara 1 dan 65535.");
        if (string.IsNullOrWhiteSpace(DatabaseName) || DatabaseName.IndexOf('\0') >= 0 ||
            Encoding.UTF8.GetByteCount(DatabaseName) > 63 ||
            DatabaseName is "postgres" or "template0" or "template1")
            throw new ArgumentException("Gunakan nama database aplikasi (maksimal 63 byte), bukan postgres atau template.");
        var b = new NpgsqlConnectionStringBuilder(OriginalConnectionString ?? "")
        {
            Host = Host.Trim(), Port = Port, Database = DatabaseName.Trim(), Username = Username.Trim(),
            Password = Password, Timeout = 5, CommandTimeout = 15, IncludeErrorDetail = false
        };
        return b.ConnectionString;
    }
}

public sealed record DatabaseConfiguration(string ConnectionString, string Source, bool ManagedByEnvironment = false);

public sealed class DatabaseConfigurationStore
{
    private readonly string directory, legacyDirectory;
    private readonly Func<string?> environment;
    public string FilePath => Path.Combine(directory, "database.config");
    public DatabaseConfigurationStore(string? directory = null, string? legacyDirectory = null, Func<string?>? environment = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CarbonShift");
        this.legacyDirectory = legacyDirectory ?? AppContext.BaseDirectory;
        this.environment = environment ?? (() => Environment.GetEnvironmentVariable("CARBONSHIFT_DB"));
    }
    public DatabaseConfiguration? Resolve()
    {
        string? value = environment();
        if (!string.IsNullOrWhiteSpace(value)) return new(value.Trim(), "CARBONSHIFT_DB", true);
        if (File.Exists(FilePath)) return new(File.ReadAllText(FilePath).Trim(), "Pengaturan lokal Windows");
        string legacy = Path.Combine(legacyDirectory, "database.config");
        return File.Exists(legacy) ? new(File.ReadAllText(legacy).Trim(), "database.config lama") : null;
    }
    public async Task SaveAsync(string connectionString, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(environment()))
            throw new InvalidOperationException("Konfigurasi memakai CARBONSHIFT_DB. Ubah/hapus variabel tersebut sebelum memakai wizard.");
        _ = new NpgsqlConnectionStringBuilder(connectionString);
        Directory.CreateDirectory(directory);
        string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, connectionString, new UTF8Encoding(false), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, FilePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public enum DatabaseReadiness { Missing, Empty, Ready, Incompatible }
public sealed record DatabaseInspection(DatabaseReadiness State, string Message);

public sealed class DatabaseSetupService
{
    private sealed record Column(string Name, string Type, bool Nullable = false, int? Length = null,
        int? Precision = null, int? Scale = null, bool Identity = false);
    private static readonly Dictionary<string, Column[]> Columns = new()
    {
        ["user"] = [new("user_id", "int4", Identity: true), new("name", "varchar", Length:100), new("email", "varchar", Length:100),
            new("password_hash", "varchar", Length:255), new("created_at", "timestamp")],
        ["activity"] = [new("activity_id","int4",Identity:true), new("user_id","int4"), new("activity_name","varchar",Length:100),
            new("duration_minutes","int4"), new("power_consumption_kwh","numeric",Precision:6,Scale:2), new("created_at","timestamp")],
        ["weather_forecast"] = [new("forecast_id","int4",Identity:true), new("location","varchar",Length:100), new("forecast_date","date"),
            new("temperature","numeric",true,Precision:5,Scale:2), new("weather_condition","varchar",true,Length:50),
            new("solar_potential","numeric",true,Precision:5,Scale:2), new("retrieved_at","timestamp")],
        ["time_slot"] = [new("timeslot_id","int4",Identity:true), new("activity_id","int4"), new("forecast_id","int4"),
            new("start_time","timestamp"), new("end_time","timestamp"), new("green_score","numeric",true,Precision:5,Scale:2), new("created_at","timestamp")],
        ["recommendation"] = [new("recommendation_id","int4",Identity:true), new("timeslot_id","int4"), new("explanation","text",true),
            new("green_score","numeric",true,Precision:5,Scale:2), new("created_at","timestamp")]
    };

    private static readonly string[] Constraints =
    [
        "user|p|PRIMARY KEY (user_id)", "user|u|UNIQUE (email)",
        "activity|p|PRIMARY KEY (activity_id)", "activity|f|FOREIGN KEY (user_id) REFERENCES user(user_id)",
        "weather_forecast|p|PRIMARY KEY (forecast_id)",
        "time_slot|p|PRIMARY KEY (timeslot_id)", "time_slot|f|FOREIGN KEY (activity_id) REFERENCES activity(activity_id)",
        "time_slot|f|FOREIGN KEY (forecast_id) REFERENCES weather_forecast(forecast_id)",
        "recommendation|p|PRIMARY KEY (recommendation_id)", "recommendation|u|UNIQUE (timeslot_id)",
        "recommendation|f|FOREIGN KEY (timeslot_id) REFERENCES time_slot(timeslot_id)"
    ];
    private static string Normalize(string value) => Regex.Replace(value, "[\\s\\\"]", "").Replace("public.", "");

    public static string ReadSql(string resource)
    {
        using var stream = typeof(DatabaseSetupService).Assembly.GetManifestResourceStream("CarbonShift.Database." + resource)
            ?? throw new InvalidOperationException("Resource penyiapan database tidak tersedia.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async Task<DatabaseInspection> InspectAsync(DatabaseSettings settings, CancellationToken ct = default)
    {
        string connectionString = settings.ToConnectionString();
        await using var conn = new NpgsqlConnection(connectionString);
        try { await conn.OpenAsync(ct); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
            await using var server = new NpgsqlConnection(maintenance.ConnectionString);
            await server.OpenAsync(ct);
            return new(DatabaseReadiness.Missing, "Server terhubung. Database belum ada; Siapkan dan simpan akan membuatnya.");
        }
        return await InspectSchemaAsync(conn, null, ct);
    }

    private static async Task<DatabaseInspection> InspectSchemaAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var tables = new HashSet<string>();
        await using (var cmd = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE'", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
        if (tables.Count == 0)
        {
            // Do not call a schema empty just because the role cannot see somebody else's tables.
            await using var objects = new NpgsqlCommand("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','S','f')", conn, tx);
            if (Convert.ToInt32(await objects.ExecuteScalarAsync(ct)) != 0)
                return new(DatabaseReadiness.Incompatible, "Schema berisi objek yang tidak dapat dipakai. Pilih database kosong atau CarbonShift yang sesuai.");
            return new(DatabaseReadiness.Empty, "Database terhubung dan kosong. Kelima tabel CarbonShift akan dibuat.");
        }
        if (Columns.Keys.Any(t => !tables.Contains(t)))
            return new(DatabaseReadiness.Incompatible, "Schema parsial atau berisi tabel lain. Tidak ada perubahan dilakukan; pilih database baru atau schema CarbonShift lengkap.");

        foreach (var (table, expected) in Columns)
        {
            var actual = new List<Column>();
            await using var cmd = new NpgsqlCommand("""
                SELECT column_name,udt_name,is_nullable,character_maximum_length,numeric_precision,numeric_scale,is_identity,column_default
                FROM information_schema.columns WHERE table_schema='public' AND table_name=@table ORDER BY ordinal_position
                """, conn, tx);
            cmd.Parameters.AddWithValue("table", table);
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    string name = r.GetString(0);
                    if (name is "created_at" or "retrieved_at" && (r.IsDBNull(7) ||
                        !new[] { "CURRENT_TIMESTAMP", "now()", "transaction_timestamp()" }.Contains(r.GetString(7))))
                        return new(DatabaseReadiness.Incompatible, $"Default timestamp tabel {table} tidak sesuai. Database tidak diubah.");
                    actual.Add(new(r.GetString(0), r.GetString(1), r.GetString(2) == "YES",
                        r.IsDBNull(3) ? null : r.GetInt32(3),
                        r.GetString(1) == "numeric" && !r.IsDBNull(4) ? r.GetInt32(4) : null,
                        r.GetString(1) == "numeric" && !r.IsDBNull(5) ? r.GetInt32(5) : null, r.GetString(6) == "YES"));
                }
            }
            if (actual.Count != expected.Length || expected.Any(c => !actual.Contains(c)))
                return new(DatabaseReadiness.Incompatible, $"Kolom/tipe/nullability/identity tabel {table} tidak sesuai. Database tidak diubah.");
        }
        var constraints = new HashSet<string>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT t.relname,c.contype::text,pg_get_constraintdef(c.oid)
            FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid
            JOIN pg_namespace n ON n.oid=t.relnamespace
            WHERE n.nspname='public' AND t.relname=ANY(@tables) AND c.contype IN ('p','f','u')
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("tables", Columns.Keys.ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) constraints.Add(Normalize($"{r.GetString(0)}|{r.GetString(1)}|{r.GetString(2)}"));
        }
        if (!constraints.SetEquals(Constraints.Select(Normalize)))
            return new(DatabaseReadiness.Incompatible, "Primary key, foreign key, atau constraint unik tidak sesuai schema resmi. Database tidak diubah.");

        foreach (string table in Columns.Keys)
        foreach (string privilege in table == "user" ? new[] { "SELECT", "INSERT" } : new[] { "SELECT", "INSERT", "DELETE" })
        {
            await using var cmd = new NpgsqlCommand("SELECT has_table_privilege(current_user,@table,@privilege)", conn, tx);
            cmd.Parameters.AddWithValue("table", "public.\"" + table + "\""); cmd.Parameters.AddWithValue("privilege", privilege);
            if (!Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct)))
                return new(DatabaseReadiness.Incompatible, $"Akun PostgreSQL belum memiliki akses {privilege} pada tabel {table}.");
        }
        return new(DatabaseReadiness.Ready, "Database CarbonShift sesuai dan siap digunakan. Akun serta riwayat dipertahankan.");
    }

    public async Task<DatabaseInspection> PrepareAsync(DatabaseSettings settings, CancellationToken ct = default)
    {
        var initial = await InspectAsync(settings, ct);
        if (initial.State == DatabaseReadiness.Ready) return initial;
        if (initial.State == DatabaseReadiness.Incompatible) throw new InvalidOperationException(initial.Message);
        string value = settings.ToConnectionString();
        if (initial.State == DatabaseReadiness.Missing)
        {
            var maintenance = new NpgsqlConnectionStringBuilder(value) { Database = "postgres" };
            await using var server = new NpgsqlConnection(maintenance.ConnectionString);
            await server.OpenAsync(ct);
            string quoted = new NpgsqlCommandBuilder().QuoteIdentifier(settings.DatabaseName.Trim());
            await using var create = new NpgsqlCommand("CREATE DATABASE " + quoted, server);
            try { await create.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateDatabase) { }
        }
        await using var conn = new NpgsqlConnection(value);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('carbonshift_setup'))", conn, tx))
            await gate.ExecuteNonQueryAsync(ct);
        var current = await InspectSchemaAsync(conn, tx, ct);
        if (current.State == DatabaseReadiness.Empty)
        {
            await using var schema = new NpgsqlCommand(ReadSql("Schema.sql"), conn, tx);
            await schema.ExecuteNonQueryAsync(ct);
            await using var seed = new NpgsqlCommand(ReadSql("Seed.sql"), conn, tx);
            await seed.ExecuteNonQueryAsync(ct);
            current = await InspectSchemaAsync(conn, tx, ct);
        }
        if (current.State != DatabaseReadiness.Ready) throw new InvalidOperationException(current.Message);
        await tx.CommitAsync(ct);
        return current;
    }

    public static string FriendlyError(Exception error) => error switch
    {
        PostgresException { SqlState: PostgresErrorCodes.InvalidPassword } => "Username atau password PostgreSQL salah.",
        PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege } => "Hak akses PostgreSQL kurang. Database baru memerlukan CREATEDB; tabel baru memerlukan akses CREATE pada schema public.",
        PostgresException => "PostgreSQL menolak penyiapan. Periksa nama database, hak akses, dan schema target.",
        NpgsqlException or TimeoutException => "Tidak dapat terhubung ke PostgreSQL. Pastikan layanan aktif, host dan port benar.",
        IOException or UnauthorizedAccessException => "Pengaturan lokal tidak dapat disimpan. Periksa izin folder profil Windows, lalu coba lagi.",
        ArgumentException => "Isian koneksi tidak valid. Isi host, port, database aplikasi, username, dan password PostgreSQL.",
        _ => error is InvalidOperationException ? error.Message : "Penyiapan belum berhasil. Periksa koneksi dan coba lagi."
    };
}
