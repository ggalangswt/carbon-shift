namespace CarbonShift;

public sealed record GreenScoreDetail(double Skor, double RataRadiasi, double RataAwan, double ProporsiSiang)
{
    public double SolarScore => Math.Clamp(RataRadiasi / 900, 0, 1);
    public double CloudScore => Math.Clamp(1 - RataAwan / 100, 0, 1);
}

public abstract class GreenScoreCalculator
{
    public abstract string Nama { get; }
    public abstract string Rumus { get; }
    public double Calculate(IReadOnlyList<WeatherForecast> window) =>
        window.Count == 0 ? 0 : Math.Clamp(Hitung(window), 0, 1);
    public double Calculate(IReadOnlyList<WeatherForecast> window, out GreenScoreDetail detail)
    {
        double score = Calculate(window);
        detail = window.Count == 0 ? new(0, 0, 0, 0) :
            new(score, window.Average(w => w.Radiasi), window.Average(w => w.TutupanAwan), SkorSiang(window));
        return score;
    }
    public virtual string Jelaskan(GreenScoreDetail detail) =>
        $"Rata-rata radiasi {detail.RataRadiasi:0} W/m², awan {detail.RataAwan:0}%, dan {detail.ProporsiSiang * 100:0}% jam siang.";
    protected abstract double Hitung(IReadOnlyList<WeatherForecast> window);
    protected double SkorSurya(IReadOnlyList<WeatherForecast> window) => Math.Clamp(window.Average(w => w.Radiasi) / 900, 0, 1);
    protected double SkorAwan(IReadOnlyList<WeatherForecast> window) => Math.Clamp(1 - window.Average(w => w.TutupanAwan) / 100, 0, 1);
    protected double SkorSiang(IReadOnlyList<WeatherForecast> window) => (double)window.Count(w => w.IsDay) / window.Count;
}

public sealed class SolarOnlyCalculator : GreenScoreCalculator
{
    public override string Nama => "Potensi surya";
    public override string Rumus => "Skor internal = rata-rata radiasi / 900 W/m², dibatasi 0-1";
    protected override double Hitung(IReadOnlyList<WeatherForecast> window) => SkorSurya(window);
}

public sealed class WeightedCalculator : GreenScoreCalculator
{
    private readonly double solar, cloud, day;
    public WeightedCalculator() : this(.7, .2, .1) { }
    public WeightedCalculator(double bobotSurya, double bobotAwan, double bobotSiang)
    {
        double total = bobotSurya + bobotAwan + bobotSiang;
        if (!double.IsFinite(total) || total <= 0 || bobotSurya < 0 || bobotAwan < 0 || bobotSiang < 0)
            throw new ArgumentException("Bobot harus berhingga, tidak negatif, dan total lebih dari nol.");
        solar = bobotSurya / total; cloud = bobotAwan / total; day = bobotSiang / total;
    }
    public override string Nama => "Berbobot (surya + awan + siang)";
    public override string Rumus => $"Skor internal = {solar:0.00} × surya + {cloud:0.00} × (1 - awan/100) + {day:0.00} × siang; tampilan × 100";
    protected override double Hitung(IReadOnlyList<WeatherForecast> window) =>
        solar * SkorSurya(window) + cloud * SkorAwan(window) + day * SkorSiang(window);
}
