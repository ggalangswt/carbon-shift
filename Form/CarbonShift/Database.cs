using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;

namespace CarbonShift;

public static class Database
{
    public static string ConnectionString
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("CARBONSHIFT_DB");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            string path = Path.Combine(AppContext.BaseDirectory, "database.config");
            if (File.Exists(path) && !string.IsNullOrWhiteSpace(File.ReadAllText(path))) return File.ReadAllText(path).Trim();
            throw new InvalidOperationException("Atur CARBONSHIFT_DB atau database.config sesuai database.config.example.");
        }
    }
    public static async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(ConnectionString);
        try { await conn.OpenAsync(ct); return conn; }
        catch { await conn.DisposeAsync(); throw; }
    }
}

public static class PasswordHasher
{
    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100000, HashAlgorithmName.SHA256, 32);
        return string.Join("$", "pbkdf2-sha256", "100000", Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }
    public static bool Verify(string password, string stored)
    {
        try
        {
            string[] parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" ||
                !int.TryParse(parts[1], out int iterations) || iterations is < 1 or > 1000000) return false;
            byte[] salt = Convert.FromBase64String(parts[2]), hash = Convert.FromBase64String(parts[3]);
            if (salt.Length != 16 || hash.Length != 32) return false;
            byte[] input = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, hash.Length);
            return CryptographicOperations.FixedTimeEquals(hash, input);
        }
        catch (FormatException) { return false; }
    }
}

public sealed record UserAccount(int UserId, string Name, string Email, string PasswordHash);

public static class UserRepository
{
    public static async Task<UserAccount?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        await using var conn = await Database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT user_id, name, email, password_hash FROM \"user\" WHERE email = @email", conn);
        cmd.Parameters.AddWithValue("email", email);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)) : null;
    }
}

public static class RecommendationRepository
{
    public static async Task<int> SaveAsync(int userId, RecommendationResult result, CancellationToken ct = default)
    {
        RecommendationPolicy.EnsureSaveable(result, DateTimeOffset.UtcNow);
        var request = result.Request;
        var forecast = result.Forecast;
        await using var conn = await Database.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        async Task<int> Insert(string sql, params (string Name, object Value)[] parameters)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        int activityId = await Insert(
            "INSERT INTO activity (user_id, activity_name, duration_minutes, power_consumption_kwh) " +
            "VALUES (@user,@name,@duration,@kwh) RETURNING activity_id",
            ("user", userId), ("name", request.ActivityName), ("duration", request.DurationHours * 60),
            ("kwh", Math.Round(request.EnergyKwh, 2)));
        var day = forecast.Hours.Where(h => DateOnly.FromDateTime(h.Start.DateTime) == request.Date && h.IsValid).ToArray();
        var daylight = day.Where(h => h.IsDay).ToArray();
        var summary = daylight.Length == 0 ? day : daylight;
        double cloud = summary.Average(h => h.TutupanAwan);
        int forecastId = await Insert(
            "INSERT INTO weather_forecast (location,forecast_date,temperature,weather_condition,solar_potential,retrieved_at) " +
            "VALUES (@location,@date,@temp,@condition,@solar,@fetched) RETURNING forecast_id",
            ("location", forecast.Location.DatabaseLabel), ("date", request.Date),
            ("temp", Math.Round((decimal)summary.Average(h => h.Temperature), 2)),
            ("condition", daylight.Length == 0 ? "Malam" : cloud < 25 ? "Cerah" : cloud < 60 ? "Berawan sebagian" : "Berawan"),
            ("solar", Scale(Math.Clamp(summary.Average(h => h.Radiasi) / 900, 0, 1))),
            ("fetched", forecast.Location.LocalNow(forecast.FetchedAt).DateTime));
        int bestId = 0;
        foreach (var slot in result.Slots)
        {
            int id = await Insert(
                "INSERT INTO time_slot (activity_id,forecast_id,start_time,end_time,green_score) " +
                "VALUES (@activity,@forecast,@start,@end,@score) RETURNING timeslot_id",
                ("activity", activityId), ("forecast", forecastId), ("start", slot.Start.DateTime),
                ("end", slot.End.DateTime), ("score", Scale(slot.GreenScore)));
            if (slot == result.Best) bestId = id;
        }
        int recommendationId = await Insert(
            "INSERT INTO recommendation (timeslot_id,explanation,green_score) VALUES (@slot,@explanation,@score) RETURNING recommendation_id",
            ("slot", bestId), ("explanation", result.Explanation), ("score", Scale(result.Best.GreenScore)));
        RecommendationPolicy.EnsureSaveable(result, DateTimeOffset.UtcNow);
        await tx.CommitAsync(ct);
        return recommendationId;
    }

    public static async Task<IReadOnlyList<ActivityRecord>> GetHistoryAsync(int userId, CancellationToken ct = default)
    {
        await using var conn = await Database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT r.recommendation_id,a.activity_name,a.duration_minutes,a.power_consumption_kwh,
                   t.start_time,t.end_time,r.green_score,r.created_at,f.location,r.explanation
            FROM activity a JOIN time_slot t ON t.activity_id=a.activity_id
            JOIN recommendation r ON r.timeslot_id=t.timeslot_id
            JOIN weather_forecast f ON f.forecast_id=t.forecast_id
            WHERE a.user_id=@user ORDER BY t.start_time DESC,r.recommendation_id DESC
            """, conn);
        cmd.Parameters.AddWithValue("user", userId);
        var rows = new List<ActivityRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetDecimal(3),
                reader.GetDateTime(4), reader.GetDateTime(5), reader.IsDBNull(6) ? 0 : (double)reader.GetDecimal(6) / 100,
                reader.GetDateTime(7), reader.GetString(8),
                reader.IsDBNull(9) ? "Penjelasan tidak tersedia untuk data lama." : reader.GetString(9)));
        return rows.AsReadOnly();
    }

    public static async Task DeleteHistoryAsync(int userId, CancellationToken ct = default)
    {
        await using var conn = await Database.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var find = new NpgsqlCommand(
            "SELECT DISTINCT t.forecast_id FROM time_slot t JOIN activity a ON a.activity_id=t.activity_id WHERE a.user_id=@user", conn, tx);
        find.Parameters.AddWithValue("user", userId);
        var ids = new List<int>();
        await using (var reader = await find.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetInt32(0));
        string[] statements =
        [
            "DELETE FROM recommendation WHERE timeslot_id IN (SELECT t.timeslot_id FROM time_slot t JOIN activity a ON a.activity_id=t.activity_id WHERE a.user_id=@user)",
            "DELETE FROM time_slot WHERE activity_id IN (SELECT activity_id FROM activity WHERE user_id=@user)",
            "DELETE FROM activity WHERE user_id=@user",
            "DELETE FROM weather_forecast f WHERE forecast_id=ANY(@ids) AND NOT EXISTS (SELECT 1 FROM time_slot t WHERE t.forecast_id=f.forecast_id)"
        ];
        foreach (string sql in statements)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("user", userId);
            cmd.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Integer, ids.ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
    private static decimal Scale(double score) => Math.Round((decimal)(score * 100), 2);
}

public sealed record DashboardStatistics(int WeeklyCount, double? AverageScore, ActivityRecord? Next)
{
    public static DashboardStatistics Calculate(IReadOnlyList<ActivityRecord> history, DateTime localToday, DateTimeOffset now)
    {
        var monday = localToday.Date.AddDays(-(((int)localToday.DayOfWeek + 6) % 7));
        var week = history.Where(h => h.Start >= monday && h.Start < monday.AddDays(7)).ToArray();
        return new(week.Length, week.Length == 0 ? null : week.Average(h => h.GreenScore),
            history.Where(h => h.StartInstant >= now).OrderBy(h => h.StartInstant).FirstOrDefault());
    }
}
