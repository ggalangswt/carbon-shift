namespace CarbonShift.Tests;

public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CARBONSHIFT_TEST_DB") != "1")
            Skip = "Set CARBONSHIFT_TEST_DB=1 to test isolated PostgreSQL fixtures.";
    }
}

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CARBONSHIFT_TEST_LIVE") != "1")
            Skip = "Set CARBONSHIFT_TEST_LIVE=1 to call real Open-Meteo endpoints.";
    }
}
