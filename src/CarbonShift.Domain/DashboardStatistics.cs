namespace CarbonShift;

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
