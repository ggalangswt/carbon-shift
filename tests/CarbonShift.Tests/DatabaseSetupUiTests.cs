using System.Reflection;
using CarbonShift;
using Npgsql;
using static CarbonShift.Tests.UiFlowTests;

namespace CarbonShift.Tests;

[Collection("Desktop UI")]
public class DatabaseSetupUiTests
{
    private static void Click(Control parent, string text)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is Button b && b.Text == text) { b.PerformClick(); return; }
            if (Find(c, text) is { } found) { found.PerformClick(); return; }
        }
        throw new InvalidOperationException("Tombol tidak ditemukan: " + text);
    }
    private static Button? Find(Control parent, string text)
    {
        foreach (Control c in parent.Controls)
            if (c is Button b && b.Text == text) return b;
            else if (Find(c, text) is { } found) return found;
        return null;
    }
    private static void Capture(Form form, string name)
    {
        form.PerformLayout();
        string root = Path.Combine(Path.GetTempPath(), "CarbonShift-guide");
        Directory.CreateDirectory(root);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(root, name + ".png"));
    }

    [DatabaseFact, Trait("Category", "Integration")]
    public async Task Fresh_wizard_then_register_forecast_save_restart_and_cancel_settings()
    {
        string original = Database.ConnectionString;
        var baseSettings = DatabaseSettings.Parse(original);
        string name = "cs_wizard_" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), name);
        var store = new DatabaseConfigurationStore(root, Path.Combine(root, "legacy"), () => null);
        var maintenance = new NpgsqlConnectionStringBuilder(original) { Database = "postgres" };
        try
        {
            await OnUiThread(async () =>
            {
                using var login = new FormLogin(store) { StartPosition = FormStartPosition.Manual, Location = new(-30000, -30000) };
                login.Show();
                FormDatabaseSetup? wizard = null;
                await WaitFor(() => (wizard = Application.OpenForms.OfType<FormDatabaseSetup>().FirstOrDefault()) != null);
                Field<TextBox>(wizard!, "host").Text = baseSettings.Host;
                Field<NumericUpDown>(wizard!, "port").Value = baseSettings.Port;
                Field<TextBox>(wizard!, "username").Text = baseSettings.Username;
                Field<TextBox>(wizard!, "password").Text = baseSettings.Password;
                Capture(wizard!, "01-database");
                Field<TextBox>(wizard!, "databaseName").Text = name;
                Click(wizard!, "Uji koneksi");
                await WaitFor(() => Field<Button>(wizard!, "test").Enabled);
                Assert.Contains("belum ada", Field<TextBox>(wizard!, "message").Text);
                Click(wizard!, "Siapkan dan simpan");
                await WaitFor(() => Field<Button>(login, "btnSubmit").Enabled);
                Assert.True(File.Exists(store.FilePath));
                Capture(login, "02-login");
                typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(Field<LinkLabel>(login, "switchMode"), [new LinkLabelLinkClickedEventArgs(new LinkLabel.Link())]);
                Field<TextBox>(login, "tbName").Text = "Mahasiswa CarbonShift";
                Field<TextBox>(login, "tbEmail").Text = "mahasiswa@contoh.id";
                Field<TextBox>(login, "tbPassword").Text = "contoh12345";
                Field<TextBox>(login, "tbConfirmation").Text = "contoh12345";
                Capture(login, "03-register");
                Click(login, "Buat akun");
                FormDashboard? dashboard = null;
                await WaitFor(() => (dashboard = Application.OpenForms.OfType<FormDashboard>().FirstOrDefault()) != null);
                await WaitFor(() => Field<Panel>(dashboard!, "content").Controls.Count > 0 &&
                    Field<Label>(Field<PageOverview>(dashboard!, "overview"), "stats").Text.StartsWith("Minggu"));
                var session = Field<AppSession>(dashboard!, "session");
                // Deterministic weather fixture; keep its DEMO label honest in the guide.
                session.SetLocation(Fixtures.Jogja);
                session.SetForecast(WeatherService.Demo(Fixtures.Jogja, DateTimeOffset.UtcNow));
                Field<ComboBox>(dashboard!, "query").Text = Fixtures.Jogja.DisplayName;
                await WaitFor(() => Field<Label>(Field<PageOverview>(dashboard!, "overview"), "stats").Text.StartsWith("Minggu"));
                Capture(dashboard!, "04-dashboard");
                Click(dashboard!, "Scheduler");
                var scheduler = Field<PageScheduler>(dashboard!, "scheduler");
                Field<DateTimePicker>(scheduler, "date").Value = Fixtures.Jogja.LocalNow(DateTimeOffset.UtcNow).Date.AddDays(1);
                Click(scheduler, "Cari rekomendasi");
                Assert.False(Field<Button>(scheduler, "save").Enabled);
                Capture(dashboard!, "05-scheduler");
                // Exercise persistence with a deterministic forecast independently of the explicit demo.
                session.SetForecast(Fixtures.Forecast(now: DateTimeOffset.UtcNow));
                Click(scheduler, "Cari rekomendasi");
                Click(scheduler, "Simpan ke riwayat");
                await WaitFor(() => Field<Label>(dashboard!, "feedback").Text.Contains("Jadwal tersimpan"));
                Click(dashboard!, "Riwayat");
                var history = Field<PageHistory>(dashboard!, "history");
                await WaitFor(() => Field<DataGridView>(history, "grid").Rows.Count == 1);
                Capture(dashboard!, "06-history");
                dashboard!.Close();
                await WaitFor(() => Field<Button>(login, "btnSubmit").Enabled);
                string current = Database.ConnectionString;
                login.BeginInvoke((Action)(() => typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(Field<LinkLabel>(login, "databaseLink"), [new LinkLabelLinkClickedEventArgs(new LinkLabel.Link())])));
                await WaitFor(() => Application.OpenForms.OfType<FormDatabaseSetup>().Any());
                Application.OpenForms.OfType<FormDatabaseSetup>().Single().Close();
                await WaitFor(() => Field<Button>(login, "btnSubmit").Enabled);
                Assert.Equal(current, Database.ConnectionString);
                login.Close();
            });
            await OnUiThread(async () =>
            {
                using var login = new FormLogin(store) { StartPosition = FormStartPosition.Manual, Location = new(-30000, -30000) };
                login.Show();
                await WaitFor(() => Field<Button>(login, "btnSubmit").Enabled);
                Assert.DoesNotContain(Application.OpenForms.OfType<Form>(), f => f is FormDatabaseSetup);
                Field<TextBox>(login, "tbEmail").Text = "mahasiswa@contoh.id";
                Field<TextBox>(login, "tbPassword").Text = "contoh12345";
                Click(login, "Masuk");
                FormDashboard? dashboard = null;
                await WaitFor(() => (dashboard = Application.OpenForms.OfType<FormDashboard>().FirstOrDefault()) != null);
                Click(dashboard!, "Riwayat");
                await WaitFor(() => Field<DataGridView>(Field<PageHistory>(dashboard!, "history"), "grid").Rows.Count == 1);
                dashboard!.Close();
                await WaitFor(() => Field<Button>(login, "btnSubmit").Enabled);
                login.Close();
            });
        }
        finally
        {
            Database.Activate(original);
            NpgsqlConnection.ClearAllPools();
            await using var server = new NpgsqlConnection(maintenance.ConnectionString);
            await server.OpenAsync();
            await using var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS " + new NpgsqlCommandBuilder().QuoteIdentifier(name) + " WITH (FORCE)", server);
            await drop.ExecuteNonQueryAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
