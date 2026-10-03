using System.Net;
using System.Net.Http.Json;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Web.Authentication;

namespace DbBackupManager.Web.Tests;

public sealed class SmtpSettingsWebTests
{
    [Fact]
    public async Task SmtpJourneyRequiresAuthenticationAndCsrfAndNeverReturnsPassword()
    {
        await using var host = await AuthTestHost.CreateAsync();
        var client = host.Client;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/notifications/smtp")).StatusCode);

        var token = await TokenAsync(client);
        using var setup = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/setup",
            new AdminSetupRequest("admin", "synthetic-password"), token);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        token = await TokenAsync(client);

        var saveRequest = new SaveSmtpSettingsRequest
        {
            Host = "smtp.example.test",
            Port = 587,
            SecurityMode = SmtpSecurityModeValue.StartTls,
            FromAddress = "ops@example.test",
            TimeoutSeconds = 30,
            IsEnabled = false,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/notifications/smtp", saveRequest)).StatusCode);
        using var saved = await SendAsync(client, HttpMethod.Put, "/api/v1/notifications/smtp", saveRequest, token);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var item = (await saved.Content.ReadFromJsonAsync<SmtpSettingsResponse>())!;
        Assert.True(await host.HasAuditAsync("smtp.settings.create"));

        using var recipient = await SendAsync(client, HttpMethod.Post, "/api/v1/notifications/smtp/recipients",
            new AddSmtpRecipientRequest { Version = item.Version, Address = "ops@example.test" }, token);
        Assert.Equal(HttpStatusCode.OK, recipient.StatusCode);
        item = (await recipient.Content.ReadFromJsonAsync<SmtpSettingsResponse>())!;

        using var password = await SendAsync(client, HttpMethod.Post, "/api/v1/notifications/smtp/password",
            new RotateSmtpPasswordRequest
            {
                Version = item.Version,
                Username = "smtp-user",
                Password = "synthetic-smtp-password",
            }, token);
        Assert.Equal(HttpStatusCode.OK, password.StatusCode);
        var raw = await password.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic-smtp-password", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("protectedSecret", raw, StringComparison.OrdinalIgnoreCase);
        item = (await password.Content.ReadFromJsonAsync<SmtpSettingsResponse>())!;
        Assert.True(item.HasCredential);

        using var enable = await SendAsync(client, HttpMethod.Post, "/api/v1/notifications/smtp/enabled",
            new SetSmtpEnabledRequest { Version = item.Version, IsEnabled = true }, token);
        item = (await enable.Content.ReadFromJsonAsync<SmtpSettingsResponse>())!;
        var requestId = Guid.NewGuid();
        using var test = await SendAsync(client, HttpMethod.Post, "/api/v1/notifications/smtp/test",
            new SendSmtpTestRequest { Version = item.Version, RequestId = requestId }, token);
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        Assert.True(await host.HasAuditAsync("smtp.test.enqueue"));
        using var replay = await SendAsync(client, HttpMethod.Post, "/api/v1/notifications/smtp/test",
            new SendSmtpTestRequest
            {
                Version = (await test.Content.ReadFromJsonAsync<SmtpSettingsResponse>())!.Version,
                RequestId = requestId,
            }, token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
    }

    private static async Task<string> TokenAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<CsrfTokenResponse>("/api/v1/auth/csrf"))!.Token;

    private static Task<HttpResponseMessage> SendAsync<T>(
        HttpClient client, HttpMethod method, string path, T value, string token)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(value) };
        request.Headers.Add(AdminAuthenticationDefaults.CsrfHeaderName, token);
        return client.SendAsync(request);
    }
}
