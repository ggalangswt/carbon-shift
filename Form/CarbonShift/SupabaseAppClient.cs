using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

namespace CarbonShift;

public sealed class CarbonShiftApiException(string message, HttpStatusCode status, string? field = null) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public string? Field { get; } = field;
}
public sealed record SupabasePublicSettings(string Url, string PublishableKey)
{
    public bool Valid
    {
        get
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
            if (string.IsNullOrWhiteSpace(PublishableKey)) return false;
            if (PublishableKey.StartsWith("sb_publishable_", StringComparison.Ordinal)) return PublishableKey.Length > 20;
            // Legacy anon JWTs are accepted, secret/service-role keys are never accepted.
            try
            {
                string part = PublishableKey.Split('.')[1].Replace('-', '+').Replace('_', '/');
                using var doc = JsonDocument.Parse(Convert.FromBase64String(part.PadRight((part.Length + 3) / 4 * 4, '=')));
                return doc.RootElement.GetProperty("role").GetString() == "anon";
            }
            catch { return false; }
        }
    }
    public static SupabasePublicSettings Load()
    {
        var metadata = typeof(SupabaseAppClient).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
        string Read(string key) => metadata.FirstOrDefault(a => a.Key == key)?.Value ?? "";
        string path = Path.Combine(AppContext.BaseDirectory, "supabase.public.json");
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<SupabasePublicSettings>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new("", "");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new(Read("CarbonShift.SupabaseUrl"), Read("CarbonShift.SupabasePublishableKey"));
    }
}
public sealed class SupabaseAppClient : ICarbonShiftClient, IDisposable
{
    private readonly HttpClient http;
    private readonly SupabasePublicSettings settings;
    private readonly WeatherService weather;
    private readonly LocationSearchService search;
    private readonly ConcurrentDictionary<Guid, ForecastSnapshot> forecasts = new();
    private string? token;
    private DateTimeOffset expires;
    private int generation;
    public event EventHandler? SessionExpired;
    public static Uri DefaultAddress => Uri.TryCreate(SupabasePublicSettings.Load().Url, UriKind.Absolute, out var uri) ? uri : new("https://unconfigured.invalid/");
    public SupabaseAppClient(HttpClient? client = null, SupabasePublicSettings? settings = null, WeatherService? weather = null)
    {
        this.settings = settings ?? SupabasePublicSettings.Load();
        http = client ?? new HttpClient { BaseAddress = this.settings.Valid ? new Uri(this.settings.Url) : new Uri("https://unconfigured.invalid/"), Timeout = TimeSpan.FromSeconds(60) };
        var upstream = new ApiClient(); this.weather = weather ?? new WeatherService(upstream, new LocalStore()); search = new(upstream);
    }
    private async Task<T?> SendAsync<T>(string path, object body, bool authenticated, CancellationToken ct)
    {
        if (!settings.Valid) throw new CarbonShiftApiException("Layanan belum siap. Hubungi pengelola aplikasi.", HttpStatusCode.ServiceUnavailable);
        int current = generation;
        if (authenticated && (token == null || DateTimeOffset.UtcNow >= expires))
        {
            Expire(); throw new CarbonShiftApiException("Sesi berakhir. Silakan masuk kembali.", HttpStatusCode.Unauthorized);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("apikey", settings.PublishableKey);
        if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (generation != current) throw new OperationCanceledException("Sesi telah berubah.");
            if (!response.IsSuccessStatusCode)
            {
                string? field = null; string? message = null; string? code = null;
                try
                {
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    if (json.RootElement.TryGetProperty("field", out var f)) field = f.GetString();
                    if (json.RootElement.TryGetProperty("code", out var c)) code = c.GetString();
                    // Only friendly Edge Function messages may reach the UI. PostgREST SQL errors remain private.
                    if (path.StartsWith("functions/", StringComparison.Ordinal) && json.RootElement.TryGetProperty("message", out var m)) message = m.GetString();
                }
                catch (JsonException) { }
                if (response.StatusCode == HttpStatusCode.Unauthorized && authenticated) Expire();
                var status = code == "P0002" ? HttpStatusCode.Conflict : response.StatusCode;
                string fallback = status switch
                {
                    HttpStatusCode.Unauthorized => "Sesi berakhir. Silakan masuk kembali.",
                    HttpStatusCode.TooManyRequests => "Terlalu banyak percobaan. Tunggu sebentar lalu coba lagi.",
                    HttpStatusCode.Conflict => "Hasil rekomendasi tidak tersedia. Hitung ulang.",
                    _ => "Layanan belum dapat dihubungi. Coba lagi."
                };
                throw new CarbonShiftApiException(message ?? fallback, status, field);
            }
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (generation != current) throw new OperationCanceledException("Sesi telah berubah.");
            if (string.IsNullOrWhiteSpace(text) || text == "null" || response.StatusCode == HttpStatusCode.NoContent) return default;
            return JsonSerializer.Deserialize<T>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException();
        }
        catch (HttpRequestException) { throw new CarbonShiftApiException("Layanan belum dapat dihubungi. Periksa internet lalu coba lagi.", HttpStatusCode.ServiceUnavailable); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CarbonShiftApiException("Permintaan belum selesai. Coba lagi atau periksa Riwayat sebelum menyimpan ulang.", HttpStatusCode.RequestTimeout); }
        catch (JsonException) { throw new CarbonShiftApiException("Respons layanan belum dapat dibaca. Coba lagi.", HttpStatusCode.BadGateway); }
    }
    private async Task<UserAccount> Authenticate(object request, CancellationToken ct)
    {
        Logout();
        int current = generation;
        var response = await SendAsync<AuthResponse>("functions/v1/carbonshift-auth", request, false, ct) ?? throw new JsonException();
        if (generation != current) throw new OperationCanceledException();
        token = response.AccessToken; expires = response.ExpiresAt;
        return response.User;
    }
    public Task<UserAccount> LoginAsync(string email, string password, CancellationToken ct = default) => Authenticate(new { action = "login", email, password }, ct);
    public Task<UserAccount> RegisterAsync(string name, string email, string password, string confirmation, CancellationToken ct = default) =>
        Authenticate(new { action = "register", name, email, password, confirmation }, ct);
    public Task<IReadOnlyList<Location>> SearchAsync(string query, CancellationToken ct = default) => search.SearchAsync(query, ct);
    public async Task<ForecastResponse> ForecastAsync(Location location, bool force, CancellationToken ct = default)
    {
        int current = generation;
        var value = await weather.GetForecastAsync(location, force, ct);
        if (generation != current) throw new OperationCanceledException();
        forecasts.Clear(); var id = Guid.NewGuid(); forecasts[id] = value;
        return new(id, value);
    }
    public async Task<PreviewResponse> PreviewAsync(Guid forecastId, ScheduleRequest request, string method, CancellationToken ct = default)
    {
        if (!forecasts.TryGetValue(forecastId, out var forecast)) throw new CarbonShiftApiException("Ambil ulang forecast terlebih dahulu.", HttpStatusCode.Conflict);
        return await SendAsync<PreviewResponse>("functions/v1/carbonshift/recommendations/preview", new { location = forecast.Location, request, method }, true, ct) ?? throw new JsonException();
    }
    public async Task<SaveResponse> SaveAsync(Guid previewId, CancellationToken ct = default) =>
        await SendAsync<SaveResponse>("rest/v1/rpc/carbonshift_save_preview", new { p_preview_id = previewId }, true, ct) ?? throw new JsonException();
    public async Task<IReadOnlyList<ActivityRecord>> HistoryAsync(CancellationToken ct = default) =>
        (await SendAsync<HistoryResponse[]>("rest/v1/rpc/carbonshift_history", new { }, true, ct) ?? []).Select(r => r.ToRecord()).ToArray();
    public async Task DeleteHistoryAsync(CancellationToken ct = default) => await SendAsync<object>("rest/v1/rpc/carbonshift_delete_history", new { }, true, ct);
    public async Task<DashboardResponse> DashboardAsync(string timeZone, CancellationToken ct = default)
    {
        var history = await HistoryAsync(ct); var now = DateTimeOffset.UtcNow;
        var result = DashboardStatistics.Calculate(history, TimeZoneInfo.ConvertTime(now, Location.Zone(timeZone)).Date, now);
        return new(result.WeeklyCount, result.AverageScore * 100, result.Next == null ? null : HistoryResponse.From(result.Next));
    }
    private void Expire() { Logout(); SessionExpired?.Invoke(this, EventArgs.Empty); }
    public void Logout() { token = null; expires = default; forecasts.Clear(); Interlocked.Increment(ref generation); }
    public void Dispose() { Logout(); http.Dispose(); }
}
