using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CarbonShift
{
    // ===================== MODEL =====================

    public class WeatherForecast
    {
        public int Jam { get; set; }
        public double TutupanAwan { get; set; }   // persen
        public double Radiasi { get; set; }       // W/m2

        // Potensi surya 0 - 1 (radiasi 900 W/m2 dianggap puncak)
        public double SolarPotential
        {
            get { return Math.Max(0, Math.Min(1, Radiasi / 900.0)); }
        }

        public string Kondisi
        {
            get
            {
                if (TutupanAwan < 25) return "Cerah";
                if (TutupanAwan < 60) return "Berawan Sebagian";
                return "Berawan";
            }
        }
    }

    public class TimeSlot
    {
        public int StartHour { get; set; }
        public int EndHour { get; set; }
        public double GreenScore { get; set; }
        public double AvgCloud { get; set; }
        public string Bukti { get; set; } = "";

        public string Rentang
        {
            get { return string.Format("{0:00}:00 - {1:00}:00", StartHour, EndHour); }
        }

        public string Label
        {
            get
            {
                if (GreenScore >= 0.7) return "Sangat Baik";
                if (GreenScore >= 0.4) return "Cukup";
                return "Rendah";
            }
        }

        public string Explanation
        {
            get
            {
                if (GreenScore >= 0.7)
                    return string.Format("Direkomendasikan karena potensi energi surya tinggi dan tutupan awan rata-rata hanya {0:0}%.", AvgCloud);
                if (GreenScore >= 0.4)
                    return string.Format("Cukup direkomendasikan, namun potensi energi surya sedang dengan tutupan awan rata-rata {0:0}%.", AvgCloud);
                return string.Format("Kurang direkomendasikan karena potensi surya rendah (tutupan awan rata-rata {0:0}%). Pertimbangkan rentang waktu lain.", AvgCloud);
            }
        }
    }

    public class ActivityRecord
    {
        public string Nama { get; set; }
        public int DurasiJam { get; set; }
        public double KWh { get; set; }
        public string Rentang { get; set; }
        public double GreenScore { get; set; }
        public DateTime Dibuat { get; set; }

        // Perkiraan porsi energi yang berasal dari surya
        public double PorsiSurya { get { return KWh * GreenScore; } }
    }

    // ===================== STATE GLOBAL =====================

    public static class AppState
    {
        public static List<WeatherForecast> Forecast = WeatherService.GetDummy();
        public static string SumberData = "Data contoh (offline)";
        public static List<ActivityRecord> History = new List<ActivityRecord>();
    }

    // ===================== SERVICE =====================

    public static class AuthService
    {
        public static int UserId;
        public static string NamaUser = "";

        public static bool Login(string email, string password, out string pesan)
        {
            pesan = "";

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                pesan = "Email dan password wajib diisi.";
                return false;
            }
            if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            {
                pesan = "Format email tidak valid.";
                return false;
            }

            UserAccount? user;
            try
            {
                user = UserRepository.FindByEmail(email);
            }
            catch (Exception ex)
            {
                pesan = "Tidak dapat terhubung ke database: " + ex.Message;
                return false;
            }

            if (user == null || !PasswordHasher.Verify(password, user.PasswordHash))
            {
                pesan = "Email atau password salah.";
                return false;
            }

            UserId = user.UserId;
            NamaUser = user.Name;
            return true;
        }
    }

    public static class WeatherService
    {
        // Koordinat lokasi untuk data live (ganti sesuai kota Anda)
        public const double Latitude = -5.43;
        public const double Longitude = 105.26;

        public static string Lokasi
        {
            get { return string.Format(CultureInfo.InvariantCulture, "{0}, {1}", Latitude, Longitude); }
        }

        public static List<WeatherForecast> GetDummy()
        {
            double[] awan = { 70, 70, 70, 70, 70, 70, 60, 50, 40, 30, 20, 15, 10, 15, 25, 40, 55, 70, 75, 80, 80, 80, 80, 80 };
            double[] rad = { 0, 0, 0, 0, 0, 0, 40, 180, 380, 560, 720, 830, 860, 800, 650, 480, 280, 90, 5, 0, 0, 0, 0, 0 };

            List<WeatherForecast> list = new List<WeatherForecast>();
            for (int i = 0; i < 24; i++)
            {
                list.Add(new WeatherForecast { Jam = i, TutupanAwan = awan[i], Radiasi = rad[i] });
            }
            return list;
        }

        // Ambil data nyata dari Open-Meteo (gratis, tanpa API key)
        public static async Task<List<WeatherForecast>> GetLiveAsync()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            string url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&hourly=cloud_cover,shortwave_radiation&forecast_days=1&timezone=auto",
                Latitude, Longitude);

            using (WebClient wc = new WebClient())
            {
                string json = await wc.DownloadStringTaskAsync(url);

                double[] awan = ParseArray(json, "cloud_cover");
                double[] rad = ParseArray(json, "shortwave_radiation");

                int n = Math.Min(awan.Length, rad.Length);
                List<WeatherForecast> list = new List<WeatherForecast>();
                for (int i = 0; i < n && i < 24; i++)
                {
                    list.Add(new WeatherForecast { Jam = i, TutupanAwan = awan[i], Radiasi = rad[i] });
                }
                return list;
            }
        }

        private static double[] ParseArray(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + key + "\":\\[([^\\]]*)\\]");
            if (!m.Success) throw new Exception("Data '" + key + "' tidak ditemukan pada respons API.");

            string[] parts = m.Groups[1].Value.Split(',');
            double[] hasil = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                double v;
                hasil[i] = double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
            }
            return hasil;
        }
    }

    public static class RecommendationEngine
    {
        public static List<TimeSlot> Generate(List<WeatherForecast> data, int durasi, int jamMulaiMin, int jamSelesaiMax,
                                              GreenScoreCalculator calculator)
        {
            List<TimeSlot> hasil = new List<TimeSlot>();

            for (int start = jamMulaiMin; start + durasi <= jamSelesaiMax; start++)
            {
                int s = start;
                List<WeatherForecast> window = data.Where(d => d.Jam >= s && d.Jam < s + durasi).ToList();
                if (window.Count < durasi) continue;

                GreenScoreDetail detail;
                double skor = calculator.Calculate(window, out detail);

                hasil.Add(new TimeSlot
                {
                    StartHour = s,
                    EndHour = s + durasi,
                    GreenScore = skor,
                    AvgCloud = detail.RataAwan,
                    Bukti = calculator.Jelaskan(detail)
                });
            }

            return hasil.OrderByDescending(t => t.GreenScore).ToList();
        }
    }
}