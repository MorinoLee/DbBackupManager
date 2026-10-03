using System.Net;
using System.Net.Http.Json;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Web.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DbBackupManager.Web.Tests;

public sealed class StorageTargetsWebTests
{
    [Fact]
    public async Task StorageTargetJourneyRequiresAuthenticationAndCsrfAndRejectsMismatchedCredentials()
    {
        await using var host = await AuthTestHost.CreateAsync(services =>
        {
            services.RemoveAll<IBackupFileStorageProbe>();
            services.AddSingleton<IBackupFileStorageProbe, FakeFileProbe>();
        });
        var client = host.Client;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/storage-targets")).StatusCode);
        var token = await TokenAsync(client);
        using var setup = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/setup",
            new AdminSetupRequest("admin", "synthetic-password"), token);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        token = await TokenAsync(client);

        using var smb = await SendAsync(client, HttpMethod.Post, "/api/v1/file-credentials",
            new CreateFileCredentialRequest
            {
                Name = "SMB",
                Username = "synthetic",
                Kind = FileCredentialKindValue.SmbPassword,
                Password = "synthetic-password",
            }, token);
        var smbItem = (await smb.Content.ReadFromJsonAsync<FileCredentialResponse>())!;
        using var sftp = await SendAsync(client, HttpMethod.Post, "/api/v1/file-credentials",
            new CreateFileCredentialRequest
            {
                Name = "SFTP",
                Username = "synthetic",
                Kind = FileCredentialKindValue.SftpPassword,
                Password = "synthetic-password",
            }, token);
        var sftpItem = (await sftp.Content.ReadFromJsonAsync<FileCredentialResponse>())!;

        var smbRequest = new SaveStorageTargetRequest
        {
            Name = "合成 SMB 目标",
            Protocol = StorageProtocolValue.Smb,
            Host = "synthetic-host",
            BasePath = "synthetic-share",
            CredentialId = smbItem.Id,
            IsEnabled = true,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/storage-targets", smbRequest)).StatusCode);
        using var created = await SendAsync(client, HttpMethod.Post, "/api/v1/storage-targets", smbRequest, token);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var item = (await created.Content.ReadFromJsonAsync<StorageTargetResponse>())!;
        Assert.True(await host.HasAuditAsync("storage_target.create"));

        using var mismatched = await SendAsync(client, HttpMethod.Post, "/api/v1/storage-targets",
            new SaveStorageTargetRequest
            {
                Name = "错误凭据",
                Protocol = StorageProtocolValue.Smb,
                Host = "synthetic-host",
                BasePath = "synthetic-share",
                CredentialId = sftpItem.Id,
                IsEnabled = true,
            }, token);
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
        using var badFingerprint = await SendAsync(client, HttpMethod.Post, "/api/v1/storage-targets",
            new SaveStorageTargetRequest
            {
                Name = "错误指纹",
                Protocol = StorageProtocolValue.Sftp,
                Host = "synthetic-host",
                Port = 22,
                BasePath = "/archives",
                CredentialId = sftpItem.Id,
                Fingerprint = "SHA256:bad",
                IsEnabled = true,
            }, token);
        Assert.Equal(HttpStatusCode.BadRequest, badFingerprint.StatusCode);

        using var list = await client.GetAsync("/api/v1/storage-targets");
        Assert.True(list.Headers.CacheControl?.NoStore);
        Assert.Single((await list.Content.ReadFromJsonAsync<StorageTargetResponse[]>())!);

        using var probe = await SendAsync(client, HttpMethod.Post, $"/api/v1/storage-targets/{item.Id}/probe",
            new StorageTargetVersionRequest { Version = item.Version }, token);
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        Assert.True(await host.HasAuditAsync("storage_target.probe"));
        Assert.DoesNotContain("synthetic-password", await probe.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task<string> TokenAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<CsrfTokenResponse>("/api/v1/auth/csrf"))!.Token;

    private static Task<HttpResponseMessage> SendAsync<T>(HttpClient client, HttpMethod method, string path, T value, string token)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(value) };
        request.Headers.Add(AdminAuthenticationDefaults.CsrfHeaderName, token);
        return client.SendAsync(request);
    }

    private sealed class FakeFileProbe : IBackupFileStorageProbe
    {
        public Task<BackupFileStorageResult<BackupFileMetadata>> InspectAsync(
            BackupFileEndpointInput endpoint, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(BackupFileStorageResult.Succeeded(new BackupFileMetadata(false, false, null)));
    }
}
