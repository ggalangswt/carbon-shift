using System.Text.RegularExpressions;

namespace CarbonShift;

public sealed record Location(string Name, string Province, double Latitude, double Longitude, string TimeZoneId)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && double.IsFinite(Latitude) &&
        double.IsFinite(Longitude) && Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180 &&
        TimeZoneId is "Asia/Jakarta" or "Asia/Makassar" or "Asia/Jayapura";
    public string DisplayName => string.IsNullOrWhiteSpace(Province) ? Name : $"{Name}, {Province}";
    public string CacheKey => FormattableString.Invariant($"{Latitude:F5}_{Longitude:F5}_{TimeZoneId.Replace('/', '_')}");
    public string DatabaseLabel
    {
        get
        {
            string suffix = FormattableString.Invariant($" ({Latitude:F4},{Longitude:F4}) [{TimeZoneId}]");
            return DisplayName[..Math.Min(DisplayName.Length, Math.Max(0, 100 - suffix.Length))] + suffix;
        }
    }
    public DateTimeOffset LocalNow(DateTimeOffset now) => TimeZoneInfo.ConvertTime(now, Zone(TimeZoneId));
    public static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);
    public DateTimeOffset At(DateTime local) => new(DateTime.SpecifyKind(local, DateTimeKind.Unspecified),
        Zone(TimeZoneId).GetUtcOffset(local));
    public override string ToString() => DisplayName;
}

// A complete interval [Start, Start + 1 hour). Radiation is the mean for this interval.
public sealed record WeatherForecast(DateTimeOffset Start, double Temperature, double TutupanAwan,
    double Radiasi, bool IsDay)
{
    public double SolarPotential => Math.Clamp(Radiasi / 900.0, 0, 1);
    public string Kondisi => !IsDay ? "Malam" : TutupanAwan < 25 ? "Cerah" : TutupanAwan < 60 ? "Berawan sebagian" : "Berawan";
    public bool IsValid => double.IsFinite(Temperature) && double.IsFinite(TutupanAwan) &&
        TutupanAwan is >= 0 and <= 100 && double.IsFinite(Radiasi) && Radiasi >= 0 &&
        Start.Minute == 0 && Start.Second == 0;
}

public enum ForecastSource { Live, Cache, StaleCache, Demo }

public sealed class ForecastSnapshot
{
    public Location Location { get; }
    public DateTimeOffset FetchedAt { get; }
    public IReadOnlyList<WeatherForecast> Hours { get; }
    public ForecastSource Source { get; }
    public string? Warning { get; }
    public bool IsDemo => Source == ForecastSource.Demo;
    public string SourceLabel => Source switch
    {
        ForecastSource.Live => "Open-Meteo (live)", ForecastSource.Cache => "Open-Meteo (cache)",
        ForecastSource.StaleCache => "Cache cadangan", _ => "DEMO, tidak dapat disimpan"
    };
    public string Status => $"{Location.DisplayName} | {Location.TimeZoneId} | {SourceLabel} | Diambil {Location.LocalNow(FetchedAt):dd MMM HH:mm}";
    public ForecastSnapshot(Location location, DateTimeOffset fetchedAt, IReadOnlyList<WeatherForecast> hours,
        ForecastSource source, string? warning = null)
    {
        Location = location; FetchedAt = fetchedAt;
        Hours = Array.AsReadOnly(hours.ToArray());
        Source = source; Warning = warning;
    }
    public ForecastSnapshot WithSource(ForecastSource source, string? warning = null) =>
        new(Location, FetchedAt, Hours, source, warning);
}

public sealed record ScheduleRequest(string ActivityName, int DurationHours, decimal EnergyKwh,
    DateOnly Date, int EarliestHour, int LatestHour);

public sealed record TimeSlot(DateTimeOffset Start, DateTimeOffset End, GreenScoreDetail Detail)
{
    public double GreenScore => Detail.Skor;
    public string Rentang => $"{Start:HH:mm} - {(End.Date > Start.Date ? "24:00" : End.ToString("HH:mm"))}";
    public string Label => GreenScore >= .7 ? "Sangat baik" : GreenScore >= .4 ? "Cukup" : "Rendah";
}

public sealed class RecommendationResult
{
    public ScheduleRequest Request { get; }
    public ForecastSnapshot Forecast { get; }
    public string Method { get; }
    public string Formula { get; }
    public IReadOnlyList<TimeSlot> Slots { get; }
    public TimeSlot Best => Slots[0];
    public string Explanation { get; }
    public RecommendationResult(ScheduleRequest request, ForecastSnapshot forecast,
        GreenScoreCalculator calculator, IEnumerable<TimeSlot> slots, string? explanation = null)
    {
        Request = request; Forecast = forecast; Method = calculator.Nama; Formula = calculator.Rumus;
        Slots = Array.AsReadOnly(slots.ToArray());
        if (Slots.Count == 0) throw new ArgumentException("Tidak ada slot lengkap yang masih akan datang pada rentang ini.");
        var d = Best.Detail;
        Explanation = $"Aktivitas: {request.ActivityName}; estimasi total energi {request.EnergyKwh:0.00} kWh.\r\n" +
            $"Tanggal: {request.Date:dd MMM yyyy}; permintaan {request.EarliestHour:00}:00 - {request.LatestHour:00}:00; durasi {request.DurationHours} jam.\r\n" +
            $"Lokasi: {forecast.Location.DatabaseLabel}.\r\n" +
            $"Waktu terbaik: {Best.Rentang}; Green Score {Best.GreenScore * 100:0.0}/100 ({Best.Label}).\r\n" +
            $"Metode: {Method}. {Formula}.\r\n" +
            $"Komponen (0-100): surya {d.SolarScore * 100:0.0}, langit cerah {d.CloudScore * 100:0.0}, siang {d.ProporsiSiang * 100:0.0}.\r\n" +
            calculator.Jelaskan(d) + " Semua jam aktivitas memenuhi rentang permintaan. " +
            (Best.GreenScore < .4 ? "Potensi rendah; pertimbangkan tanggal atau rentang lain. " : "Skor tertinggi di antara kandidat yang tersedia. ") +
            $"\r\nSumber: {forecast.SourceLabel}; diambil {forecast.Location.LocalNow(forecast.FetchedAt):yyyy-MM-dd HH:mm:ss zzz}. " +
            (forecast.Warning is null ? "" : forecast.Warning + " ") + "\r\n" + ScoreMeaning;
        if (explanation != null) Explanation = explanation;
    }
    public const string ScoreMeaning = "Green Score menunjukkan kesesuaian waktu dengan potensi surya, bukan persentase listrik terbarukan atau pengurangan emisi.";
}

public static class RecommendationEngine
{
    public static RecommendationResult Generate(ScheduleRequest request, ForecastSnapshot forecast,
        DateTimeOffset now, GreenScoreCalculator calculator)
    {
        if (string.IsNullOrWhiteSpace(request.ActivityName) || request.ActivityName.Trim().Length > 100)
            throw new ArgumentException("Nama aktivitas wajib diisi, maksimal 100 karakter.");
        if (request.DurationHours is < 1 or > 12 || request.EarliestHour is < 0 or > 23 ||
            request.LatestHour is < 1 or > 24 || request.LatestHour - request.EarliestHour < request.DurationHours)
            throw new ArgumentException("Rentang waktu harus cukup untuk durasi 1-12 jam.");
        if (request.EnergyKwh <= 0 || request.EnergyKwh > 9999.99m)
            throw new ArgumentException("Estimasi total energi harus lebih dari 0 dan maksimal 9999,99 kWh.");
        var today = DateOnly.FromDateTime(forecast.Location.LocalNow(now).DateTime);
        if (request.Date < today || request.Date > today.AddDays(2))
            throw new ArgumentException("Pilih tanggal hari ini, besok, atau lusa sesuai zona waktu lokasi.");
        var hours = forecast.Hours.Where(h => h.IsValid).GroupBy(h => h.Start)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var slots = new List<TimeSlot>();
        for (int hour = request.EarliestHour; hour + request.DurationHours <= request.LatestHour; hour++)
        {
            var start = forecast.Location.At(request.Date.ToDateTime(TimeOnly.MinValue).AddHours(hour));
            if (start < now) continue;
            var window = new List<WeatherForecast>();
            for (int i = 0; i < request.DurationHours; i++)
                if (hours.TryGetValue(start.AddHours(i), out var item)) window.Add(item);
            if (window.Count != request.DurationHours) continue;
            calculator.Calculate(window, out var detail);
            slots.Add(new(start, start.AddHours(request.DurationHours), detail));
        }
        return new(request, forecast, calculator, slots.OrderByDescending(s => s.GreenScore).ThenBy(s => s.Start));
    }
}

// Shared by UI and tests: any input/forecast change invalidates the saveable result.
public sealed class RecommendationState
{
    public RecommendationResult? Result { get; private set; }
    public bool CanSave => Result is { Forecast.IsDemo: false };
    public void Set(RecommendationResult result) => Result = result;
    public void Invalidate() => Result = null;
}

public sealed record ActivityRecord(int Id, string Nama, int DurationMinutes, decimal KWh,
    DateTime Start, DateTime End, double GreenScore, DateTime Dibuat, string Lokasi, string Explanation)
{
    public string Rentang => $"{Start:dd/MM/yyyy HH:mm} - {End:dd/MM/yyyy HH:mm}";
    public DateTimeOffset? StartInstant
    {
        get
        {
            var match = Regex.Match(Lokasi, @"\[([^\]]+)\]$");
            if (!match.Success) return null;
            try { return new DateTimeOffset(Start, Location.Zone(match.Groups[1].Value).GetUtcOffset(Start)); }
            catch (TimeZoneNotFoundException) { return null; }
        }
    }
}

public sealed class AuthValidationException(string message, string field) : ArgumentException(message)
{
    public string Field { get; } = field;
}


public sealed record UserAccount(int UserId, string Name, string Email);
