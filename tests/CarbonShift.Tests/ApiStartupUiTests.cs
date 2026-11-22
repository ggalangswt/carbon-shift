using CarbonShift;
using static CarbonShift.Tests.UiFlowTests;

namespace CarbonShift.Tests;

[Collection("Desktop UI")]
public class ApiStartupUiTests
{
    [Fact]
    public Task Login_opens_without_database_configuration_and_expired_session_returns_to_login() => OnUiThread(async () =>
    {
        var client = new TestAppClient();
        using var form = new FormLogin(client) { StartPosition = FormStartPosition.Manual, Location = new(-30000, -30000) };
        form.Show();
        string images = Path.Combine(Path.GetTempPath(), "CarbonShift-preview");
        Directory.CreateDirectory(images);
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(images, "login.png"));
        }
        Assert.True(Field<Button>(form, "btnSubmit").Enabled);
        Assert.DoesNotContain(Descendants(form), c => c.Text.Contains("Pengaturan database"));
        Field<TextBox>(form, "tbEmail").Text = "test@example.test";
        Field<TextBox>(form, "tbPassword").Text = "password123";
        Field<Button>(form, "btnSubmit").PerformClick();
        await WaitFor(() => Application.OpenForms.OfType<FormDashboard>().Any());
        client.Expire();
        await WaitFor(() => form.Visible && !Application.OpenForms.OfType<FormDashboard>().Any());
        Assert.Equal("", Field<TextBox>(form, "tbPassword").Text);
        form.Close();
    });
    private static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
}
