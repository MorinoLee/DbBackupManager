using System.Net;
using System.Net.Http.Json;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Web.Authentication;

namespace DbBackupManager.Web.Tests;

public sealed class SqlCredentialsWebTests
{
    [Fact]
    public async Task CredentialJourneyRequiresAuthenticationAndCsrfAndNeverReturnsSecrets()
    {
        await using var host = await AuthTestHost.CreateAsync();
        var client = host.Client;
        using var anonymous = await client.GetAsync("/api/v1/sql-credentials");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var anonymousPage = await client.GetAsync("/sql-credentials");
        Assert.Equal(HttpStatusCode.Redirect, anonymousPage.StatusCode);

        var token = await TokenAsync(client);
        using var setup = await PostAsync(client, "/api/v1/auth/setup", new AdminSetupRequest("admin", "synthetic-password"), token);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        token = await TokenAsync(client);
        var input = new CreateSqlCredentialRequest { Name = "测试凭据", Username = "synthetic-login", Password = "synthetic-sql-password" };
        using var missingCsrf = await client.PostAsJsonAsync("/api/v1/sql-credentials", input);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        using var create = await PostAsync(client, "/api/v1/sql-credentials", input, token);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var raw = await create.Content.ReadAsStringAsync();
        Assert.DoesNotContain(input.Password, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("protectedSecret", raw, StringComparison.OrdinalIgnoreCase);
        var item = (await create.Content.ReadFromJsonAsync<SqlCredentialResponse>())!;
        Assert.True(await host.HasAuditAsync("credential.create"));
        using var list = await client.GetAsync("/api/v1/sql-credentials");
        Assert.True(list.Headers.CacheControl?.NoStore);
        Assert.Single((await list.Content.ReadFromJsonAsync<SqlCredentialResponse[]>())!);

        using var rotate = await PostAsync(client, $"/api/v1/sql-credentials/{item.Id}/password",
            new RotateSqlCredentialPasswordRequest { Version = item.Version, Password = "synthetic-rotated" }, token);
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        var updated = (await rotate.Content.ReadFromJsonAsync<SqlCredentialResponse>())!;
        using var missingEnabled = await PostAsync(client, $"/api/v1/sql-credentials/{item.Id}/enabled",
            new { updated.Version }, token);
        Assert.Equal(HttpStatusCode.BadRequest, missingEnabled.StatusCode);
        using var stale = await PostAsync(client, $"/api/v1/sql-credentials/{item.Id}/enabled",
            new SetSqlCredentialEnabledRequest { Version = item.Version, IsEnabled = false }, token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var disable = await PostAsync(client, $"/api/v1/sql-credentials/{item.Id}/enabled",
            new SetSqlCredentialEnabledRequest { Version = updated.Version, IsEnabled = false }, token);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.False((await disable.Content.ReadFromJsonAsync<SqlCredentialResponse>())!.IsEnabled);
        using var page = await client.GetAsync("/sql-credentials");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.DoesNotContain(input.Password, await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task<string> TokenAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<CsrfTokenResponse>("/api/v1/auth/csrf"))!.Token;

    private static Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string path, T value, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(value) };
        request.Headers.Add(AdminAuthenticationDefaults.CsrfHeaderName, token);
        return client.SendAsync(request);
    }
}
