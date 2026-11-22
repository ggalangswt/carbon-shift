using System.Net;
using System.Text.Json;
using CarbonShift;

namespace CarbonShift.Tests;

public sealed class MemoryStore : ILocalStore
{
    private readonly Dictionary<string, string> items = [];
    public Task<T?> ReadAsync<T>(string key, CancellationToken ct = default) =>
        Task.FromResult(items.TryGetValue(key, out var text) ? JsonSerializer.Deserialize<T>(text) : default);
    public Task WriteAsync<T>(string key, T value, CancellationToken ct = default)
    { items[key] = JsonSerializer.Serialize(value); return Task.CompletedTask; }
}

public sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(response(request, ++Calls));
    }
}

public class WeatherTests
{
    public static string Payload(string? missing = null, DateTimeOffset? now = null)
    {
        var midnight = Fixtures.Jogja.LocalNow(now ?? Fixtures.Now).Date;
        var hours = new Dictionary<string, object?[]>
        {
            ["time"] = Enumerable.Range(0, 73).Select(i => (object?)midnight.AddHours(i).ToString("yyyy-MM-ddTHH:mm")).ToArray(),
            ["temperature_2m"] = Enumerable.Repeat<object?>(29, 73).ToArray(),
            ["cloud_cover"] = Enumerable.Repeat<object?>(20, 73).ToArray(),
            ["shortwave_radiation"] = Enumerable.Range(0, 73).Select(i => (object?)(i * 10)).ToArray(),
            ["is_day"] = Enumerable.Repeat<object?>(1, 73).ToArray()
        };
        if (missing != null) hours[missing][12] = null;
        return JsonSerializer.Serialize(new { hourly = hours });
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    [Fact] public void Radiation_at_t_plus_one_is_assigned_to_interval_t()
    {
        var f = WeatherService.Parse(Payload(), Fixtures.Jogja, Fixtures.Now);
        Assert.Equal(72, f.Hours.Count);
        Assert.Equal(10, f.Hours[0].Radiasi);
        Assert.Equal(720, f.Hours[^1].Radiasi);
        Assert.Equal(23, f.Hours[^1].Start.Hour);
        Assert.Equal(new DateOnly(2026, 11, 24), DateOnly.FromDateTime(f.Hours[^1].Start.DateTime));
    }
    [Theory]
    [InlineData("temperature_2m")] [InlineData("cloud_cover")] [InlineData("is_day")] [InlineData("shortwave_radiation")]
    public void Null_data_excludes_interval_instead_of_inventing_zero(string variable)
    {
        var f = WeatherService.Parse(Payload(variable), Fixtures.Jogja, Fixtures.Now);
        Assert.Equal(71, f.Hours.Count);
        Assert.NotNull(f.Warning);
        int missingHour = variable == "shortwave_radiation" ? 11 : 12;
        Assert.DoesNotContain(f.Hours, h => h.Start.Date == new DateTime(2026, 11, 22) && h.Start.Hour == missingHour);
    }
    [Fact] public void Inconsistent_arrays_are_rejected() =>
        Assert.Throws<InvalidDataException>(() => WeatherService.Parse(
            """{"hourly":{"time":["2026-11-22T00:00"],"cloud_cover":[],"temperature_2m":[],"shortwave_radiation":[],"is_day":[]}}""",
            Fixtures.Jogja, Fixtures.Now));
    [Fact] public async Task Fresh_cache_skips_network_and_roundtrips_immutable_snapshot()
    {
        var store = new MemoryStore();
        await store.WriteAsync("forecast_" + Fixtures.Jogja.CacheKey, Fixtures.Forecast());
        var handler = new FakeHandler((_, _) => throw new Exception("Network should not run"));
        var weather = new WeatherService(new(new HttpClient(handler)), store, () => Fixtures.Now.AddMinutes(30));
        var cached = await weather.GetForecastAsync(Fixtures.Jogja);
        Assert.Equal(ForecastSource.Cache, cached.Source);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(72, cached.Hours.Count);
    }
    [Fact] public async Task Force_refresh_bypasses_cache_and_retries_once_on_server_failure()
    {
        var store = new MemoryStore(); await store.WriteAsync("forecast_" + Fixtures.Jogja.CacheKey, Fixtures.Forecast());
        var handler = new FakeHandler((_, call) => call == 1 ? new(HttpStatusCode.ServiceUnavailable) : Json(Payload()));
        var weather = new WeatherService(new(new HttpClient(handler)), store, () => Fixtures.Now);
        var result = await weather.GetForecastAsync(Fixtures.Jogja, true);
        Assert.Equal(2, handler.Calls); Assert.Equal(ForecastSource.Live, result.Source);
    }
    [Theory]
    [InlineData(2, true)] [InlineData(6, true)] [InlineData(7, false)]
    public async Task Api_failure_only_falls_back_to_matching_cache_up_to_six_hours(int age, bool allowed)
    {
        var store = new MemoryStore();
        var cached = Fixtures.Forecast();
        await store.WriteAsync("forecast_" + Fixtures.Jogja.CacheKey,
            new ForecastSnapshot(cached.Location, Fixtures.Now.AddHours(-age), cached.Hours, ForecastSource.Live));
        var handler = new FakeHandler((_, _) => new(HttpStatusCode.ServiceUnavailable));
        var weather = new WeatherService(new(new HttpClient(handler)), store, () => Fixtures.Now);
        if (allowed) Assert.Equal(ForecastSource.StaleCache, (await weather.GetForecastAsync(Fixtures.Jogja)).Source);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => weather.GetForecastAsync(Fixtures.Jogja));
        Assert.Equal(2, handler.Calls);
    }
    [Fact] public async Task Permanent_http_error_does_not_retry()
    {
        var handler = new FakeHandler((_, _) => new(HttpStatusCode.BadRequest));
        var weather = new WeatherService(new(new HttpClient(handler)), new MemoryStore(), () => Fixtures.Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => weather.GetForecastAsync(Fixtures.Jogja));
        Assert.Equal(1, handler.Calls);
    }
    [Fact] public async Task Timeout_retries_once_then_reports_no_cache()
    {
        var handler = new FakeHandler((_, _) => throw new TaskCanceledException("timeout"));
        var weather = new WeatherService(new(new HttpClient(handler)), new MemoryStore(), () => Fixtures.Now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => weather.GetForecastAsync(Fixtures.Jogja));
        Assert.Equal(2, handler.Calls);
    }
    [Fact] public async Task Cancellation_is_not_converted_to_cache_fallback()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var handler = new FakeHandler((_, _) => Json(Payload()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ApiClient(new HttpClient(handler)).GetAsync("https://example.test", cts.Token));
        Assert.Equal(0, handler.Calls);
    }
    [Fact] public void Cache_for_other_location_demo_and_previous_horizon_are_rejected()
    {
        var f = Fixtures.Forecast();
        Assert.False(WeatherService.UsableCache(f, Fixtures.Jogja with { Name = "Other" }, Fixtures.Now, TimeSpan.FromHours(6)));
        Assert.False(WeatherService.UsableCache(f.WithSource(ForecastSource.Demo), Fixtures.Jogja, Fixtures.Now, TimeSpan.FromHours(6)));
        Assert.False(WeatherService.UsableCache(f, Fixtures.Jogja, Fixtures.Now.AddDays(1), TimeSpan.FromDays(2)));
    }
    [Fact] public async Task Geocoding_handles_empty_and_only_returns_indonesian_locations()
    {
        var handler = new FakeHandler((request, _) =>
        {
            Assert.Contains("countryCode=ID", request.RequestUri!.Query);
            return Json("""{"results":[{"name":"Yogyakarta","admin1":"DIY","country_code":"ID","latitude":-7.8,"longitude":110.36,"timezone":"Asia/Jakarta"},{"name":"Other","country_code":"US"}]}""");
        });
        var locations = await new LocationSearchService(new(new HttpClient(handler))).SearchAsync("Yogyakarta");
        Assert.Single(locations); Assert.Equal("Yogyakarta, DIY", locations[0].DisplayName);
        var empty = new LocationSearchService(new(new HttpClient(new FakeHandler((_, _) => Json("{}")))));
        Assert.Empty(await empty.SearchAsync("Tidakada"));
    }
    [Fact] public async Task File_store_preserves_per_user_settings()
    {
        string root = Path.Combine(Path.GetTempPath(), "carbonshift-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalStore(root);
            await store.WriteAsync("location_user_1", Fixtures.Jogja);
            var second = Fixtures.Jogja with { Name = "Makassar", TimeZoneId = "Asia/Makassar" };
            await store.WriteAsync("location_user_2", second);
            Assert.Equal(Fixtures.Jogja, await new LocalStore(root).ReadAsync<Location>("location_user_1"));
            Assert.Equal(second, await store.ReadAsync<Location>("location_user_2"));
            await File.WriteAllTextAsync(Path.Combine(root, "broken.json"), "{");
            Assert.Null(await store.ReadAsync<Location>("broken"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact] public void Cache_still_serves_tomorrow_after_last_start_today()
    {
        var late = Fixtures.Jogja.At(new(2026, 11, 22, 23, 30, 0));
        var f = Fixtures.Forecast(now: late);
        Assert.True(WeatherService.UsableCache(f, Fixtures.Jogja, late, TimeSpan.FromHours(1)));
    }

    [LiveFact, Trait("Category", "Live")]
    public async Task Real_geocoding_and_forecast_return_current_three_day_intervals()
    {
        var api = new ApiClient();
        var locations = await new LocationSearchService(api).SearchAsync("Yogyakarta");
        Assert.NotEmpty(locations);
        var snapshot = await new WeatherService(api, new MemoryStore()).GetForecastAsync(locations[0]);
        Assert.Equal(72, snapshot.Hours.Count);
        Assert.All(snapshot.Hours, h => Assert.True(h.IsValid));
        Assert.Equal(3, snapshot.Hours.Select(h => h.Start.Date).Distinct().Count());
    }
}
