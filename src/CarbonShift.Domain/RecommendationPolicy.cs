namespace CarbonShift;

public static class RecommendationPolicy
{
    public static void EnsureSaveable(RecommendationResult result, DateTimeOffset now)
    {
        if (result.Forecast.IsDemo) throw new InvalidOperationException("Data demo tidak boleh disimpan.");
        if (result.Best.Start < now) throw new InvalidOperationException("Waktu rekomendasi sudah lewat. Hitung ulang.");
        if (now < result.Forecast.FetchedAt || now - result.Forecast.FetchedAt > TimeSpan.FromHours(6))
            throw new InvalidOperationException("Forecast sudah kedaluwarsa. Ambil ulang dan hitung rekomendasi.");
    }
}

public static class DemoForecast
{
    public static ForecastSnapshot Create(Location location, DateTimeOffset now)
    {
        var today = location.LocalNow(now).Date;
        var data = Enumerable.Range(0, 72).Select(i =>
        {
            var start = today.AddHours(i);
            double radiation = Math.Max(0, Math.Sin((start.Hour - 6) / 12.0 * Math.PI)) * 850;
            return new WeatherForecast(location.At(start), 29, 25, radiation, start.Hour is >= 6 and < 18);
        }).ToArray();
        return new(location, now, data, ForecastSource.Demo, "Data buatan untuk mencoba alur. Hasil tidak dapat disimpan.");
    }
}
