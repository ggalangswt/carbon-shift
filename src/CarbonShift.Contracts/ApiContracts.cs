namespace CarbonShift;

public sealed record RegisterRequest(string Name, string Email, string Password, string Confirmation);
public sealed record LoginRequest(string Email, string Password);
public sealed record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt, UserAccount User);
public sealed record ApiError(string Message, string? Field = null, string? Code = null);
public sealed record ForecastRequest(Location Location, bool Force = false);
public sealed record ForecastResponse(Guid ForecastId, ForecastSnapshot Forecast);
public sealed record PreviewRequest(Guid ForecastId, ScheduleRequest Request, string Method);
public sealed record SaveRequest(Guid PreviewId);
public sealed record SaveResponse(int RecommendationId);
public sealed record SlotResponse(DateTimeOffset Start, DateTimeOffset End, double GreenScore,
    double Radiation, double CloudCover, double DayScore)
{
    public static SlotResponse From(TimeSlot slot) => new(slot.Start, slot.End, slot.GreenScore * 100,
        slot.Detail.RataRadiasi, slot.Detail.RataAwan, slot.Detail.ProporsiSiang * 100);
    public TimeSlot ToSlot() => new(Start, End, new(GreenScore / 100, Radiation, CloudCover, DayScore / 100));
}
public sealed record PreviewResponse(Guid PreviewId, DateTimeOffset ExpiresAt, ScheduleRequest Request,
    ForecastSnapshot Forecast, string Method, IReadOnlyList<SlotResponse> Slots, string Explanation)
{
    public RecommendationResult ToResult() => new(Request, Forecast,
        Method == "solar" ? new SolarOnlyCalculator() : new WeightedCalculator(), Slots.Select(s => s.ToSlot()), Explanation);
}
public sealed record HistoryResponse(int Id, string Nama, int DurationMinutes, decimal KWh, DateTime Start,
    DateTime End, double GreenScore, DateTime Dibuat, string Lokasi, string Explanation)
{
    public static HistoryResponse From(ActivityRecord r) => new(r.Id, r.Nama, r.DurationMinutes, r.KWh,
        r.Start, r.End, r.GreenScore * 100, r.Dibuat, r.Lokasi, r.Explanation);
    public ActivityRecord ToRecord() => new(Id, Nama, DurationMinutes, KWh, Start, End, GreenScore / 100, Dibuat, Lokasi, Explanation);
}
public sealed record DashboardResponse(int WeeklyCount, double? AverageScore, HistoryResponse? Next);

public interface ICarbonShiftClient
{
    event EventHandler? SessionExpired;
    Task<UserAccount> LoginAsync(string email, string password, CancellationToken ct = default);
    Task<UserAccount> RegisterAsync(string name, string email, string password, string confirmation, CancellationToken ct = default);
    Task<IReadOnlyList<Location>> SearchAsync(string query, CancellationToken ct = default);
    Task<ForecastResponse> ForecastAsync(Location location, bool force, CancellationToken ct = default);
    Task<PreviewResponse> PreviewAsync(Guid forecastId, ScheduleRequest request, string method, CancellationToken ct = default);
    Task<SaveResponse> SaveAsync(Guid previewId, CancellationToken ct = default);
    Task<IReadOnlyList<ActivityRecord>> HistoryAsync(CancellationToken ct = default);
    Task DeleteHistoryAsync(CancellationToken ct = default);
    Task<DashboardResponse> DashboardAsync(string timeZone, CancellationToken ct = default);
    void Logout();
}
