using System.Net;
using System.Reflection;
using CarbonShift;
using Npgsql;

namespace CarbonShift.Tests;

[Collection("Desktop UI")]
public class UiFlowTests
{
    internal static T Field<T>(object obj, string name) =>
        (T)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
    internal static async Task WaitFor(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!predicate()) await Task.Delay(50, timeout.Token);
    }
    internal static Task OnUiThread(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(-30000, -30000) };
            host.Shown += async (_, _) =>
            {
                try { await action(); done.SetResult(); }
                catch (Exception ex) { done.SetException(ex); }
                finally { host.Close(); }
            };
            Application.Run(host);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
    private static void Capture(FormDashboard form, string name)
    {
        form.PerformLayout();
        string root = Path.Combine(Path.GetTempPath(), "CarbonShift-preview");
        Directory.CreateDirectory(root);
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
        image.Save(Path.Combine(root, name + ".png"));
    }
    private static void Click(Control parent, string text)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is Button button && button.Text == text) { button.PerformClick(); return; }
            if (Descendants(control).OfType<Button>().FirstOrDefault(b => b.Text == text) is { } found)
            { found.PerformClick(); return; }
        }
        throw new InvalidOperationException("Button missing: " + text);
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            yield return control;
            foreach (var child in Descendants(control)) yield return child;
        }
    }

    [DatabaseFact, Trait("Category", "Integration")]
    public async Task Actual_ui_selects_location_saves_reloads_history_and_shows_dashboard()
    {
        await using var conn = await Database.OpenConnectionAsync();
        await using var insert = new NpgsqlCommand(
            "INSERT INTO \"user\" (name,email,password_hash) VALUES ('UI test',@email,@hash) RETURNING user_id", conn);
        insert.Parameters.AddWithValue("email", "ui-" + Guid.NewGuid().ToString("N") + "@carbonshift.test");
        insert.Parameters.AddWithValue("hash", PasswordHasher.Hash("ui-only"));
        int id = Convert.ToInt32(await insert.ExecuteScalarAsync());
        var store = new MemoryStore();
        var api = new ApiClient(new HttpClient(new FakeHandler((request, _) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.Host.StartsWith("geocoding") ?
                """{"results":[{"name":"Yogyakarta","admin1":"DI Yogyakarta","country_code":"ID","latitude":-7.8,"longitude":110.36,"timezone":"Asia/Jakarta"}]}""" :
                WeatherTests.Payload(now: DateTimeOffset.UtcNow))
        })));
        var user = new UserAccount(id, "UI test", "", "");
        try
        {
            await OnUiThread(async () =>
            {
                using var form = new FormDashboard(user, store, api) { StartPosition = FormStartPosition.Manual, Location = new(-30000, -30000) };
                form.Show();
                await WaitFor(() => Field<TextBox>(form, "status").Text.Contains("Belum ada lokasi"));
                var city = Field<ComboBox>(form, "query");
                city.Text = "Yogyakarta"; Click(form, "Cari kota");
                await WaitFor(() => city.Items.Count > 0);
                city.SelectedIndex = 0;
                typeof(ComboBox).GetMethod("OnSelectionChangeCommitted", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(city, [EventArgs.Empty]);
                await WaitFor(() => Field<TextBox>(form, "status").Text.Contains("Open-Meteo"));
                Capture(form, "dashboard");
                var logout = Descendants(form).OfType<Button>().Single(b => b.Text == "Logout");
                var dashboardButton = Descendants(form).OfType<Button>().Single(b => b.Text == "Dashboard");
                Assert.Equal(dashboardButton.Width, logout.Width);
                Assert.True(logout.BackColor.R > logout.BackColor.G * 2);
                Assert.True(logout.Parent!.ClientSize.Height - logout.Bottom <= logout.Parent.Padding.Bottom + 8);
                var brand = Descendants(form).OfType<Label>().Single(l => l.Text == "CarbonShift");
                Assert.True(brand.ClientSize.Width >= TextRenderer.MeasureText(brand.Text, brand.Font).Width);
                Click(form, "Forecast");
                Assert.False(Field<TableLayoutPanel>(form, "header").Visible);
                Assert.True(Field<Panel>(form, "content").Top <= 6);
                Assert.Equal(24, Field<DataGridView>(Field<PageForecast>(form, "forecast"), "grid").Rows.Count);
                Capture(form, "forecast");
                Click(form, "Scheduler");
                var page = Field<PageScheduler>(form, "scheduler");
                Field<DateTimePicker>(page, "date").Value = Fixtures.Jogja.LocalNow(DateTimeOffset.UtcNow).Date.AddDays(1);
                Click(page, "Cari rekomendasi"); Assert.True(Field<Button>(page, "save").Enabled);
                Capture(form, "scheduler-app");
                form.ClientSize = new(980, 680);
                Field<Button>(page, "save").Parent!.Parent!.Select();
                ((ScrollableControl)Field<Button>(page, "save").Parent!.Parent!).ScrollControlIntoView(Field<Button>(page, "save"));
                Capture(form, "scheduler-small");
                form.ClientSize = new(1200, 820);
                Click(page, "Simpan ke riwayat");
                await WaitFor(() => Field<Label>(form, "feedback").Text.Contains("Jadwal tersimpan"));
                Click(form, "Riwayat");
                var history = Field<PageHistory>(form, "history");
                await WaitFor(() => Field<DataGridView>(history, "grid").Rows.Count == 1);
                Capture(form, "history");
                Click(form, "Dashboard");
                Assert.True(Field<TableLayoutPanel>(form, "header").Visible);
                await WaitFor(() => Field<Label>(Field<PageOverview>(form, "overview"), "next").Text.Contains("Mengisi daya laptop"));
                form.ClientSize = new(980, 680); Capture(form, "dashboard-small");
                form.Close();
            });
            await OnUiThread(async () =>
            {
                using var reopened = new FormDashboard(user, store, api) { StartPosition = FormStartPosition.Manual, Location = new(-30000, -30000) };
                reopened.Show();
                await WaitFor(() => Field<TextBox>(reopened, "status").Text.Contains("Open-Meteo"));
                Click(reopened, "Riwayat");
                await WaitFor(() => Field<DataGridView>(Field<PageHistory>(reopened, "history"), "grid").Rows.Count == 1);
                reopened.Close();
            });
        }
        finally
        {
            await RecommendationRepository.DeleteHistoryAsync(id);
            await using var delete = new NpgsqlCommand("DELETE FROM \"user\" WHERE user_id=@id", conn);
            delete.Parameters.AddWithValue("id", id); await delete.ExecuteNonQueryAsync();
        }
    }
}
