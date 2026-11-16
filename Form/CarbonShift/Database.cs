using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Npgsql;

namespace CarbonShift
{
    public static class Database
    {
        private const string EnvName = "CARBONSHIFT_DB";
        private const string FileName = "database.config";
        private static string? connectionString;

        public static string ConnectionString
        {
            get
            {
                if (connectionString == null)
                    connectionString = LoadConnectionString();
                return connectionString;
            }
        }

        private static string LoadConnectionString()
        {
            string? env = Environment.GetEnvironmentVariable(EnvName);
            if (!string.IsNullOrWhiteSpace(env))
                return env.Trim();

            string path = Path.Combine(AppContext.BaseDirectory, FileName);
            if (File.Exists(path))
            {
                string isi = File.ReadAllText(path).Trim();
                if (isi.Length > 0)
                    return isi;
            }

            throw new InvalidOperationException(
                "Connection string belum diatur. Buat file database.config (lihat database.config.example) atau environment variable CARBONSHIFT_DB.");
        }

        public static NpgsqlConnection OpenConnection()
        {
            NpgsqlConnection conn = new NpgsqlConnection(ConnectionString);
            conn.Open();
            return conn;
        }
    }

    public static class PasswordHasher
    {
        private const string Algoritma = "pbkdf2-sha256";
        private const int Iterasi = 100000;

        public static string Hash(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterasi, HashAlgorithmName.SHA256, 32);
            return string.Format("{0}${1}${2}${3}", Algoritma, Iterasi, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
        }

        public static bool Verify(string password, string tersimpan)
        {
            string[] bagian = tersimpan.Split('$');
            if (bagian.Length != 4 || bagian[0] != Algoritma)
                return false;

            int iterasi;
            if (!int.TryParse(bagian[1], out iterasi))
                return false;

            byte[] salt = Convert.FromBase64String(bagian[2]);
            byte[] hashAsli = Convert.FromBase64String(bagian[3]);
            byte[] hashInput = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterasi, HashAlgorithmName.SHA256, hashAsli.Length);

            return CryptographicOperations.FixedTimeEquals(hashAsli, hashInput);
        }
    }

    public class UserAccount
    {
        public int UserId { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string PasswordHash { get; set; } = "";
    }

    public static class UserRepository
    {
        public static UserAccount? FindByEmail(string email)
        {
            using (NpgsqlConnection conn = Database.OpenConnection())
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                "SELECT user_id, name, email, password_hash FROM \"user\" WHERE email = @email", conn))
            {
                cmd.Parameters.AddWithValue("email", email);

                using (NpgsqlDataReader r = cmd.ExecuteReader())
                {
                    if (!r.Read())
                        return null;

                    return new UserAccount
                    {
                        UserId = r.GetInt32(0),
                        Name = r.GetString(1),
                        Email = r.GetString(2),
                        PasswordHash = r.GetString(3)
                    };
                }
            }
        }
    }

    public static class RecommendationRepository
    {
        public static int Save(int userId, string namaAktivitas, int durasiJam, double kwh,
                               List<TimeSlot> slots, TimeSlot terbaik, List<WeatherForecast> forecast, string lokasi)
        {
            DateTime hariIni = DateTime.Today;

            using (NpgsqlConnection conn = Database.OpenConnection())
            using (NpgsqlTransaction tx = conn.BeginTransaction())
            {
                int activityId;
                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO activity (user_id, activity_name, duration_minutes, power_consumption_kwh) " +
                    "VALUES (@user, @nama, @durasi, @kwh) RETURNING activity_id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("user", userId);
                    cmd.Parameters.AddWithValue("nama", namaAktivitas);
                    cmd.Parameters.AddWithValue("durasi", durasiJam * 60);
                    cmd.Parameters.AddWithValue("kwh", Math.Round((decimal)kwh, 2));
                    activityId = (int)cmd.ExecuteScalar()!;
                }

                List<WeatherForecast> siang = forecast.Where(f => f.Radiasi > 0).ToList();
                if (siang.Count == 0) siang = forecast;
                WeatherForecast ringkasan = new WeatherForecast
                {
                    TutupanAwan = siang.Average(f => f.TutupanAwan),
                    Radiasi = siang.Average(f => f.Radiasi)
                };

                int forecastId;
                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO weather_forecast (location, forecast_date, temperature, weather_condition, solar_potential) " +
                    "VALUES (@lokasi, @tanggal, NULL, @kondisi, @surya) RETURNING forecast_id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("lokasi", lokasi);
                    cmd.Parameters.AddWithValue("tanggal", hariIni);
                    cmd.Parameters.AddWithValue("kondisi", ringkasan.Kondisi);
                    cmd.Parameters.AddWithValue("surya", KeSkala100(ringkasan.SolarPotential));
                    forecastId = (int)cmd.ExecuteScalar()!;
                }

                int timeslotTerbaik = 0;
                foreach (TimeSlot slot in slots)
                {
                    using (NpgsqlCommand cmd = new NpgsqlCommand(
                        "INSERT INTO time_slot (activity_id, forecast_id, start_time, end_time, green_score) " +
                        "VALUES (@activity, @forecast, @mulai, @selesai, @skor) RETURNING timeslot_id", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("activity", activityId);
                        cmd.Parameters.AddWithValue("forecast", forecastId);
                        cmd.Parameters.AddWithValue("mulai", hariIni.AddHours(slot.StartHour));
                        cmd.Parameters.AddWithValue("selesai", hariIni.AddHours(slot.EndHour));
                        cmd.Parameters.AddWithValue("skor", KeSkala100(slot.GreenScore));
                        int id = (int)cmd.ExecuteScalar()!;
                        if (slot == terbaik) timeslotTerbaik = id;
                    }
                }

                int recommendationId;
                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO recommendation (timeslot_id, explanation, green_score) " +
                    "VALUES (@timeslot, @penjelasan, @skor) RETURNING recommendation_id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("timeslot", timeslotTerbaik);
                    cmd.Parameters.AddWithValue("penjelasan", terbaik.Explanation);
                    cmd.Parameters.AddWithValue("skor", KeSkala100(terbaik.GreenScore));
                    recommendationId = (int)cmd.ExecuteScalar()!;
                }

                tx.Commit();
                return recommendationId;
            }
        }

        public static List<ActivityRecord> GetHistory(int userId)
        {
            List<ActivityRecord> hasil = new List<ActivityRecord>();

            using (NpgsqlConnection conn = Database.OpenConnection())
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                "SELECT a.activity_name, a.duration_minutes, a.power_consumption_kwh, " +
                "       t.start_time, t.end_time, r.green_score, r.created_at " +
                "FROM activity a " +
                "JOIN time_slot t ON t.activity_id = a.activity_id " +
                "JOIN recommendation r ON r.timeslot_id = t.timeslot_id " +
                "WHERE a.user_id = @user " +
                "ORDER BY r.created_at DESC", conn))
            {
                cmd.Parameters.AddWithValue("user", userId);

                using (NpgsqlDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        DateTime mulai = r.GetDateTime(3);
                        DateTime selesai = r.GetDateTime(4);
                        int jamSelesai = mulai.Hour + (int)(selesai - mulai).TotalHours;

                        hasil.Add(new ActivityRecord
                        {
                            Nama = r.GetString(0),
                            DurasiJam = r.GetInt32(1) / 60,
                            KWh = (double)r.GetDecimal(2),
                            Rentang = string.Format("{0:00}:00 - {1:00}:00", mulai.Hour, jamSelesai),
                            GreenScore = r.IsDBNull(5) ? 0 : (double)r.GetDecimal(5) / 100.0,
                            Dibuat = r.GetDateTime(6)
                        });
                    }
                }
            }

            return hasil;
        }

        public static void DeleteHistory(int userId)
        {
            using (NpgsqlConnection conn = Database.OpenConnection())
            using (NpgsqlTransaction tx = conn.BeginTransaction())
            {
                string[] perintah =
                {
                    "DELETE FROM recommendation WHERE timeslot_id IN " +
                    "(SELECT t.timeslot_id FROM time_slot t JOIN activity a ON a.activity_id = t.activity_id WHERE a.user_id = @user)",
                    "DELETE FROM time_slot WHERE activity_id IN (SELECT activity_id FROM activity WHERE user_id = @user)",
                    "DELETE FROM activity WHERE user_id = @user",
                    "DELETE FROM weather_forecast f WHERE NOT EXISTS (SELECT 1 FROM time_slot t WHERE t.forecast_id = f.forecast_id)"
                };

                foreach (string sql in perintah)
                {
                    using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn, tx))
                    {
                        cmd.Parameters.AddWithValue("user", userId);
                        cmd.ExecuteNonQuery();
                    }
                }

                tx.Commit();
            }
        }

        private static decimal KeSkala100(double skor01)
        {
            return Math.Round((decimal)(skor01 * 100), 2);
        }
    }
}
