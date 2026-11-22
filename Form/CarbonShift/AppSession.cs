namespace CarbonShift;

public sealed class AppSession(UserAccount user, ICarbonShiftClient? client = null)
{
    public ICarbonShiftClient Client { get; } = client ?? new SupabaseAppClient();
    public Guid? ForecastId { get; set; }
    public UserAccount User { get; } = user;
    public Location? Location { get; private set; }
    public ForecastSnapshot? Forecast { get; private set; }
    public event EventHandler? Changed;
    public void SetLocation(Location location)
    {
        if (!location.IsValid) throw new ArgumentException("Lokasi Indonesia tidak valid. Pilih ulang dari pencarian kota.");
        Location = location; Forecast = null; ForecastId = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void SetForecast(ForecastSnapshot? forecast)
    {
        if (forecast != null && forecast.Location != Location) throw new ArgumentException("Lokasi forecast tidak sesuai.");
        ForecastId = null; Forecast = forecast;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

