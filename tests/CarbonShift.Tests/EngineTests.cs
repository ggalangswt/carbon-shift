using System.Globalization;
using System.Reflection;
using CarbonShift;

namespace CarbonShift.Tests;

public static class Fixtures
{
    public static readonly DateTimeOffset Now = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);
    public static readonly Location Jogja = new("Yogyakarta", "DI Yogyakarta", -7.8, 110.36, "Asia/Jakarta");
    public static ForecastSnapshot Forecast(Location? location = null, DateTimeOffset? now = null, ForecastSource source = ForecastSource.Live)
    {
        var loc = location ?? Jogja; var at = now ?? Now;
        return new(loc, at, Enumerable.Range(0, 72).Select(i => new WeatherForecast(
            loc.At(loc.LocalNow(at).Date.AddHours(i)), 28, 20, i % 24 is >= 6 and < 18 ? 720 : 0, i % 24 is >= 6 and < 18)).ToArray(), source);
    }
    public static ScheduleRequest Request(DateOnly? date = null) => new("Laptop", 2, .12m, date ?? new(2026, 11, 23), 6, 18);
    public static RecommendationResult Result() => RecommendationEngine.Generate(Request(), Forecast(), Now, new WeightedCalculator());
}

public class EngineTests
{
    [Fact] public void Weighted_score_uses_documented_components()
    {
        var h = new WeatherForecast(Fixtures.Jogja.At(new(2026, 11, 23, 10, 0, 0)), 29, 20, 720, true);
        Assert.Equal(.82, new WeightedCalculator().Calculate([h]), 10);
        Assert.Equal(.8, new SolarOnlyCalculator().Calculate([h]), 10);
        Assert.Equal(.72, new WeightedCalculator().Calculate([h with { IsDay = false }]), 10);
    }
    [Fact] public void Generates_complete_ranked_windows_and_breaks_ties_by_start()
    {
        var result = Fixtures.Result();
        Assert.Equal(11, result.Slots.Count);
        Assert.Equal(6, result.Best.Start.Hour);
        Assert.All(result.Slots, s => { Assert.InRange(s.GreenScore, 0, 1); Assert.Equal(TimeSpan.FromHours(2), s.End - s.Start); });
        Assert.Equal(result.Slots.OrderByDescending(s => s.GreenScore).ThenBy(s => s.Start), result.Slots);
    }
    [Fact] public void Picks_highest_radiation_with_other_factors_equal()
    {
        var f = Fixtures.Forecast();
        var hours = f.Hours.Select(h => h with { Radiasi = h.Start.Hour switch { 10 => 400, 11 => 600, 12 => 800, 13 => 850, 14 => 700, _ => 0 } }).ToArray();
        var result = RecommendationEngine.Generate(Fixtures.Request() with { EarliestHour = 10, LatestHour = 15 },
            new(f.Location, f.FetchedAt, hours, f.Source), Fixtures.Now, new WeightedCalculator());
        Assert.Equal(12, result.Best.Start.Hour);
    }
    [Theory]
    [InlineData(0, 7)] [InlineData(1, 8)] [InlineData(59, 8)]
    public void Excludes_elapsed_hours(int minutes, int expected)
    {
        var now = Fixtures.Now.AddMinutes(minutes);
        var result = RecommendationEngine.Generate(Fixtures.Request(new(2026, 11, 22)), Fixtures.Forecast(), now, new WeightedCalculator());
        Assert.Equal(expected, result.Slots.Min(s => s.Start.Hour));
    }
    [Theory]
    [InlineData("Asia/Jakarta", 7)] [InlineData("Asia/Makassar", 8)] [InlineData("Asia/Jayapura", 9)]
    public void Uses_location_clock_not_computer_clock(string zone, int expectedHour)
    {
        var loc = Fixtures.Jogja with { TimeZoneId = zone };
        var result = RecommendationEngine.Generate(Fixtures.Request(new(2026, 11, 22)), Fixtures.Forecast(loc),
            Fixtures.Now.AddMinutes(1), new WeightedCalculator());
        Assert.Equal(expectedHour + 1, result.Slots.Min(s => s.Start.Hour));
        Assert.Equal(TimeSpan.FromHours(expectedHour), result.Best.Start.Offset);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Supports_three_calendar_days(int day)
    {
        var request = Fixtures.Request(new DateOnly(2026, 11, 22).AddDays(day));
        Assert.Equal(request.Date, DateOnly.FromDateTime(RecommendationEngine.Generate(request, Fixtures.Forecast(),
            Fixtures.Now, new WeightedCalculator()).Best.Start.DateTime));
    }
    [Theory]
    [InlineData(-1)] [InlineData(3)]
    public void Rejects_dates_outside_horizon(int day) =>
        Assert.Throws<ArgumentException>(() => RecommendationEngine.Generate(Fixtures.Request(new DateOnly(2026, 11, 22).AddDays(day)),
            Fixtures.Forecast(), Fixtures.Now, new WeightedCalculator()));
    [Fact] public void Missing_duplicate_or_invalid_hours_are_not_filled_with_zero()
    {
        var f = Fixtures.Forecast();
        var h = f.Hours.Where(h => h.Start.Hour != 10).ToList();
        h.Add(h.First(x => x.Start.Day == 23 && x.Start.Hour == 11));
        var result = RecommendationEngine.Generate(Fixtures.Request(), new(f.Location, f.FetchedAt, h, f.Source), Fixtures.Now, new WeightedCalculator());
        Assert.DoesNotContain(result.Slots, s => s.Start.Hour is 9 or 10 or 11);
    }
    [Fact] public void Rejects_insufficient_interval_and_blank_name()
    {
        Assert.Throws<ArgumentException>(() => RecommendationEngine.Generate(Fixtures.Request() with { LatestHour = 7 }, Fixtures.Forecast(), Fixtures.Now, new WeightedCalculator()));
        Assert.Throws<ArgumentException>(() => RecommendationEngine.Generate(Fixtures.Request() with { ActivityName = " " }, Fixtures.Forecast(), Fixtures.Now, new WeightedCalculator()));
    }
    [Fact] public void Snapshot_is_defensively_copied()
    {
        var hours = Fixtures.Forecast().Hours.ToList();
        var snapshot = new ForecastSnapshot(Fixtures.Jogja, Fixtures.Now, hours, ForecastSource.Live);
        var result = RecommendationEngine.Generate(Fixtures.Request(), snapshot, Fixtures.Now, new WeightedCalculator());
        hours.Clear();
        Assert.Equal(72, result.Forecast.Hours.Count);
        Assert.Contains("Komponen (0-100)", result.Explanation);
        Assert.Contains("Sumber:", result.Explanation);
        Assert.Contains("permintaan 06:00 - 18:00", result.Explanation);
    }
    [Fact] public void Demo_elapsed_and_expired_results_cannot_be_saved()
    {
        var result = Fixtures.Result();
        RecommendationPolicy.EnsureSaveable(result, Fixtures.Now);
        Assert.Throws<InvalidOperationException>(() => RecommendationPolicy.EnsureSaveable(result, result.Best.Start.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(() => RecommendationPolicy.EnsureSaveable(result, Fixtures.Now.AddHours(7)));
        var demo = RecommendationEngine.Generate(Fixtures.Request(), Fixtures.Forecast(source: ForecastSource.Demo), Fixtures.Now, new WeightedCalculator());
        Assert.Throws<InvalidOperationException>(() => RecommendationPolicy.EnsureSaveable(demo, Fixtures.Now));
    }
    [Fact] public void Negative_or_nonfinite_weights_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new WeightedCalculator(-1, 2, 1));
        Assert.Throws<ArgumentException>(() => new WeightedCalculator(double.NaN, 1, 1));
        Assert.Equal(0, new WeightedCalculator().Calculate([]));
    }
}

public class HistoryTests
{
    [Fact] public void Csv_escapes_user_text_and_exports_score_on_scale_100()
    {
        var row = new ActivityRecord(1, "Laptop \"A\", besar", 90, .12m, new(2026, 11, 23, 10, 0, 0),
            new(2026, 11, 23, 12, 0, 0), .8234, new(2026, 11, 22), Fixtures.Jogja.DatabaseLabel, "Dua baris\r\nalasan");
        string csv = HistoryCsv.Export([row]);
        Assert.Contains("\"Laptop \"\"A\"\", besar\"", csv);
        Assert.Contains(",1.5,0.12,", csv);
        Assert.Contains(",82.34,", csv);
        Assert.Contains("\"Dua baris\r\nalasan\"", csv);
        Assert.Equal(TimeSpan.FromHours(7), row.StartInstant!.Value.Offset);
        Assert.Contains("\"'=1+1\"", HistoryCsv.Export([row with { Nama = "=1+1" }]));
    }
    [Fact] public void Dashboard_uses_schedule_date_and_handles_legacy_timezone()
    {
        var next = new ActivityRecord(1, "Besok", 60, .1m, new(2026, 11, 23, 10, 0, 0),
            new(2026, 11, 23, 11, 0, 0), .8, new(2026, 1, 1), Fixtures.Jogja.DatabaseLabel, "");
        var legacy = next with { Id = 2, Lokasi = "Yogyakarta" };
        var stats = DashboardStatistics.Calculate([next, legacy], new(2026, 11, 23), Fixtures.Now);
        Assert.Equal(2, stats.WeeklyCount); Assert.Equal(.8, stats.AverageScore!.Value, 10); Assert.Equal(next, stats.Next);
        Assert.Null(legacy.StartInstant);
    }
}
