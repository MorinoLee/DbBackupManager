using System.Net;
using System.Net.Http.Json;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Web.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DbBackupManager.Web.Tests;

public sealed class BackupManagementWebTests
{
    [Fact]
    public async Task ManualBackupApiQueuesIdempotentlyAndCancelRequiresIdentityAndCsrf()
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
        using var storage = await SendAsync(client, HttpMethod.Post, "/api/v1/storage-targets",
            new SaveStorageTargetRequest
            {
                Name = "合成远程目标",
                Protocol = StorageProtocolValue.Smb,
                Host = "synthetic-host",
                BasePath = "synthetic-share",
                CredentialId = fileItem.Id,
                IsEnabled = true,
            }, token);
        var target = (await storage.Content.ReadFromJsonAsync<StorageTargetResponse>())!;
        Assert.NotEqual(Guid.Empty, target.Id);
        using var planResponse = await SendAsync(client, HttpMethod.Post, "/api/v1/backup-plans", new SaveBackupPlanRequest
        {
            DatabaseId = database.Id,
            Name = "合成备份计划",
            Mode = 1,
            FullSchedule = new BackupPlanScheduleRequest { ScheduleType = 2, LocalTime = new TimeOnly(1, 15), DaysOfWeek = 1 },
            TimeZoneId = "UTC",
            StorageMode = 1,
            LocalRecoveryWindowDays = 7,
            UseCompression = false,
            BackupTimeoutMinutes = 60,
            VerifyTimeoutMinutes = 30,
            TransferTimeoutMinutes = 45,
            IsPaused = true,
        }, token);
        Assert.Equal(HttpStatusCode.OK, planResponse.StatusCode);
        var plan = (await planResponse.Content.ReadFromJsonAsync<BackupPlanResponse>())!;
        var requestId = Guid.NewGuid();
        using (var scope = host.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var version = await db.BackupPlanVersions.SingleAsync(item => item.Id == plan.CurrentVersionId);
            var managed = await db.ManagedDatabases.SingleAsync(item => item.Id == database.Id);
            var instanceEntity = await db.DatabaseInstances.SingleAsync(item => item.Id == managed.InstanceId);
            var serverEntity = await db.DatabaseServers.SingleAsync(item => item.Id == instanceEntity.ServerId);
            var task = BackupTask.ForPlan(requestId, plan.Id, version.Id, BackupRunPurpose.PlanFull, BackupTaskTriggerType.Manual, null);
            var snapshot = BackupTaskSnapshot.ForPlan(task, version, plan.Name, BackupRunPurpose.PlanFull,
                new(serverEntity.Id, serverEntity.Name, instanceEntity.Id, instanceEntity.Name, managed.Id, managed.DatabaseName),
                new(instanceEntity.ConnectionAddress, instanceEntity.SqlCredentialReferenceId, true, false, null, instanceEntity.ConnectionTimeoutSeconds),
                new(serverEntity.LocalBackupRootPath, "v3", new(serverEntity.StagingAccessProtocol, serverEntity.StagingAccessHost,
                    serverEntity.StagingAccessPort, serverEntity.StagingAccessBasePath, serverEntity.StagingCredentialReferenceId,
                    serverEntity.StagingSftpHostKeyFingerprint)));
            db.AddRange(task, snapshot);
            await db.SaveChangesAsync();
        }
        var dashboard = (await client.GetFromJsonAsync<BackupDashboardResponse>("/api/v1/backup-dashboard"))!;
        Assert.Single(dashboard.Tasks); Assert.Equal("Pending", dashboard.Tasks[0].Status);
        using var detailResponse = await client.GetAsync($"/api/v1/backup-tasks/{requestId}");
        var raw = await detailResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("leaseToken", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rowVersion", raw, StringComparison.OrdinalIgnoreCase);
        using var cancel = await SendAsync(client, HttpMethod.Post, $"/api/v1/backup-tasks/{requestId}/cancel", new BackupTaskCommandRequest { RequestId = Guid.NewGuid() }, token);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var detail = (await client.GetFromJsonAsync<BackupTaskDetailResponse>($"/api/v1/backup-tasks/{requestId}"))!;
        Assert.Equal("Cancelled", detail.Task.Status);
        Assert.Equal(0, detail.Reconciliation.AttemptCount);
        Assert.Null(detail.Reconciliation.NextAtUtc);
        Assert.False(detail.Reconciliation.CanRequest);

        using var serviceScope = host.CreateScope();
        var store = serviceScope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(BackupTaskStoreResultCode.NotFound, (await store.ClaimTaskAsync(requestId,
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "web-plan-task", now, now.AddMinutes(5)))).Code);
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch, (await store.RetryFailedAsync(
            new(requestId, Guid.NewGuid(), now))).Code);
        // 旧 /backups 入口保留兼容跳转，指向拆分后的任务记录页。
        using var backupsPage = await client.GetAsync("/backups");
        Assert.Equal(HttpStatusCode.Redirect, backupsPage.StatusCode);
        Assert.Contains("/backup-tasks", backupsPage.Headers.Location?.OriginalString, StringComparison.Ordinal);
        using var tasksPage = await client.GetAsync("/backup-tasks");
        Assert.Equal(HttpStatusCode.OK, tasksPage.StatusCode);

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
