using CarbonShift;
using Npgsql;

namespace CarbonShift.Tests;

public class RegistrationTests
{
    [Theory]
    [InlineData("", "test@example.com", "password123", "password123", "name")]
    [InlineData("  ", "test@example.com", "password123", "password123", "name")]
    [InlineData("Nama", "bad", "password123", "password123", "email")]
    [InlineData("Nama", "", "password123", "password123", "email")]
    [InlineData("Nama", "test@example.com", "short", "short", "password")]
    [InlineData("Nama", "test@example.com", "        ", "        ", "password")]
    [InlineData("Nama", "test@example.com", "password123", "different", "confirmation")]
    public async Task Invalid_input_is_rejected_before_database_access(string name, string email, string password, string confirm, string field)
    {
        var error = await Assert.ThrowsAsync<AuthValidationException>(() => AuthService.RegisterAsync(name, email, password, confirm));
        Assert.Equal(field, error.Field);
    }

    [Theory]
    [InlineData("name")] [InlineData("email")] [InlineData("password")]
    public async Task Maximum_lengths_are_enforced(string field)
    {
        var error = await Assert.ThrowsAsync<AuthValidationException>(() => AuthService.RegisterAsync(
            field == "name" ? new string('n', 101) : "Nama",
            field == "email" ? new string('a', 90) + "@example.com" : "a@example.com",
            field == "password" ? new string('p', 129) : "password123", "password123"));
        Assert.Equal(field, error.Field);
    }

    [Fact]
    public void Hashes_are_salted_and_preserve_password_whitespace()
    {
        string password = " password123 ";
        string a = PasswordHasher.Hash(password), b = PasswordHasher.Hash(password);
        Assert.NotEqual(a, b); Assert.True(PasswordHasher.Verify(password, a));
        Assert.False(PasswordHasher.Verify(password.Trim(), a));
    }

    [DatabaseFact, Trait("Category", "Integration")]
    public async Task Registration_login_duplicates_and_history_remain_isolated()
    {
        string prefix = "register-" + Guid.NewGuid().ToString("N");
        string[] emails = [prefix + "-a@example.test", prefix + "-b@example.test", prefix + "-race@example.test", prefix + "-legacy@example.test"];
        var ids = new List<int>();
        await using var conn = await Database.OpenConnectionAsync();
        try
        {
            var a = await AuthService.RegisterAsync(" Akun A ", " " + emails[0].ToUpperInvariant() + " ", " password123 ", " password123 ");
            ids.Add(a.UserId);
            var b = await AuthService.RegisterAsync("Akun B", emails[1], "password456", "password456");
            ids.Add(b.UserId);
            var legacyAccount = await UserRepository.CreateAsync("Akun lama", emails[3], PasswordHasher.Hash("12345"));
            ids.Add(legacyAccount.UserId);
            Assert.Equal(legacyAccount.UserId, (await AuthService.LoginAsync(emails[3], "12345")).UserId);
            Assert.Equal("Akun A", a.Name); Assert.Equal(emails[0], a.Email);
            Assert.NotEqual(" password123 ", a.PasswordHash); Assert.True(PasswordHasher.Verify(" password123 ", a.PasswordHash));
            Assert.Empty(await RecommendationRepository.GetHistoryAsync(a.UserId));
            Assert.Equal(a.UserId, (await AuthService.LoginAsync(emails[0].ToUpperInvariant(), " password123 ")).UserId);
            await Assert.ThrowsAsync<ArgumentException>(() => AuthService.LoginAsync(emails[0], "wrong"));
            await Assert.ThrowsAsync<AuthValidationException>(() => AuthService.RegisterAsync("Duplicate", emails[0].ToUpperInvariant(), "password123", "password123"));
            // Existing mixed-case emails still work and cannot be registered again.
            await using (var legacy = new NpgsqlCommand("UPDATE \"user\" SET email=upper(email) WHERE user_id=@id", conn))
            { legacy.Parameters.AddWithValue("id", a.UserId); await legacy.ExecuteNonQueryAsync(); }
            Assert.Equal(a.UserId, (await AuthService.LoginAsync(emails[0], " password123 ")).UserId);
            await Assert.ThrowsAsync<AuthValidationException>(() => AuthService.RegisterAsync("Duplicate", emails[0], "password123", "password123"));

            var now = DateTimeOffset.UtcNow;
            var tomorrow = DateOnly.FromDateTime(Fixtures.Jogja.LocalNow(now).DateTime).AddDays(1);
            foreach (var user in new[] { a, b })
            {
                var request = Fixtures.Request(tomorrow) with { ActivityName = user.Name };
                await RecommendationRepository.SaveAsync(user.UserId,
                    RecommendationEngine.Generate(request, Fixtures.Forecast(now: now), now, new WeightedCalculator()));
            }
            var rowsA = await RecommendationRepository.GetHistoryAsync(a.UserId);
            var rowsB = await RecommendationRepository.GetHistoryAsync(b.UserId);
            Assert.Single(rowsA); Assert.Single(rowsB);
            Assert.Equal(a.Name, rowsA[0].Nama); Assert.Equal(b.Name, rowsB[0].Nama);
            Assert.DoesNotContain(b.Name, HistoryCsv.Export(rowsA)); Assert.DoesNotContain(a.Name, HistoryCsv.Export(rowsB));
            Assert.Equal(1, DashboardStatistics.Calculate(rowsA, tomorrow.ToDateTime(TimeOnly.MinValue), now).WeeklyCount);
            Assert.Equal(1, DashboardStatistics.Calculate(rowsB, tomorrow.ToDateTime(TimeOnly.MinValue), now).WeeklyCount);
            await RecommendationRepository.DeleteHistoryAsync(a.UserId);
            Assert.Empty(await RecommendationRepository.GetHistoryAsync(a.UserId));
            Assert.Single(await RecommendationRepository.GetHistoryAsync(b.UserId));

            async Task<Exception?> Race()
            {
                try { var user = await AuthService.RegisterAsync("Race", emails[2], "password123", "password123"); return null; }
                catch (AuthValidationException ex) { return ex; }
            }
            var attempts = await Task.WhenAll(Race(), Race());
            Assert.Single(attempts, e => e == null);
            Assert.Single(attempts.OfType<AuthValidationException>());
            await using var count = new NpgsqlCommand("SELECT count(*) FROM \"user\" WHERE email=@email", conn);
            count.Parameters.AddWithValue("email", emails[2]);
            Assert.Equal(1, Convert.ToInt32(await count.ExecuteScalarAsync()));
        }
        finally
        {
            // Cleanup includes any account committed just before an assertion/connection failure.
            await using var find = new NpgsqlCommand("SELECT user_id FROM \"user\" WHERE lower(email)=ANY(@emails)", conn);
            find.Parameters.AddWithValue("emails", emails);
            await using (var reader = await find.ExecuteReaderAsync())
                while (await reader.ReadAsync()) if (!ids.Contains(reader.GetInt32(0))) ids.Add(reader.GetInt32(0));
            foreach (int id in ids)
            {
                await RecommendationRepository.DeleteHistoryAsync(id);
                await using var delete = new NpgsqlCommand("DELETE FROM \"user\" WHERE user_id=@id", conn);
                delete.Parameters.AddWithValue("id", id); await delete.ExecuteNonQueryAsync();
            }
        }
    }
}
