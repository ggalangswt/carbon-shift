using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarbonShift;

namespace CarbonShift.Tests;

public class SupabaseClientTests
{
    static readonly SupabasePublicSettings Settings = new("https://example.supabase.co/", "sb_publishable_test_value_for_client_only");
    [Theory]
    [InlineData("anon", true)]
    [InlineData("service_role", false)]
    public void Legacy_keys_accept_only_the_public_anon_role(string role, bool expected)
    {
        string payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { role }))).TrimEnd('=').Replace('+','-').Replace('/','_');
        Assert.Equal(expected, (Settings with { PublishableKey = "header." + payload + ".signature" }).Valid);
    }
    [Fact]
    public void Preview_transport_preserves_the_server_snapshot_and_explanation()
    {
        var result = Fixtures.Result();
        var preview = new PreviewResponse(Guid.NewGuid(), Fixtures.Now.AddMinutes(15), result.Request, result.Forecast,
            "weighted", result.Slots.Take(10).Select(SlotResponse.From).ToArray(), "Penjelasan persis dari layanan.");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var decoded = JsonSerializer.Deserialize<PreviewResponse>(JsonSerializer.Serialize(preview, options), options)!;
        var displayed = decoded.ToResult();
        Assert.Equal(preview.Explanation, displayed.Explanation);
        Assert.Equal(result.Request, displayed.Request);
        Assert.Equal(result.Forecast.Location, displayed.Forecast.Location);
        Assert.Equal(result.Best.GreenScore, displayed.Best.GreenScore, 8);
        Assert.Equal(result.Best.Start, displayed.Best.Start);
    }
    [Theory]
    [InlineData("sb_secret_123456789012345678901234567890")]
    [InlineData("postgres-password")]
    public void Secret_keys_are_rejected(string key) => Assert.False((Settings with { PublishableKey = key }).Valid);

    [Fact]
    public async Task Login_and_Rpc_use_native_token_and_never_send_user_id()
    {
        string? authHeader = null; string? path = null; string? requestBody = null;
        var handler = new CallbackHandler(async request =>
        {
            Assert.Equal(Settings.PublishableKey, request.Headers.GetValues("apikey").Single());
            path = request.RequestUri!.AbsolutePath; requestBody = await request.Content!.ReadAsStringAsync();
            authHeader = request.Headers.Authorization?.Parameter;
            return path.EndsWith("carbonshift-auth") ? new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new AuthResponse("native-token", DateTimeOffset.UtcNow.AddHours(1), new(42, "User", "user@example.test")))
            } : new(HttpStatusCode.OK) { Content = new StringContent("[]") };
        });
        using var client = new SupabaseAppClient(new HttpClient(handler) { BaseAddress = new(Settings.Url) }, Settings);
        var user = await client.LoginAsync("user@example.test", "password123"); Assert.Equal(42, user.UserId);
        Assert.Null(authHeader); Assert.Contains("login", requestBody);
        Assert.Empty(await client.HistoryAsync());
        Assert.Equal("native-token", authHeader); Assert.Equal("/rest/v1/rpc/carbonshift_history", path);
        Assert.DoesNotContain("userId", requestBody!);
        client.Logout();
        var error = await Assert.ThrowsAsync<CarbonShiftApiException>(() => client.HistoryAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, error.Status);
    }
    [Fact]
    public async Task Sql_errors_stay_private_and_session_expiry_is_reported()
    {
        int calls = 0; bool expired = false;
        using var client = new SupabaseAppClient(new HttpClient(new CallbackHandler(_ =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new AuthResponse("token", DateTimeOffset.UtcNow.AddHours(1), new(1,"A","a@test.example"))) } :
                new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"message\":\"private SQL details\"}") });
        })) { BaseAddress = new(Settings.Url) }, Settings);
        client.SessionExpired += (_, _) => expired = true;
        await client.LoginAsync("a@test.example", "password123");
        var error = await Assert.ThrowsAsync<CarbonShiftApiException>(() => client.HistoryAsync());
        Assert.True(expired); Assert.DoesNotContain("SQL", error.Message);
    }
    [Fact]
    public async Task Late_login_cannot_restore_logged_out_session()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>();
        using var client = new SupabaseAppClient(new HttpClient(new CallbackHandler(_ => response.Task)) { BaseAddress = new(Settings.Url) }, Settings);
        var login = client.LoginAsync("a@test.example", "password123"); client.Logout();
        response.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(new AuthResponse("token", DateTimeOffset.UtcNow.AddHours(1), new(1,"A","a@test.example"))) });
        await Assert.ThrowsAsync<CarbonShiftApiException>(() => login);
        await Assert.ThrowsAsync<CarbonShiftApiException>(() => client.HistoryAsync());
    }
    private sealed class CallbackHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }
}
