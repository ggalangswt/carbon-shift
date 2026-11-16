using System;
using System.Collections.Generic;
using System.Linq;

namespace CarbonShift
{
    public class GreenScoreDetail
    {
        public double Skor { get; private set; }
        public double RataRadiasi { get; private set; }
        public double RataAwan { get; private set; }
        public double ProporsiSiang { get; private set; }

        public GreenScoreDetail(double skor, double rataRadiasi, double rataAwan, double proporsiSiang)
        {
            Skor = skor;
            RataRadiasi = rataRadiasi;
            RataAwan = rataAwan;
            ProporsiSiang = proporsiSiang;
        }
    }

    public abstract class GreenScoreCalculator
    {
        private const double RadiasiPuncak = 900.0;

        public abstract string Nama { get; }
        public abstract string Rumus { get; }

        public double Calculate(List<WeatherForecast> window)
        {
            if (window == null || window.Count == 0)
                return 0;

            return Batasi(Hitung(window));
        }

        public double Calculate(List<WeatherForecast> window, out GreenScoreDetail detail)
        {
            double skor = Calculate(window);

            if (window == null || window.Count == 0)
                detail = new GreenScoreDetail(0, 0, 0, 0);
            else
                detail = new GreenScoreDetail(skor, window.Average(w => w.Radiasi),
                                              window.Average(w => w.TutupanAwan), SkorSiang(window));

            return skor;
        }

        public virtual string Jelaskan(GreenScoreDetail detail)
        {
            return string.Format("Rata-rata radiasi {0:0} W/m2.", detail.RataRadiasi);
        }

        protected abstract double Hitung(List<WeatherForecast> window);

        protected double SkorSurya(List<WeatherForecast> window)
        {
            return Batasi(window.Average(w => w.Radiasi) / RadiasiPuncak);
        }

        protected double SkorAwan(List<WeatherForecast> window)
        {
            return Batasi(1 - window.Average(w => w.TutupanAwan) / 100.0);
        }

        protected double SkorSiang(List<WeatherForecast> window)
        {
            return (double)window.Count(w => w.Radiasi > 0) / window.Count;
        }

        private static double Batasi(double nilai)
        {
            return Math.Max(0, Math.Min(1, nilai));
        }
    }

    public class SolarOnlyCalculator : GreenScoreCalculator
    {
        public override string Nama { get { return "Potensi Surya"; } }
        public override string Rumus { get { return "Skor = rata-rata radiasi / 900 W/m2"; } }

        protected override double Hitung(List<WeatherForecast> window)
        {
            return SkorSurya(window);
        }
    }

    public class WeightedCalculator : GreenScoreCalculator
    {
        private readonly double bobotSurya;
        private readonly double bobotAwan;
        private readonly double bobotSiang;

        public WeightedCalculator() : this(0.70, 0.20, 0.10) { }

        public WeightedCalculator(double bobotSurya, double bobotAwan, double bobotSiang)
        {
            double total = bobotSurya + bobotAwan + bobotSiang;
            if (total <= 0)
                throw new ArgumentException("Total bobot harus lebih dari 0.");

            this.bobotSurya = bobotSurya / total;
            this.bobotAwan = bobotAwan / total;
            this.bobotSiang = bobotSiang / total;
        }

        public override string Nama { get { return "Berbobot (Surya + Awan + Siang)"; } }

        public override string Rumus
        {
            get
            {
                return string.Format("Skor = {0:0.00} x surya + {1:0.00} x (1 - awan) + {2:0.00} x siang",
                    bobotSurya, bobotAwan, bobotSiang);
            }
        }

        public override string Jelaskan(GreenScoreDetail detail)
        {
            return base.Jelaskan(detail) + string.Format(" Tutupan awan rata-rata {0:0}%, {1:0}% jam di siang hari.",
                detail.RataAwan, detail.ProporsiSiang * 100);
        }

        protected override double Hitung(List<WeatherForecast> window)
        {
            return bobotSurya * SkorSurya(window)
                 + bobotAwan * SkorAwan(window)
                 + bobotSiang * SkorSiang(window);
        }
    }
}
