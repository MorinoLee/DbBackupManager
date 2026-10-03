using System.Net;
using System.Net.Http.Json;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Domain.BackupTasks;
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
        using var remotePolicyResponse = await SendAsync(client, HttpMethod.Post, "/api/v1/manual-backup-policies",
            new SaveManualBackupRequest
            {
                DatabaseId = database.Id,
                Name = "合成远程手动备份",
                RetentionDays = 7,
                BackupTimeoutMinutes = 120,
                VerifyTimeoutMinutes = 60,
                UseCompression = false,
                IsEnabled = true,
                StorageMode = 2,
                StorageTargetId = target.Id,
                RemoteRetentionDays = 14,
                TransferTimeoutMinutes = 90,
            }, token);
        Assert.Equal(HttpStatusCode.OK, remotePolicyResponse.StatusCode);
        var remotePolicy = (await remotePolicyResponse.Content.ReadFromJsonAsync<ManualBackupPolicyResponse>())!;
        Assert.Equal(2, remotePolicy.Settings.StorageMode);
        Assert.Equal(target.Id, remotePolicy.Settings.StorageTargetId);
        Assert.Equal(14, remotePolicy.Settings.RemoteRetentionDays);
        using var rejectedLocalWithTarget = await SendAsync(client, HttpMethod.Post, "/api/v1/manual-backup-policies",
            new SaveManualBackupRequest
            {
                DatabaseId = database.Id,
                Name = "本地不应带目标",
                RetentionDays = 7,
                BackupTimeoutMinutes = 120,
                VerifyTimeoutMinutes = 60,
                UseCompression = false,
                IsEnabled = true,
                StorageTargetId = target.Id,
            }, token);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedLocalWithTarget.StatusCode);
        var policy = remotePolicy;
        var requestId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/manual-backup-policies/{policy.Id}/run", new BackupTaskCommandRequest { RequestId = requestId })).StatusCode);
        using var run = await SendAsync(client, HttpMethod.Post, $"/api/v1/manual-backup-policies/{policy.Id}/run", new BackupTaskCommandRequest { RequestId = requestId }, token);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        using var replay = await SendAsync(client, HttpMethod.Post, $"/api/v1/manual-backup-policies/{policy.Id}/run", new BackupTaskCommandRequest { RequestId = requestId }, token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
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

        var reconciliationTaskId = Guid.NewGuid();
        using var secondRun = await SendAsync(client, HttpMethod.Post, $"/api/v1/manual-backup-policies/{policy.Id}/run",
            new BackupTaskCommandRequest { RequestId = reconciliationTaskId }, token);
        Assert.Equal(HttpStatusCode.OK, secondRun.StatusCode);
        using var serviceScope = host.CreateScope();
        var store = serviceScope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var now = DateTimeOffset.UtcNow;
        var claimed = await store.ClaimTaskAsync(
            reconciliationTaskId,
            new ClaimNextBackupTaskCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "web-reconciliation-test",
                now,
                now.AddMinutes(5)));
        var marked = await store.MarkBackupInvocationStartedAsync(
            claimed.Value!.Lease,
            claimed.Value.Attempt.RowVersion,
            now.AddSeconds(1));
        var unknown = await store.CommitStageAsync(
            marked.Value!,
            new BackupStageCommitCommand(
                Guid.NewGuid(),
                BackupStageOutcome.Indeterminate,
                now.AddSeconds(2),
                ErrorCode: "synthetic_result_unknown",
                ErrorMessage: "合成结果不确定"));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, unknown.Code);

        using var anonymous = host.CreateHttpClient();
        var anonymousToken = (await anonymous.GetFromJsonAsync<CsrfTokenResponse>("/api/v1/auth/csrf"))!.Token;
        using var unauthorized = await SendAsync(anonymous, HttpMethod.Post,
            $"/api/v1/backup-tasks/{reconciliationTaskId}/reconcile",
            new BackupTaskCommandRequest { RequestId = Guid.NewGuid() }, anonymousToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var missingCsrf = await client.PostAsJsonAsync(
            $"/api/v1/backup-tasks/{reconciliationTaskId}/reconcile",
            new BackupTaskCommandRequest { RequestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        using var invalidReconciliation = await SendAsync(client, HttpMethod.Post,
            $"/api/v1/backup-tasks/{reconciliationTaskId}/reconcile",
            new BackupTaskCommandRequest { RequestId = Guid.Empty }, token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidReconciliation.StatusCode);

        var reconciliationRequestId = Guid.NewGuid();
        using var requestedReconciliation = await SendAsync(client, HttpMethod.Post,
            $"/api/v1/backup-tasks/{reconciliationTaskId}/reconcile",
            new BackupTaskCommandRequest { RequestId = reconciliationRequestId }, token);
        using var replayedReconciliation = await SendAsync(client, HttpMethod.Post,
            $"/api/v1/backup-tasks/{reconciliationTaskId}/reconcile",
            new BackupTaskCommandRequest { RequestId = reconciliationRequestId }, token);
        Assert.Equal(HttpStatusCode.OK, requestedReconciliation.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayedReconciliation.StatusCode);
        var reconciliationDetail = (await client.GetFromJsonAsync<BackupTaskDetailResponse>(
            $"/api/v1/backup-tasks/{reconciliationTaskId}"))!;
        Assert.Equal("NeedsAttention", reconciliationDetail.Task.Status);
        Assert.Equal(0, reconciliationDetail.Reconciliation.AttemptCount);
        Assert.NotNull(reconciliationDetail.Reconciliation.NextAtUtc);
        Assert.Equal("synthetic_result_unknown", reconciliationDetail.Reconciliation.ReasonCode);
        Assert.True(reconciliationDetail.Reconciliation.CanRequest);
        var reconciliationRaw = await client.GetStringAsync($"/api/v1/backup-tasks/{reconciliationTaskId}");
        Assert.DoesNotContain("leaseToken", reconciliationRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rowVersion", reconciliationRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("errorMessage", reconciliationRaw, StringComparison.OrdinalIgnoreCase);

        var activeLease = await store.AcquireReconciliationLeaseAsync(new AcquireReconciliationLeaseCommand(
            reconciliationTaskId,
            Guid.NewGuid(),
            "web-reconciliation-active",
            DateTimeOffset.UtcNow.AddSeconds(1),
            DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, activeLease.Code);
        using var conflictingReconciliation = await SendAsync(client, HttpMethod.Post,
            $"/api/v1/backup-tasks/{reconciliationTaskId}/reconcile",
            new BackupTaskCommandRequest { RequestId = Guid.NewGuid() }, token);
        Assert.Equal(HttpStatusCode.Conflict, conflictingReconciliation.StatusCode);
        Assert.False((await client.GetFromJsonAsync<BackupTaskDetailResponse>(
            $"/api/v1/backup-tasks/{reconciliationTaskId}"))!.Reconciliation.CanRequest);
        await VerifyConfirmFailedAsync(host, store, serviceScope.ServiceProvider,
            reconciliationTaskId, requestId, activeLease.Value!.Lease, token);
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
    private static async Task VerifyConfirmFailedAsync(AuthTestHost host, IBackupTaskExecutionStore store,
        IServiceProvider services, Guid taskId, Guid cancelledTaskId, LeaseHandle lease, string token)
    {
        var client = host.Client;
        var path = $"/api/v1/backup-tasks/{taskId}/confirm-failed";
        var command = new ConfirmBackupTaskFailedRequest { RequestId = Guid.NewGuid(), EvidenceReviewed = true };
        using var anonymous = host.CreateHttpClient();
        var anonymousToken = (await anonymous.GetFromJsonAsync<CsrfTokenResponse>("/api/v1/auth/csrf"))!.Token;
        using var unauthenticated = await SendAsync(anonymous, HttpMethod.Post, path, command, anonymousToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var noCsrf = await client.PostAsJsonAsync(path, command);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var missingReview = await SendAsync(client, HttpMethod.Post, path, new { command.RequestId }, token);
        Assert.Equal(HttpStatusCode.BadRequest, missingReview.StatusCode);
        using var notReviewed = await SendAsync(client, HttpMethod.Post, path,
            new ConfirmBackupTaskFailedRequest { RequestId = command.RequestId }, token);
        Assert.Equal(HttpStatusCode.BadRequest, notReviewed.StatusCode);
        using var emptyRequest = await SendAsync(client, HttpMethod.Post, path,
            new ConfirmBackupTaskFailedRequest { RequestId = Guid.Empty, EvidenceReviewed = true }, token);
        Assert.Equal(HttpStatusCode.BadRequest, emptyRequest.StatusCode);
        using var active = await SendAsync(client, HttpMethod.Post, path, command, token);
        Assert.Equal(HttpStatusCode.Conflict, active.StatusCode);

        var factory = services.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var admin = await db.AdminUsers.SingleAsync();
            var rejected = await services.GetRequiredService<IBackupManagementService>().ConfirmFailedAsync(
                new AdminSession(admin.Id, admin.Username, "stale-security-stamp"), taskId, command.RequestId, true);
            Assert.Equal(BackupManagementCode.AuthenticationRequired, rejected.Code);
            Assert.False(await db.BackupTaskStateChanges.AnyAsync(x => x.MutationId == command.RequestId));
        }
        var released = await store.CommitReconciliationAsync(lease,
            new ReconciliationCommitCommand(Guid.NewGuid(), BackupReconciliationOutcome.Inconclusive,
                DateTimeOffset.UtcNow.AddSeconds(2), ErrorCode: "reconciliation.file_missing", ErrorMessage: "合成文件缺失"));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, released.Code);
        using var wrongState = await SendAsync(client, HttpMethod.Post,
            $"/api/v1/backup-tasks/{cancelledTaskId}/confirm-failed", command, token);
        Assert.Equal(HttpStatusCode.Conflict, wrongState.StatusCode);
        using var missing = await SendAsync(client, HttpMethod.Post,
            $"/api/v1/backup-tasks/{Guid.NewGuid()}/confirm-failed", command, token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var confirmed = await SendAsync(client, HttpMethod.Post, path, command, token);
        using var replay = await SendAsync(client, HttpMethod.Post, path, command, token);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var reused = await SendAsync(client, HttpMethod.Post,
            $"/api/v1/backup-tasks/{cancelledTaskId}/confirm-failed", command, token);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        using var repeatedConclusion = await SendAsync(client, HttpMethod.Post, path,
            new ConfirmBackupTaskFailedRequest { RequestId = Guid.NewGuid(), EvidenceReviewed = true }, token);
        Assert.Equal(HttpStatusCode.Conflict, repeatedConclusion.StatusCode);
        var detail = (await client.GetFromJsonAsync<BackupTaskDetailResponse>($"/api/v1/backup-tasks/{taskId}"))!;
        Assert.Equal("Failed", detail.Task.Status);
        Assert.Equal("admin_confirmed_failed", detail.Task.ErrorCode);
        Assert.Equal("ConfirmedFailed", Assert.Single(detail.Attempts).InvocationStatus);
        Assert.Empty(detail.Files);
        Assert.True(await host.HasAuditAsync("backup.task.reconciliation.confirm_failed"));
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Equal(0, (await db.BackupTasks.SingleAsync(x => x.Id == taskId)).RetryCount);
            Assert.Equal(1, await db.BackupTaskStateChanges.CountAsync(x => x.MutationId == command.RequestId));
            Assert.Equal(1, await db.TaskEvents.CountAsync(x => x.EventId == command.RequestId));
            Assert.Equal(1, await db.NotificationOutbox.CountAsync(x => x.MutationId == command.RequestId));
            Assert.Equal(1, await db.AuditRecords.CountAsync(x => x.Action == "backup.task.reconciliation.confirm_failed"));
        }
        using var retry = await SendAsync(client, HttpMethod.Post, $"/api/v1/backup-tasks/{taskId}/retry",
            new BackupTaskCommandRequest { RequestId = Guid.NewGuid() }, token);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("Pending", (await client.GetFromJsonAsync<BackupTaskDetailResponse>($"/api/v1/backup-tasks/{taskId}"))!.Task.Status);
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
