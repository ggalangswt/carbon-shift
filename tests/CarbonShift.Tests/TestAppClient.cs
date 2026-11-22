using CarbonShift;

namespace CarbonShift.Tests;

internal sealed class TestAppClient : ICarbonShiftClient
{
    private UserAccount user = new(1, "Pengujian", "test@example.test");
    private readonly Dictionary<Guid, RecommendationResult> previews = [];
    private readonly List<ActivityRecord> history = [];
    public event EventHandler? SessionExpired;
    public void Expire() => SessionExpired?.Invoke(this, EventArgs.Empty);
    public Task<UserAccount> LoginAsync(string email, string password, CancellationToken ct = default) => Task.FromResult(user with { Email = email });
    public Task<UserAccount> RegisterAsync(string name, string email, string password, string confirmation, CancellationToken ct = default)
    {
        if (password.Length < 8) throw new CarbonShiftApiException("Password harus 8–128 karakter.", System.Net.HttpStatusCode.BadRequest, "password");
        user = new(1, name, email); return Task.FromResult(user);
    }
    public Task<IReadOnlyList<Location>> SearchAsync(string query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Location>>([Fixtures.Jogja]);
    public Task<ForecastResponse> ForecastAsync(Location location, bool force, CancellationToken ct = default) =>
        Task.FromResult(new ForecastResponse(Guid.NewGuid(), Fixtures.Forecast(now: DateTimeOffset.UtcNow)));
    public Task<PreviewResponse> PreviewAsync(Guid forecastId, ScheduleRequest request, string method, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var result = RecommendationEngine.Generate(request, Fixtures.Forecast(now: now), now, method == "solar" ? new SolarOnlyCalculator() : new WeightedCalculator());
        var id = Guid.NewGuid(); previews[id] = result;
        return Task.FromResult(new PreviewResponse(id, now.AddMinutes(15), request, result.Forecast, method, result.Slots.Take(10).Select(SlotResponse.From).ToArray(), result.Explanation));
    }
    public Task<SaveResponse> SaveAsync(Guid previewId, CancellationToken ct = default)
    {
        var r = previews[previewId]; int id = history.Count + 1;
        history.Add(new(id, r.Request.ActivityName, r.Request.DurationHours * 60, r.Request.EnergyKwh, r.Best.Start.DateTime, r.Best.End.DateTime,
            r.Best.GreenScore, DateTime.UtcNow, r.Forecast.Location.DatabaseLabel, r.Explanation));
        return Task.FromResult(new SaveResponse(id));
    }
    public Task<IReadOnlyList<ActivityRecord>> HistoryAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ActivityRecord>>(history.ToArray());
    public Task DeleteHistoryAsync(CancellationToken ct = default) { history.Clear(); return Task.CompletedTask; }
    public Task<DashboardResponse> DashboardAsync(string timeZone, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow; var d = DashboardStatistics.Calculate(history, Fixtures.Jogja.LocalNow(now).Date, now);
        return Task.FromResult(new DashboardResponse(d.WeeklyCount, d.AverageScore * 100, d.Next == null ? null : HistoryResponse.From(d.Next)));
    }
    public void Logout() { }
}
