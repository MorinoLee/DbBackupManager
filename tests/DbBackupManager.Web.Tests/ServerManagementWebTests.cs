using System.Net;
using System.Net.Http.Json;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Web.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DbBackupManager.Web.Tests;

public sealed class ServerManagementWebTests
{
    [Fact]
    public async Task AuthenticatedJourneyCreatesServerAndInstanceDiscoversAndManagesDatabase()
    {
        await using var host = await AuthTestHost.CreateAsync(services =>
        {
            services.RemoveAll<ITargetSqlReadOnlyProbe>(); services.AddSingleton<ITargetSqlReadOnlyProbe, FakeProbe>();
        });
        var client = host.Client;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/server-inventory")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/servers")).StatusCode);
        var token = (await client.GetFromJsonAsync<CsrfTokenResponse>("/api/v1/auth/csrf"))!.Token;
        using var setup = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/setup", new AdminSetupRequest("admin", "synthetic-password"), token);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        token = (await client.GetFromJsonAsync<CsrfTokenResponse>("/api/v1/auth/csrf"))!.Token;
        using var file = await SendAsync(client, HttpMethod.Post, "/api/v1/file-credentials",
            new CreateFileCredentialRequest { Name = "SMB", Username = "synthetic", Kind = FileCredentialKindValue.SmbPassword, Password = "synthetic-password" }, token);
        var fileItem = (await file.Content.ReadFromJsonAsync<FileCredentialResponse>())!;
        using var sql = await SendAsync(client, HttpMethod.Post, "/api/v1/sql-credentials",
            new CreateSqlCredentialRequest { Name = "SQL", Username = "synthetic", Password = "synthetic-password" }, token);
        var sqlItem = (await sql.Content.ReadFromJsonAsync<SqlCredentialResponse>())!;
        var serverRequest = new SaveServerRequest
        {
            Name = "测试服务器",
            LocalBackupRootPath = @"D:\Synthetic",
            Protocol = 1,
            Host = "synthetic-host",
            BasePath = "synthetic-share",
            CredentialId = fileItem.Id,
            IsEnabled = true
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/servers", serverRequest)).StatusCode);
        using var createdServer = await SendAsync(client, HttpMethod.Post, "/api/v1/servers", serverRequest, token);
        Assert.Equal(HttpStatusCode.OK, createdServer.StatusCode);
        var server = (await createdServer.Content.ReadFromJsonAsync<ServerResponse>())!;
        var instanceRequest = new SaveInstanceRequest
        {
            ServerId = server.Id,
            Name = "默认实例",
            ConnectionAddress = "synthetic-host",
            SqlCredentialId = sqlItem.Id,
            TrustServerCertificate = false,
            ConnectionTimeoutSeconds = 15,
            IsEnabled = true
        };
        using var createdInstance = await SendAsync(client, HttpMethod.Post, "/api/v1/instances", instanceRequest, token);
        Assert.Equal(HttpStatusCode.OK, createdInstance.StatusCode);
        var instance = (await createdInstance.Content.ReadFromJsonAsync<InstanceResponse>())!;
        using var discover = await SendAsync(client, HttpMethod.Post, $"/api/v1/instances/{instance.Id}/discover",
            new InstanceProbeRequest { Version = instance.Version }, token);
        Assert.Equal(HttpStatusCode.OK, discover.StatusCode);
        using var stale = await SendAsync(client, HttpMethod.Post, $"/api/v1/instances/{instance.Id}/probe",
            new InstanceProbeRequest { Version = instance.Version }, token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var inventoryResponse = await client.GetAsync("/api/v1/server-inventory");
        Assert.True(inventoryResponse.Headers.CacheControl?.NoStore);
        var inventory = (await inventoryResponse.Content.ReadFromJsonAsync<ServerInventoryResponse>())!;
        var database = Assert.Single(inventory.Databases);
        Assert.False(database.IsManaged);
        using var missingManaged = await SendAsync(client, HttpMethod.Post, $"/api/v1/databases/{database.Id}/managed", new { database.Version }, token);
        Assert.Equal(HttpStatusCode.BadRequest, missingManaged.StatusCode);
        using var manage = await SendAsync(client, HttpMethod.Post, $"/api/v1/databases/{database.Id}/managed",
            new SetDatabaseManagedRequest { Version = database.Version, IsManaged = true }, token);
        Assert.Equal(HttpStatusCode.OK, manage.StatusCode);
        Assert.True((await manage.Content.ReadFromJsonAsync<DatabaseResponse>())!.IsManaged);
        Assert.True(await host.HasAuditAsync("database.manage"));
        using var page = await client.GetAsync("/servers");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.DoesNotContain("synthetic-password", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var updateWithoutVersion = await SendAsync(client, HttpMethod.Put, $"/api/v1/servers/{server.Id}", serverRequest, token);
        Assert.Equal(HttpStatusCode.BadRequest, updateWithoutVersion.StatusCode);
    }
    private static Task<HttpResponseMessage> SendAsync<T>(HttpClient client, HttpMethod method, string path, T value, string token)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(value) };
        request.Headers.Add(AdminAuthenticationDefaults.CsrfHeaderName, token); return client.SendAsync(request);
    }
    private sealed class FakeProbe : ITargetSqlReadOnlyProbe
    {
        public Task<TargetSqlResult<TargetSqlServerInfo>> ProbeServerAsync(TargetSqlConnectionInput connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(TargetSqlResult.Succeeded(new TargetSqlServerInfo("15.0.synthetic", "synthetic", "synthetic", 3, 15, true)));
        public Task<TargetSqlResult<TargetSqlDatabaseCatalog>> DiscoverDatabasesAsync(TargetSqlConnectionInput connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(TargetSqlResult.Succeeded(new TargetSqlDatabaseCatalog([new("synthetic-db", TargetSqlDatabaseState.Online, TargetSqlRecoveryModel.Full, false, false, true)])));
        public Task<TargetSqlResult<TargetSqlBackupVerification>> VerifyBackupAsync(TargetSqlConnectionInput connection, TargetSqlBackupVerificationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
