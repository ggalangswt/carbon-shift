using CarbonShift;
using Npgsql;

namespace CarbonShift.Tests;

// Explicitly opt in with: dotnet test --filter Category=Integration
public class IntegrationTests
{
    [DatabaseFact, Trait("Category", "Integration")]
    public async Task Tomorrow_and_day_after_roundtrip_without_touching_existing_users_or_schema()
    {
        var fixtureUsers = new List<int>();
        await using var conn = await Database.OpenConnectionAsync();
        async Task<int> CreateUser()
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO \"user\" (name,email,password_hash) VALUES ('CarbonShift test',@email,@hash) RETURNING user_id", conn);
            cmd.Parameters.AddWithValue("email", "test-" + Guid.NewGuid().ToString("N") + "@carbonshift.test");
            cmd.Parameters.AddWithValue("hash", PasswordHasher.Hash("integration-only"));
            int id = Convert.ToInt32(await cmd.ExecuteScalarAsync()); fixtureUsers.Add(id); return id;
        }
        try
        {
            int first = await CreateUser(), second = await CreateUser();
            var now = DateTimeOffset.UtcNow; var forecast = Fixtures.Forecast(now: now);
            var today = DateOnly.FromDateTime(Fixtures.Jogja.LocalNow(now).DateTime);
            foreach (int day in new[] { 1, 2 })
            {
                var result = RecommendationEngine.Generate(Fixtures.Request(today.AddDays(day)), forecast, now, new WeightedCalculator());
                await RecommendationRepository.SaveAsync(first, result);
            }
            var loaded = await RecommendationRepository.GetHistoryAsync(first);
            Assert.Equal(2, loaded.Count); Assert.Empty(await RecommendationRepository.GetHistoryAsync(second));
            Assert.Contains(loaded, h => DateOnly.FromDateTime(h.Start) == today.AddDays(1));
            Assert.Contains(loaded, h => DateOnly.FromDateTime(h.Start) == today.AddDays(2));
            Assert.All(loaded, h => { Assert.Equal(.82, h.GreenScore, 2); Assert.Contains("Komponen (0-100)", h.Explanation); Assert.NotNull(h.StartInstant); });
            await using var verify = new NpgsqlCommand(
                "SELECT COUNT(*) FROM weather_forecast f JOIN time_slot t ON t.forecast_id=f.forecast_id JOIN activity a ON a.activity_id=t.activity_id WHERE a.user_id=@user AND f.forecast_date<>t.start_time::date", conn);
            verify.Parameters.AddWithValue("user", first);
            Assert.Equal(0, Convert.ToInt32(await verify.ExecuteScalarAsync()));
            // Re-read over a new connection (the application uses no in-memory history).
            Assert.Equal(2, (await RecommendationRepository.GetHistoryAsync(first)).Count);
            await RecommendationRepository.DeleteHistoryAsync(first);
            Assert.Empty(await RecommendationRepository.GetHistoryAsync(first));
        }
        finally
        {
            foreach (int id in fixtureUsers)
            {
                await RecommendationRepository.DeleteHistoryAsync(id);
                await using var delete = new NpgsqlCommand("DELETE FROM \"user\" WHERE user_id=@user", conn);
                delete.Parameters.AddWithValue("user", id); await delete.ExecuteNonQueryAsync();
            }
        }
    }
}
