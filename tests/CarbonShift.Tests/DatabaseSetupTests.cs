using CarbonShift;
using Npgsql;

namespace CarbonShift.Tests;

public class DatabaseSetupTests
{
    [Fact] public async Task Configuration_precedence_and_atomic_save()
    {
        string root = Path.Combine(Path.GetTempPath(), "cs-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string user = Path.Combine(root, "user"), legacy = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacy);
        try
        {
            var settings = new DatabaseSettings("localhost", 5432, "carbonshift", "postgres", "a;password='quoted'");
            Assert.Equal(settings.Password, new NpgsqlConnectionStringBuilder(settings.ToConnectionString()).Password);
            string? environment = null;
            var store = new DatabaseConfigurationStore(user, legacy, () => environment);
            Assert.Null(store.Resolve());
            await File.WriteAllTextAsync(Path.Combine(legacy, "database.config"), "Host=legacy");
            Assert.Equal("Host=legacy", store.Resolve()!.ConnectionString);
            await store.SaveAsync(settings.ToConnectionString());
            Assert.Equal(settings.ToConnectionString(), new DatabaseConfigurationStore(user, legacy, () => null).Resolve()!.ConnectionString);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync("Host=replacement", cancellation.Token));
            Assert.Equal(settings.ToConnectionString(), store.Resolve()!.ConnectionString);
            environment = "Host=environment";
            Assert.True(store.Resolve()!.ManagedByEnvironment);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync("Host=ignored"));
            Assert.Equal("Host=environment", store.Resolve()!.ConnectionString);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("postgres")] [InlineData("template0")] [InlineData("template1")] [InlineData("")]
    public void Reserved_or_empty_database_names_are_rejected(string name) =>
        Assert.Throws<ArgumentException>(() => (DatabaseSettings.Default with { DatabaseName = name }).ToConnectionString());

    [DatabaseFact, Trait("Category", "Integration")]
    public async Task Setup_new_existing_empty_and_partial_databases_without_resetting_data()
    {
        var original = DatabaseSettings.Parse(Database.ConnectionString);
        string name = "cs_setup_" + Guid.NewGuid().ToString("N");
        var target = original with { DatabaseName = name };
        var service = new DatabaseSetupService();
        var maintenance = new NpgsqlConnectionStringBuilder(original.ToConnectionString()) { Database = "postgres" };
        await using var server = new NpgsqlConnection(maintenance.ConnectionString);
        await server.OpenAsync();
        try
        {
            Assert.Equal(DatabaseReadiness.Missing, (await service.InspectAsync(target)).State);
            Assert.Equal(DatabaseReadiness.Ready, (await service.PrepareAsync(target)).State);
            await using (var conn = new NpgsqlConnection(target.ToConnectionString()))
            {
                await conn.OpenAsync();
                await using var preserve = new NpgsqlCommand("INSERT INTO \"user\" (name,email,password_hash) VALUES ('Keep','keep@example.test','test')", conn);
                await preserve.ExecuteNonQueryAsync();
            }
            Assert.Equal(DatabaseReadiness.Ready, (await service.PrepareAsync(target)).State);
            await using (var conn = new NpgsqlConnection(target.ToConnectionString()))
            {
                await conn.OpenAsync();
                await using var count = new NpgsqlCommand("SELECT count(*) FROM \"user\"", conn);
                Assert.Equal(2, Convert.ToInt32(await count.ExecuteScalarAsync()));
                await using var alter = new NpgsqlCommand("ALTER TABLE recommendation DROP CONSTRAINT recommendation_timeslot_id_key", conn);
                await alter.ExecuteNonQueryAsync();
            }
            Assert.Equal(DatabaseReadiness.Incompatible, (await service.InspectAsync(target)).State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(target));
            await using (var conn = new NpgsqlConnection(target.ToConnectionString()))
            {
                await conn.OpenAsync();
                await using var empty = new NpgsqlCommand("DROP TABLE recommendation,time_slot,weather_forecast,activity,\"user\"; CREATE TABLE unrelated (id integer)", conn);
                await empty.ExecuteNonQueryAsync();
            }
            Assert.Equal(DatabaseReadiness.Incompatible, (await service.InspectAsync(target)).State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(target));
            await using (var conn = new NpgsqlConnection(target.ToConnectionString()))
            {
                await conn.OpenAsync();
                await using var empty = new NpgsqlCommand("DROP TABLE unrelated", conn);
                await empty.ExecuteNonQueryAsync();
            }
            Assert.Equal(DatabaseReadiness.Empty, (await service.InspectAsync(target)).State);
            Assert.Equal(DatabaseReadiness.Ready, (await service.PrepareAsync(target)).State);
            var wrong = target with { Password = Guid.NewGuid().ToString("N") };
            await Assert.ThrowsAnyAsync<NpgsqlException>(() => service.InspectAsync(wrong));
            var unreachable = target with { Port = 1 };
            await Assert.ThrowsAnyAsync<NpgsqlException>(() => service.InspectAsync(unreachable));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand("DROP DATABASE " + new NpgsqlCommandBuilder().QuoteIdentifier(name) + " WITH (FORCE)", server);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [DatabaseFact, Trait("Category", "Integration")]
    public async Task Existing_database_works_without_createdb_but_missing_privileges_are_rejected()
    {
        var owner = DatabaseSettings.Parse(Database.ConnectionString);
        string suffix = Guid.NewGuid().ToString("N");
        string db = "cs_rights_" + suffix, role = "cs_role_" + suffix, password = Guid.NewGuid().ToString("N");
        var target = owner with { DatabaseName = db };
        var service = new DatabaseSetupService();
        var maintenance = new NpgsqlConnectionStringBuilder(owner.ToConnectionString()) { Database = "postgres" };
        await using var server = new NpgsqlConnection(maintenance.ConnectionString); await server.OpenAsync();
        string quotedRole = new NpgsqlCommandBuilder().QuoteIdentifier(role);
        try
        {
            await service.PrepareAsync(target);
            // Identifiers and the generated hexadecimal-only fixture password are controlled by this test.
            await using (var create = new NpgsqlCommand("CREATE ROLE " + quotedRole + " LOGIN PASSWORD '" + password + "'", server))
                await create.ExecuteNonQueryAsync();
            var limited = target with { Username = role, Password = password };
            Assert.Equal(DatabaseReadiness.Incompatible, (await service.InspectAsync(limited)).State);
            await using (var conn = new NpgsqlConnection(target.ToConnectionString()))
            {
                await conn.OpenAsync();
                await using var grant = new NpgsqlCommand("GRANT USAGE ON SCHEMA public TO " + quotedRole +
                    "; GRANT SELECT,INSERT,DELETE ON ALL TABLES IN SCHEMA public TO " + quotedRole, conn);
                await grant.ExecuteNonQueryAsync();
            }
            Assert.Equal(DatabaseReadiness.Ready, (await service.PrepareAsync(limited)).State);
            await Assert.ThrowsAnyAsync<PostgresException>(() => service.PrepareAsync(limited with { DatabaseName = db + "_new" }));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS " + new NpgsqlCommandBuilder().QuoteIdentifier(db) +
                " WITH (FORCE); DROP ROLE IF EXISTS " + quotedRole, server);
            await drop.ExecuteNonQueryAsync();
        }
    }

}
