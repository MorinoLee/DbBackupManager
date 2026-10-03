using System.Net;
using System.Net.Http.Json;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Web.Authentication;

namespace DbBackupManager.Web.Tests;

public sealed class FileCredentialsWebTests
{
    [Fact]
    public async Task CredentialJourneyRequiresAuthenticationAndCsrfAndNeverReturnsSecrets()
    {
        await using var host = await AuthTestHost.CreateAsync();
        var client = host.Client;
        using var anonymous = await client.GetAsync("/api/v1/file-credentials");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var anonymousPage = await client.GetAsync("/file-credentials");
        Assert.Equal(HttpStatusCode.Redirect, anonymousPage.StatusCode);

        var token = await TokenAsync(client);
        using var setup = await PostAsync(client, "/api/v1/auth/setup", new AdminSetupRequest("admin", "synthetic-password"), token);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        token = await TokenAsync(client);
        var input = new CreateFileCredentialRequest { Kind = FileCredentialKindValue.SmbPassword, Name = "测试凭据", Username = "synthetic-login", Password = "synthetic-sql-password" };
        using var missingCsrf = await client.PostAsJsonAsync("/api/v1/file-credentials", input);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        using var create = await PostAsync(client, "/api/v1/file-credentials", input, token);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var raw = await create.Content.ReadAsStringAsync();
        Assert.DoesNotContain(input.Password, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("protectedSecret", raw, StringComparison.OrdinalIgnoreCase);
        var item = (await create.Content.ReadFromJsonAsync<FileCredentialResponse>())!;
        Assert.True(await host.HasAuditAsync("credential.create"));
        using var list = await client.GetAsync("/api/v1/file-credentials");
        Assert.True(list.Headers.CacheControl?.NoStore);
        Assert.Single((await list.Content.ReadFromJsonAsync<FileCredentialResponse[]>())!);

        using var rotate = await PostAsync(client, $"/api/v1/file-credentials/{item.Id}/password",
            new RotateFileCredentialPasswordRequest { Version = item.Version, Password = "synthetic-rotated" }, token);
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        var updated = (await rotate.Content.ReadFromJsonAsync<FileCredentialResponse>())!;
        using var missingEnabled = await PostAsync(client, $"/api/v1/file-credentials/{item.Id}/enabled",
            new { updated.Version }, token);
        Assert.Equal(HttpStatusCode.BadRequest, missingEnabled.StatusCode);
        using var stale = await PostAsync(client, $"/api/v1/file-credentials/{item.Id}/enabled",
            new SetFileCredentialEnabledRequest { Version = item.Version, IsEnabled = false }, token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var disable = await PostAsync(client, $"/api/v1/file-credentials/{item.Id}/enabled",
            new SetFileCredentialEnabledRequest { Version = updated.Version, IsEnabled = false }, token);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.False((await disable.Content.ReadFromJsonAsync<FileCredentialResponse>())!.IsEnabled);
        using var page = await client.GetAsync("/file-credentials");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.DoesNotContain(input.Password, await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        const string privateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nsynthetic\n-----END OPENSSH PRIVATE KEY-----";
        using var passwordAsKey = await PostAsync(client, "/api/v1/file-credentials",
            new CreateFileCredentialRequest
            {
                Kind = FileCredentialKindValue.SftpPrivateKey,
                Name = "错误私钥入口",
                Username = "synthetic-login",
                Password = "synthetic-sql-password",
            }, token);
        Assert.Equal(HttpStatusCode.BadRequest, passwordAsKey.StatusCode);
        using var createdKey = await PostAsync(client, "/api/v1/file-credentials/private-key",
            new CreateSftpPrivateKeyRequest
            {
                Name = "测试私钥",
                Username = "synthetic-key",
                PrivateKey = privateKey,
                Passphrase = "synthetic-passphrase",
            }, token);
        Assert.Equal(HttpStatusCode.OK, createdKey.StatusCode);
        var keyRaw = await createdKey.Content.ReadAsStringAsync();
        Assert.DoesNotContain(privateKey, keyRaw, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-passphrase", keyRaw, StringComparison.Ordinal);
        var key = (await createdKey.Content.ReadFromJsonAsync<FileCredentialResponse>())!;
        Assert.Equal(FileCredentialKindValue.SftpPrivateKey, key.Kind);
        Assert.True(key.HasPassphrase);
        using var rotateKey = await PostAsync(client, $"/api/v1/file-credentials/{key.Id}/private-key",
            new RotateSftpPrivateKeyRequest { Version = key.Version, PrivateKey = privateKey }, token);
        Assert.Equal(HttpStatusCode.OK, rotateKey.StatusCode);
        Assert.False((await rotateKey.Content.ReadFromJsonAsync<FileCredentialResponse>())!.HasPassphrase);
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
