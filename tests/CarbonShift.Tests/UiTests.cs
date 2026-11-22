using System.Reflection;
using CarbonShift;

namespace CarbonShift.Tests;

[Collection("Desktop UI")]
public class UiTests
{
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("UI test timed out");
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static T Field<T>(object obj, string name) =>
        (T)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;

    [Fact, Trait("Category", "UI")]
    public void Input_and_forecast_changes_disable_save_and_demo_cannot_be_saved()
    {
        OnSta(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var session = new AppSession(new(1, "Pengujian", ""), new TestAppClient());
            session.SetLocation(Fixtures.Jogja); session.SetForecast(Fixtures.Forecast(now: now)); session.ForecastId = Guid.NewGuid();
            using var form = new Form { ClientSize = new(930, 650), StartPosition = FormStartPosition.Manual, Location = new(-30000, -30000) };
            var page = new PageScheduler(session, () => { });
            form.Controls.Add(page); form.Show(); page.RefreshAsync().GetAwaiter().GetResult();
            var date = Field<DateTimePicker>(page, "date");
            date.Value = Fixtures.Jogja.LocalNow(now).Date.AddDays(1);
            var find = Field<Button>(page, "find"); var save = Field<Button>(page, "save");
            find.PerformClick(); Assert.True(save.Enabled);
            Field<NumericUpDown>(page, "duration").Value = 3; Assert.False(save.Enabled);
            find.PerformClick(); Assert.True(save.Enabled);
            Field<ComboBox>(page, "activity").Text = "Aktivitas custom"; Assert.False(save.Enabled);
            find.PerformClick(); Assert.True(save.Enabled);
            Field<NumericUpDown>(page, "energy").Value = .3m; Assert.False(save.Enabled);
            find.PerformClick(); Assert.True(save.Enabled);
            session.SetForecast(Fixtures.Forecast(now: now)); session.ForecastId = Guid.NewGuid(); Assert.False(save.Enabled);
            session.SetForecast(WeatherService.Demo(Fixtures.Jogja, now));
            find.PerformClick(); Assert.False(save.Enabled);
            Assert.Contains("DEMO", Field<TextBox>(page, "explanation").Text);
            form.Close();
        });
    }

    [Theory, Trait("Category", "UI")]
    [InlineData(930, 650)] [InlineData(1200, 800)]
    public void Scheduler_scrolls_inputs_and_renders_results_at_supported_sizes(int width, int height)
    {
        OnSta(() =>
        {
            var session = new AppSession(new(1, "Pengujian", ""), new TestAppClient());
            var now = DateTimeOffset.UtcNow;
            session.SetLocation(Fixtures.Jogja); session.SetForecast(Fixtures.Forecast(now: now)); session.ForecastId = Guid.NewGuid();
            using var form = new Form { ClientSize = new(width, height), StartPosition = FormStartPosition.Manual, Location = new(-30000, -30000) };
            var page = new PageScheduler(session, () => { }); form.Controls.Add(page);
            form.Show(); page.RefreshAsync().GetAwaiter().GetResult();
            Field<DateTimePicker>(page, "date").Value = Fixtures.Jogja.LocalNow(now).Date.AddDays(1);
            Field<Button>(page, "find").PerformClick(); form.PerformLayout();
            var grid = Field<DataGridView>(page, "grid");
            Assert.Equal(10, grid.Rows.Count);
            Assert.True(Field<TextBox>(page, "explanation").Height >= 100);
            var path = Path.Combine(Path.GetTempPath(), "CarbonShift-preview");
            Directory.CreateDirectory(path);
            using var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.ClientSize));
            image.Save(Path.Combine(path, $"scheduler-{width}.png"));
            form.Close();
        });
    }
}
