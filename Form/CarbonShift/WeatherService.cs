using System.Globalization;
using System.Net;
using System.Text.Json;

namespace CarbonShift;

public interface ILocalStore
{
    Task<T?> ReadAsync<T>(string key, CancellationToken ct = default);
    Task WriteAsync<T>(string key, T value, CancellationToken ct = default);
}

public sealed class LocalStore : ILocalStore
{
    private readonly string root;
    public LocalStore(string? directory = null) =>
        root = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CarbonShift");
    private string PathFor(string key) => Path.Combine(root, key + ".json");
    public async Task<T?> ReadAsync<T>(string key, CancellationToken ct = default)
    {
        try
        {
            using var stream = File.OpenRead(PathFor(key));
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return default; }
    }
    public async Task WriteAsync<T>(string key, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(root);
        string path = PathFor(key), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value), ct);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class ApiClient
{
    private readonly HttpClient http;
    public ApiClient(HttpClient? client = null) => http = client ?? Shared;
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(10) };
    public async Task<string> GetAsync(string url, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using var response = await http.GetAsync(url, timeout.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt == 0 &&
                (ex is OperationCanceledException || ex is HttpRequestException h &&
                (h.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)h.StatusCode >= 500)))
            {
                await Task.Delay(150, ct);
            }
        }
    }
}

public sealed class LocationSearchService(ApiClient api)
{
    public async Task<IReadOnlyList<Location>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (query.Trim().Length < 2) throw new ArgumentException("Masukkan minimal dua karakter nama kota.");
        string json = await api.GetAsync("https://geocoding-api.open-meteo.com/v1/search?name=" +
            Uri.EscapeDataString(query.Trim()) + "&count=10&language=id&countryCode=ID", ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results)) return [];
        var locations = new List<Location>();
        foreach (var item in results.EnumerateArray())
        {
            if (!item.TryGetProperty("country_code", out var country) || country.GetString() != "ID") continue;
            string? zone = item.TryGetProperty("timezone", out var z) ? z.GetString() : null;
            if (zone is not ("Asia/Jakarta" or "Asia/Makassar" or "Asia/Jayapura")) continue;
            locations.Add(new(item.GetProperty("name").GetString() ?? "Kota",
                item.TryGetProperty("admin1", out var province) ? province.GetString() ?? "" : "",
                item.GetProperty("latitude").GetDouble(), item.GetProperty("longitude").GetDouble(), zone));
        }
        return locations.AsReadOnly();
    }
}

public sealed class WeatherService
{
    private readonly ApiClient api;
    private readonly ILocalStore store;
    private readonly Func<DateTimeOffset> clock;
    public WeatherService(ApiClient api, ILocalStore store, Func<DateTimeOffset>? clock = null)
    { this.api = api; this.store = store; this.clock = clock ?? (() => DateTimeOffset.UtcNow); }

    public static bool UsableCache(ForecastSnapshot cache, Location location, DateTimeOffset now, TimeSpan maxAge)
    {
        if (cache.Location != location || cache.IsDemo || now < cache.FetchedAt || now - cache.FetchedAt > maxAge) return false;
        var today = DateOnly.FromDateTime(location.LocalNow(now).DateTime);
        // A previous day's cache must not pretend to cover all three new calendar days.
        var localNow = location.LocalNow(now);
        bool noWholeHourToday = localNow.Hour == 23 && localNow > location.At(localNow.Date.AddHours(23));
        return Enumerable.Range(0, 3).Where(day => day != 0 || !noWholeHourToday)
            .All(day => cache.Hours.Any(h => h.IsValid && h.Start >= now &&
                DateOnly.FromDateTime(h.Start.DateTime) == today.AddDays(day)));
    }

    public async Task<ForecastSnapshot> GetForecastAsync(Location location, bool force = false, CancellationToken ct = default)
    {
        if (!location.IsValid) throw new ArgumentException("Lokasi Indonesia tidak valid.");
        string key = "forecast_" + location.CacheKey;
        var now = clock();
        var cached = await store.ReadAsync<ForecastSnapshot>(key, ct);
        if (!force && cached != null && UsableCache(cached, location, now, TimeSpan.FromHours(1)))
            return cached.WithSource(ForecastSource.Cache);
        try
        {
            // Four API days supply the final 00:00 point needed for the last interval of day three.
            string url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={location.Latitude}&longitude={location.Longitude}&hourly=temperature_2m,cloud_cover,shortwave_radiation,is_day&forecast_days=4&timezone={Uri.EscapeDataString(location.TimeZoneId)}");
            var snapshot = Parse(await api.GetAsync(url, ct), location, clock());
            try { await store.WriteAsync(key, snapshot, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                snapshot = snapshot.WithSource(ForecastSource.Live, "Forecast live tersedia, tetapi cache tidak dapat disimpan.");
            }
            return snapshot;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            now = clock();
            if (cached != null && UsableCache(cached, location, now, TimeSpan.FromHours(6)))
                return cached.WithSource(ForecastSource.StaleCache,
                    "API gagal. Memakai cache maksimal 6 jam; prakiraan mungkin sudah berubah.");
            throw new InvalidOperationException("Forecast tidak tersedia dan tidak ada cache yang masih sesuai. Coba ambil ulang.", ex);
        }
    }

    public static ForecastSnapshot Parse(string json, Location location, DateTimeOffset fetchedAt)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("hourly", out var hourly)) throw new InvalidDataException("Respons tidak memiliki forecast per jam.");
        string[] names = ["time", "temperature_2m", "cloud_cover", "shortwave_radiation", "is_day"];
        var arrays = names.Select(name => hourly.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a : throw new InvalidDataException("Data forecast tidak lengkap: " + name)).ToArray();
        int count = arrays[0].GetArrayLength();
        if (count < 2 || arrays.Any(a => a.GetArrayLength() != count))
            throw new InvalidDataException("Panjang array forecast tidak sesuai.");
        var today = DateOnly.FromDateTime(location.LocalNow(fetchedAt).DateTime);
        var hours = new List<WeatherForecast>();
        int missing = 0;
        for (int i = 0; i < count - 1; i++)
        {
            if (!DateTime.TryParse(arrays[0][i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ||
                !DateTime.TryParse(arrays[0][i + 1].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var next) ||
                next - time != TimeSpan.FromHours(1))
            { missing++; continue; }
            var date = DateOnly.FromDateTime(time);
            if (date < today || date > today.AddDays(2)) continue;
            // cloud/temp/is_day describe t; radiation at t+1 describes [t,t+1).
            if (!Number(arrays[1][i], out double temp) || !Number(arrays[2][i], out double cloud) ||
                !Number(arrays[3][i + 1], out double radiation) ||
                arrays[4][i].ValueKind != JsonValueKind.Number || !arrays[4][i].TryGetInt32(out int day) || day is < 0 or > 1)
            { missing++; continue; }
            var item = new WeatherForecast(location.At(time), temp, cloud, radiation, day == 1);
            if (item.IsValid) hours.Add(item); else missing++;
        }
        if (hours.Count == 0) throw new InvalidDataException("Tidak ada interval forecast yang lengkap.");
        if (hours.GroupBy(h => h.Start).Any(g => g.Count() != 1))
            throw new InvalidDataException("Timestamp forecast duplikat.");
        return new(location, fetchedAt, hours, ForecastSource.Live,
            missing > 0 ? $"{missing} interval tidak lengkap diabaikan; hanya slot dengan data lengkap yang digunakan." : null);
    }
    private static bool Number(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number);
    }
    public static ForecastSnapshot Demo(Location location, DateTimeOffset now)
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
