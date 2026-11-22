using System.Reflection;
using CarbonShift;
using Npgsql;
using static CarbonShift.Tests.UiFlowTests;

namespace CarbonShift.Tests;

[Collection("Desktop UI")]
public class RegistrationUiTests
{
    private static void Switch(FormLogin form) =>
        typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Field<LinkLabel>(form, "switchMode"), [new LinkLabelLinkClickedEventArgs(new LinkLabel.Link())]);

    [DatabaseFact, Trait("Category", "Integration")]
    public async Task Register_auto_login_logout_and_login_again()
    {
        string email = "ui-register-" + Guid.NewGuid().ToString("N") + "@example.test";
        try
        {
            await OnUiThread(async () =>
            {
                using var form = new FormLogin { StartPosition = FormStartPosition.Manual, Location = new(-30000, -30000) };
                form.Show();
                await WaitFor(() => Field<Button>(form, "btnSubmit").Enabled);
                Switch(form);
                Assert.True(Field<TableLayoutPanel>(form, "nameField").Visible);
                Field<TextBox>(form, "tbName").Text = "Pengguna Baru";
                Field<TextBox>(form, "tbEmail").Text = email;
                Field<TextBox>(form, "tbPassword").Text = "short";
                Field<TextBox>(form, "tbConfirmation").Text = "short";
                Field<Button>(form, "btnSubmit").PerformClick();
                Assert.Contains("8–128", Field<Label>(form, "lblError").Text);
                Assert.True(Field<TextBox>(form, "tbPassword").Focused);
                Field<TextBox>(form, "tbPassword").Text = "password123";
                Field<TextBox>(form, "tbConfirmation").Text = "password123";
                string preview = Path.Combine(Path.GetTempPath(), "CarbonShift-preview");
                Directory.CreateDirectory(preview);
                using (var bitmap = new Bitmap(form.Width, form.Height))
                { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(preview, "register.png")); }
                Field<Button>(form, "btnSubmit").PerformClick();
                FormDashboard? dashboard = null;
                await WaitFor(() => (dashboard = Application.OpenForms.OfType<FormDashboard>()
                    .FirstOrDefault(f => Field<AppSession>(f, "session").User.Email == email)) != null);
                Assert.False(form.Visible);
                var session = Field<AppSession>(dashboard!, "session");
                Assert.Equal("Pengguna Baru", session.User.Name);
                Assert.Empty(await RecommendationRepository.GetHistoryAsync(session.User.UserId));
                dashboard!.Close();
                await WaitFor(() => form.Visible && Field<Button>(form, "btnSubmit").Enabled);
                Assert.False(Field<TableLayoutPanel>(form, "nameField").Visible);
                Assert.Equal("", Field<TextBox>(form, "tbPassword").Text);
                Assert.Equal("", Field<TextBox>(form, "tbConfirmation").Text);
                Assert.Equal(email, Field<TextBox>(form, "tbEmail").Text);
                Field<TextBox>(form, "tbPassword").Text = "password123";
                Field<Button>(form, "btnSubmit").PerformClick();
                await WaitFor(() => (dashboard = Application.OpenForms.OfType<FormDashboard>()
                    .FirstOrDefault(f => Field<AppSession>(f, "session").User.Email == email)) != null);
                Assert.Equal(session.User.UserId, Field<AppSession>(dashboard!, "session").User.UserId);
                dashboard!.Close();
                await WaitFor(() => form.Visible && Field<Button>(form, "btnSubmit").Enabled);
                form.Close();
            });
        }
        finally
        {
            await using var conn = await Database.OpenConnectionAsync();
            var user = await UserRepository.FindByEmailAsync(email);
            if (user != null)
            {
                await RecommendationRepository.DeleteHistoryAsync(user.UserId);
                await using var cmd = new NpgsqlCommand("DELETE FROM \"user\" WHERE user_id=@id", conn);
                cmd.Parameters.AddWithValue("id", user.UserId); await cmd.ExecuteNonQueryAsync();
            }
        }
    }
}
