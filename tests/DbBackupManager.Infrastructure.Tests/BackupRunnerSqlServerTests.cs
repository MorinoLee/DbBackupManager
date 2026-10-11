using System.Text.Json;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.BackupExecution;
using DbBackupManager.Infrastructure.BackupManagement;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupRunnerSqlServerTests(PlatformDatabaseSqlServerFixture database) : IClassFixture<PlatformDatabaseSqlServerFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ClearBackupTaskDataAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    private readonly FakeAdapters _adapters = new();

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("uncertain")]
    public async Task LegacyActualBackupEntryReleasesOnlyWithDefiniteTerminalResponse(string outcome)
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        _adapters.Unknown = outcome == "uncertain";
        _adapters.ConfirmedFailure = outcome == "failure";
        Assert.True(await Runner(store, factory).RunTaskOnceAsync(seed.TaskId, CancellationToken.None));
        await using var db = database.CreateContext();
        var authorization = await db.BackupInvocationAuthorizations.SingleAsync();
        Assert.Equal(outcome != "uncertain", authorization.TerminalObservedAtUtc is not null);
        Assert.Equal(BackupExecutionOperationState.Applied, (await db.BackupPlanExecutionOperations.SingleAsync()).State);
        var next = Guid.NewGuid();
        Assert.True((await store.CreateTaskAsync(new(next, seed.PolicyId, BackupTaskTriggerType.Manual,
            null, Guid.NewGuid(), DateTimeOffset.UtcNow, seed.Actor.AdminUserId, seed.Actor.SecurityStamp))).IsSucceeded);
        await Runner(store, factory).RunTaskOnceAsync(next, CancellationToken.None);
        Assert.Equal(outcome == "uncertain" ? 1 : 2, _adapters.Backups);
        if (outcome == "uncertain")
            Assert.Equal(BackupTaskStatus.NeedsAttention, (await store.FindTaskAsync(seed.TaskId))!.Status);
        Assert.Equal(outcome == "uncertain" ? 1 : 2, await db.BackupInvocationAuthorizations.CountAsync());
    }

    [Fact]
    public async Task RejectedPreparationCountsAsWorkWithoutCallingAdapters()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        await using (var db = database.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [BackupTaskSnapshots] SET [FileNameRuleVersion] = 'v1' WHERE [TaskId] = {seed.TaskId}");
        }

        var runner = Runner(store, factory);
        Assert.True(await runner.RunOnceAsync(CancellationToken.None));
        Assert.False(await runner.RunOnceAsync(CancellationToken.None));
        Assert.Equal(0, _adapters.Backups);
        Assert.Equal(0, _adapters.Verifications);
        Assert.Equal(0, _adapters.DirectoryPreparations);
        Assert.Equal(BackupTaskStatus.Failed, (await store.FindTaskAsync(seed.TaskId))!.Status);
    }

    [Fact]
    public async Task MonitoringFiltersBeforePagingAndCountsAcrossAllPages()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        var monitoring = new BackupMonitoringService(factory);
        for (var i = 0; i < 22; i++)
        {
            var request = Guid.NewGuid();
            Assert.True((await store.CreateTaskAsync(new(request, seed.PolicyId, BackupTaskTriggerType.Manual, null,
                request, DateTimeOffset.UtcNow, seed.Actor.AdminUserId, seed.Actor.SecurityStamp))).IsSucceeded);
        }
        var first = (await monitoring.TasksAsync(seed.Actor, new(Status: "Pending"))).Value!;
        var second = (await monitoring.TasksAsync(seed.Actor, new(1, Status: "Pending"))).Value!;
        Assert.Equal(23, first.TotalCount); Assert.Equal(20, first.Items.Count); Assert.True(first.HasMore);
        Assert.Equal(3, second.Items.Count); Assert.False(second.HasMore);
        Assert.Empty(first.Items.Select(x => x.Id).Intersect(second.Items.Select(x => x.Id)));
        Assert.Equal(23, (await monitoring.OverviewAsync(seed.Actor)).Value!.PendingTasks);
        Assert.Empty((await monitoring.TasksAsync(seed.Actor, new(Search: "not-a-database"))).Value!.Items);
        Assert.Empty((await monitoring.TasksAsync(seed.Actor, new(Status: "Succeeded"))).Value!.Items);
        Assert.Empty((await monitoring.TasksAsync(seed.Actor, new(UntilUtc: DateTimeOffset.UtcNow.AddDays(-1)))).Value!.Items);
        Assert.Equal(first.Items[0].Id, (await monitoring.TasksAsync(seed.Actor, new(1, OldestFirst: true))).Value!.Items[^1].Id);

        Assert.True(await Runner(store, factory).RunOnceAsync(CancellationToken.None));
        await SeedCompletedMonitoringFilesAsync(seed.PolicyId);
        Assert.Equal(1, _adapters.Backups);
        Assert.Equal(1, _adapters.Verifications);
        var files = await monitoring.FilesAsync(seed.Actor, new(Search: first.Items[0].DatabaseName));
        Assert.Equal(BackupManagementCode.Succeeded, files.Code);
        Assert.Equal(23, files.Value!.TotalCount); Assert.Equal(20, files.Value.Items.Count);
        Assert.Equal(3, (await monitoring.FilesAsync(seed.Actor, new(1))).Value!.Items.Count);
        Assert.Empty((await monitoring.FilesAsync(seed.Actor, new(FromUtc: DateTimeOffset.UtcNow.AddDays(1)))).Value!.Items);
        var overview = (await monitoring.OverviewAsync(seed.Actor)).Value!;
        Assert.Equal(0, overview.PendingTasks); Assert.Equal(5, overview.RecentFiles.Count);
        Assert.Equal(0, overview.RecentFailedTasks);
        var json = JsonSerializer.Serialize(files.Value);
        Assert.DoesNotContain("ProtectedSecret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("LeaseToken", json, StringComparison.Ordinal);
        var revoked = seed.Actor with { SecurityStamp = "revoked" };
        Assert.Equal(BackupManagementCode.AuthenticationRequired, (await monitoring.FilesAsync(revoked, new())).Code);
        Assert.Equal(BackupManagementCode.AuthenticationRequired, (await monitoring.OverviewAsync(revoked)).Code);
        Assert.Equal(BackupManagementCode.Invalid, (await monitoring.TasksAsync(seed.Actor, new(Status: "0"))).Code);
        Assert.Equal(BackupManagementCode.Invalid, (await monitoring.FilesAsync(seed.Actor, new(-1))).Code);
        Assert.Equal(BackupManagementCode.Invalid, (await monitoring.TasksAsync(seed.Actor, new(FromUtc: DateTimeOffset.UtcNow, UntilUtc: DateTimeOffset.UtcNow.AddDays(-1)))).Code);
    }

    private async Task SeedCompletedMonitoringFilesAsync(Guid policyId)
    {
        await using var db = database.CreateContext();
        var templateFile = await db.BackupFiles.AsNoTracking().SingleAsync();
        var templateAttempt = await db.BackupAttempts.AsNoTracking().SingleAsync();
        var pendingTasks = await db.BackupTasks.AsTracking()
            .Where(task => task.PolicyId == policyId && task.Status == BackupTaskStatus.Pending)
            .ToArrayAsync();
        var now = DateTimeOffset.UtcNow;
        foreach (var task in pendingTasks)
        {
            var attemptId = Guid.NewGuid();
            var previousId = templateAttempt.Id.ToString("N");
            var paths = new BackupAttemptPaths(
                templateAttempt.LocalSqlFilePath.Replace(previousId, attemptId.ToString("N"), StringComparison.OrdinalIgnoreCase),
                templateAttempt.WorkerSourceFilePath.Replace(previousId, attemptId.ToString("N"), StringComparison.OrdinalIgnoreCase),
                null, null, null);
            var attempt = new BackupAttempt(attemptId, task.Id, 1, now, paths);
            var leaseToken = Guid.NewGuid();
            task.ClaimExecution(BackupStorageMode.LocalOnly, attemptId, leaseToken, "synthetic-monitoring-seed", now, now.AddMinutes(1));
            attempt.MarkBackupRunning(now);
            attempt.RecordBackupSucceeded(now);
            task.CompleteRunningStage(BackupStorageMode.LocalOnly, leaseToken, now);
            attempt.RecordLocalVerification(templateFile.LengthBytes, now);
            task.CompleteRunningStage(BackupStorageMode.LocalOnly, leaseToken, now);
            db.AddRange(attempt, BackupFile.CreateLocal(Guid.NewGuid(), task.Id, attemptId,
                templateFile.DatabaseId, templateFile.DatabaseServerId!.Value, templateFile.Protocol,
                paths.WorkerSourceFilePath, templateFile.LengthBytes, now, templateFile.RetentionDays));
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task OverviewRecentLocalFilesExcludesRemoteCopy()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        Assert.True(await Runner(store, factory).RunOnceAsync(CancellationToken.None));

        await using (var db = database.CreateContext())
        {
            var local = await db.BackupFiles.SingleAsync();
            var credentialId = await db.CredentialReferences
                .Where(x => x.Kind == CredentialKind.SmbPassword)
                .Select(x => x.Id)
                .FirstAsync();
            var target = new StorageTarget(Guid.NewGuid(), $"synthetic-monitoring-target-{Guid.NewGuid():N}",
                new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", credentialId, null));
            var remote = BackupFile.CreateRemote(Guid.NewGuid(), local.TaskId, local.AttemptId,
                local.DatabaseId, target.Id, FileTransferProtocol.Smb,
                @"\\synthetic-host\synthetic-share\remote.bak", local.LengthBytes,
                local.ValidatedAtUtc.AddSeconds(1), 7);
            db.AddRange(target, remote);
            await db.SaveChangesAsync();
        }

        var monitoring = new BackupMonitoringService(factory);
        var overview = (await monitoring.OverviewAsync(seed.Actor)).Value!;
        Assert.Single(overview.RecentFiles);
        Assert.Equal("Local", overview.RecentFiles[0].Location);
        Assert.Equal(2, (await monitoring.FilesAsync(seed.Actor, new())).Value!.TotalCount);
    }

    [Fact]
    public async Task HeartbeatUsesDatabaseTimeAndOldProcessCannotOverwriteRestart()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var seed = await SeedAsync(new BackupTaskExecutionStore(factory));
        var monitoring = new BackupMonitoringService(factory);
        await using var db = database.CreateContext();
        await db.WorkerHeartbeats.ExecuteDeleteAsync();
        Assert.Equal("Unknown", (await monitoring.OverviewAsync(seed.Actor)).Value!.Worker.Status);
        var store = new WorkerHeartbeatStore(factory);
        var old = Guid.NewGuid(); var current = Guid.NewGuid();
        await store.RegisterAsync(old, CancellationToken.None);
        Assert.Equal("Online", (await monitoring.OverviewAsync(seed.Actor)).Value!.Worker.Status);
        await store.RegisterAsync(current, CancellationToken.None);
        await store.StopAsync(old, CancellationToken.None);
        await store.PulseAsync(old, CancellationToken.None);
        Assert.Equal("Online", (await monitoring.OverviewAsync(seed.Actor)).Value!.Worker.Status);
        await db.Database.ExecuteSqlRawAsync("UPDATE WorkerHeartbeats SET LastSeenAtUtc = DATEADD(SECOND, -91, SYSUTCDATETIME())");
        Assert.Equal("Offline", (await monitoring.OverviewAsync(seed.Actor)).Value!.Worker.Status);
        await store.PulseAsync(current, CancellationToken.None);
        Assert.Equal("Online", (await monitoring.OverviewAsync(seed.Actor)).Value!.Worker.Status);
        await store.StopAsync(current, CancellationToken.None);
        Assert.Equal("Offline", (await monitoring.OverviewAsync(seed.Actor)).Value!.Worker.Status);
        Assert.Equal(current, (await db.WorkerHeartbeats.AsNoTracking().SingleAsync()).ProcessId);
    }

    [Fact]
    public async Task SmbLoadingFailuresAreClassifiedBeforeNetworkLogon()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        await SeedAsync(store);
        var now = DateTimeOffset.UtcNow;
        var item = (await store.ClaimNextAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "synthetic", now, now.AddMinutes(5)))).Value!;
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var probe = new SmbBackupSourceProbe(factory, config);
        var result = await probe.InspectAsync(item.Snapshot, item.Attempt, CancellationToken.None);
        Assert.Equal("smb_key_ring_unavailable", result.ErrorCode);
        Assert.False(result.Succeeded);
    }
    private BackupTaskRunner Runner(
        IBackupTaskExecutionStore store,
        IDbContextFactory<PlatformDbContext> factory,
        TimeProvider? timeProvider = null) =>
        new(store, _adapters, _adapters, _adapters, _adapters, _adapters, _adapters, _adapters, new BackupExecutionGuard(factory), timeProvider ?? TimeProvider.System,
            new(TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(60)));
    private async Task<(Guid TaskId, Guid PolicyId, AdminSession Actor)> SeedAsync(BackupTaskExecutionStore store)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var actor = new AdminSession(Guid.NewGuid(), $"admin-{suffix}", $"stamp-{suffix}");
        var file = new CredentialReference(Guid.NewGuid(), $"smb-{suffix}", CredentialKind.SmbPassword, "synthetic", "synthetic-cipher", "dp-smb-password-v1");
        var sql = new CredentialReference(Guid.NewGuid(), $"sql-{suffix}", CredentialKind.SqlPassword, "synthetic", "synthetic-cipher", "dp-sql-password-v1");
        var server = new DatabaseServer(Guid.NewGuid(), $"server-{suffix}", @"D:\Synthetic", new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", file.Id, null));
        var instance = new DatabaseInstance(Guid.NewGuid(), server.Id, "instance", $"synthetic-{suffix}", sql.Id, true, false, null, 15);
        instance.RecordConnectionSucceeded(DateTimeOffset.UtcNow, "15.0.synthetic", null, null);
        var managed = new ManagedDatabase(Guid.NewGuid(), instance.Id, $"db-{suffix}", false, true, DateTimeOffset.UtcNow);
        managed.SetManaged(true);
        var policy = new BackupPolicy(Guid.NewGuid(), $"policy-{suffix}", managed.Id,
            new(BackupStorageMode.LocalOnly, null, BackupScheduleType.Daily, TimeOnly.MinValue, BackupWeekdays.None, "UTC", 7, null, true, false, true, 120, 60, 60), true, true);
        await using var db = database.CreateContext();
        db.AddRange(file, sql, server, instance, managed, policy, new AdminUser(actor.AdminUserId, actor.Username, actor.Username.ToUpperInvariant(), "synthetic-hash", actor.SecurityStamp));
        await db.SaveChangesAsync();
        var taskId = Guid.NewGuid();
        var result = await store.CreateTaskAsync(new(taskId, policy.Id, BackupTaskTriggerType.Manual, null, taskId, DateTimeOffset.UtcNow, actor.AdminUserId, actor.SecurityStamp));
        Assert.True(result.IsSucceeded);
        return (taskId, policy.Id, actor);
    }

    [Fact]
    public async Task ExactRunnerClaimsOnlyAllowlistedTaskAndAttempt()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var foreign = await SeedAsync(store);
        var allowlisted = await SeedAsync(store);
        var attemptId = Guid.NewGuid();

        Assert.True(await Runner(store, factory).RunTaskOnceAsync(
            allowlisted.TaskId,
            attemptId,
            CancellationToken.None));

        Assert.Equal(BackupTaskStatus.Pending, (await store.FindTaskAsync(foreign.TaskId))!.Status);
        Assert.Equal(BackupTaskStatus.Succeeded, (await store.FindTaskAsync(allowlisted.TaskId))!.Status);
        Assert.Equal(1, _adapters.Backups);
        await using var db = database.CreateContext();
        Assert.Empty(await db.BackupAttempts.Where(attempt => attempt.TaskId == foreign.TaskId).ToArrayAsync());
        Assert.Equal(
            attemptId,
            (await db.BackupAttempts.SingleAsync(attempt => attempt.TaskId == allowlisted.TaskId)).Id);
    }

    [Fact]
    public async Task ExactRecoveryInspectsOnlyAllowlistedTask()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var foreign = await SeedAsync(store);
        var allowlisted = await SeedAsync(store);
        _adapters.Unknown = true;
        Assert.True(await Runner(store, factory).RunTaskOnceAsync(
            foreign.TaskId,
            Guid.NewGuid(),
            CancellationToken.None));
        _adapters.Backups = 0;
        Assert.True(await Runner(store, factory).RunTaskOnceAsync(
            allowlisted.TaskId,
            Guid.NewGuid(),
            CancellationToken.None));
        _adapters.Unknown = false;
        _adapters.IdentityStatus = TargetSqlBackupIdentityStatus.CompletedMatching;
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddMinutes(2));

        Assert.True(await Recovery(store, clock).ReconcileTaskOnceAsync(
            allowlisted.TaskId,
            CancellationToken.None));

        Assert.Equal(BackupTaskStatus.NeedsAttention, (await store.FindTaskAsync(foreign.TaskId))!.Status);
        Assert.Equal(BackupTaskStatus.Pending, (await store.FindTaskAsync(allowlisted.TaskId))!.Status);
        Assert.Equal(1, _adapters.IdentityInspections);
    }

    [Fact]
    public async Task TwoWorkersExecuteOnceAndSuccessContainsAtomicFileHistoryAndEvents()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        _adapters.DelayMilliseconds = 180;
        await Task.WhenAll(Runner(store, factory).RunOnceAsync(CancellationToken.None), Runner(store, factory).RunOnceAsync(CancellationToken.None));
        Assert.Equal(1, _adapters.Backups); Assert.Equal(1, _adapters.Verifications);
        Assert.Equal(BackupTaskStatus.Succeeded, (await store.FindTaskAsync(seed.TaskId))!.Status);
        await using var db = database.CreateContext();
        var file = await db.BackupFiles.SingleAsync(x => x.TaskId == seed.TaskId);
        var attempt = await db.BackupAttempts.SingleAsync(x => x.Id == file.AttemptId);
        Assert.Equal(4096, file.LengthBytes);
        Assert.Equal(attempt.WorkerSourceFilePath.ToUpperInvariant(), file.Path);
        Assert.Equal(BackupFileStatus.Available, file.Status);
        Assert.Single(await db.BackupFileStateChanges.Where(x => x.FileId == file.Id).ToArrayAsync());
        // PlatformDbContext 按 MutationId 为每条状态历史生成且只生成一条 TaskEvent，因此这里断言精确平价。
        // 该平价只在 P6.3 清理未运行时成立：清理只删除派生的已发布事件，不删除权威状态历史。
        Assert.Equal(await db.BackupTaskStateChanges.CountAsync(x => x.TaskId == seed.TaskId), await db.TaskEvents.CountAsync(x => x.TaskId == seed.TaskId));
        var manager = new BackupManagementService(factory, store);
        var detail = await manager.DetailAsync(seed.Actor, seed.TaskId);
        Assert.Equal(BackupManagementCode.Succeeded, detail.Code);
        var json = JsonSerializer.Serialize(detail.Value);
        Assert.DoesNotContain("LeaseToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RowVersion", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(attempt.LocalSqlFilePath, detail.Value!.VerifiedSqlPath);
        var projected = Assert.Single(detail.Value.Files);
        Assert.Equal(file.Id, projected.FileId);
        Assert.Equal("Local", projected.Location);
        Assert.Equal(file.Protocol.ToString(), projected.Protocol);
        Assert.Equal("Available", projected.Status);
        Assert.Equal(file.Path, projected.Path);
        Assert.Equal(attempt.LocalSqlFilePath, projected.SqlPath);
        Assert.Null(projected.TargetDisplayName);
        Assert.Null(projected.DeletedAtUtc);
        var events = new TaskEventStore(factory);
        var pending = await events.ReadPendingAsync(100, CancellationToken.None);
        Assert.NotEmpty(pending);
        await events.AcknowledgeAsync(pending.Select(x => x.EventId).ToArray(), CancellationToken.None);
        Assert.Empty(await events.ReadPendingAsync(100, CancellationToken.None));
        // 较早发生的事件可能稍晚才提交；逐事件确认不能把它当作已投递。
        var late = Guid.NewGuid();
        db.TaskEvents.Add(new(late, seed.TaskId, DateTimeOffset.UtcNow.AddHours(-1)));
        await db.SaveChangesAsync();
        await events.AcknowledgeAsync(pending.Select(x => x.EventId).ToArray(), CancellationToken.None);
        Assert.Equal(late, Assert.Single(await events.ReadPendingAsync(100, CancellationToken.None)).EventId);
    }
    [Fact]
    public async Task UnknownBackupNeverAutomaticallyRepeatsOrRegistersFile()
    {
        using var provider = database.CreateServiceProvider(); var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory); var seed = await SeedAsync(store);
        _adapters.Unknown = true;
        await Runner(store, factory).RunOnceAsync(CancellationToken.None);
        await Runner(store, factory).RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, _adapters.Backups); Assert.Equal(0, _adapters.Verifications);
        Assert.Equal(BackupTaskStatus.NeedsAttention, (await store.FindTaskAsync(seed.TaskId))!.Status);
        Assert.False((await store.RetryFailedAsync(new(seed.TaskId, Guid.NewGuid(), DateTimeOffset.UtcNow))).IsSucceeded);
        await using var db = database.CreateContext(); Assert.Empty(await db.BackupFiles.ToArrayAsync());
    }

    [Fact]
    public async Task ManagementExposesAndRequestsReconciliationWithoutExecutingBackup()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        _adapters.Unknown = true;
        Assert.True(await Runner(store, factory).RunOnceAsync(CancellationToken.None));
        var manager = new BackupManagementService(factory, store);

        var before = await manager.DetailAsync(seed.Actor, seed.TaskId);
        var invalid = await manager.RequestReconciliationAsync(seed.Actor, seed.TaskId, Guid.Empty);
        var revoked = await manager.RequestReconciliationAsync(
            seed.Actor with { SecurityStamp = "revoked" },
            seed.TaskId,
            Guid.NewGuid());
        var requestId = Guid.NewGuid();
        var requested = await manager.RequestReconciliationAsync(seed.Actor, seed.TaskId, requestId);
        var replay = await manager.RequestReconciliationAsync(seed.Actor, seed.TaskId, requestId);
        var after = await manager.DetailAsync(seed.Actor, seed.TaskId);

        Assert.Equal(BackupManagementCode.Succeeded, before.Code);
        Assert.Equal(0, before.Value!.Reconciliation.AttemptCount);
        Assert.NotNull(before.Value.Reconciliation.NextAtUtc);
        Assert.NotNull(before.Value.Reconciliation.ReasonCode);
        Assert.True(before.Value.Reconciliation.CanRequest);
        Assert.Equal(BackupManagementCode.Invalid, invalid.Code);
        Assert.Equal(BackupManagementCode.AuthenticationRequired, revoked.Code);
        Assert.Equal(BackupManagementCode.Succeeded, requested.Code);
        Assert.Equal(BackupManagementCode.Succeeded, replay.Code);
        Assert.Equal(seed.TaskId, requested.Value);
        Assert.Equal(0, after.Value!.Reconciliation.AttemptCount);
        Assert.True(after.Value.Reconciliation.CanRequest);
        Assert.Equal(before.Value.Reconciliation.ReasonCode, after.Value.Reconciliation.ReasonCode);
        Assert.Equal(1, _adapters.Backups);
        Assert.Equal(0, _adapters.Verifications);

        var lease = await store.AcquireReconciliationLeaseAsync(new AcquireReconciliationLeaseCommand(
            seed.TaskId,
            Guid.NewGuid(),
            "worker-management-projection",
            DateTimeOffset.UtcNow.AddSeconds(1),
            DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, lease.Code);
        var during = await manager.DetailAsync(seed.Actor, seed.TaskId);
        var conflict = await manager.RequestReconciliationAsync(seed.Actor, seed.TaskId, Guid.NewGuid());
        Assert.False(during.Value!.Reconciliation.CanRequest);
        Assert.Equal(BackupManagementCode.Conflict, conflict.Code);

        var json = JsonSerializer.Serialize(after.Value);
        Assert.DoesNotContain("LeaseToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RowVersion", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ErrorMessage", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadOnlyRecoveryAdvancesUnknownBackupWithoutRepeatingBackup()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        _adapters.Unknown = true;
        await Runner(store, factory).RunOnceAsync(CancellationToken.None);
        Assert.Equal(BackupTaskStatus.NeedsAttention, (await store.FindTaskAsync(seed.TaskId))!.Status);

        _adapters.Unknown = false;
        _adapters.IdentityStatus = TargetSqlBackupIdentityStatus.CompletedMatching;
        var recoveryClock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        var recovery = Recovery(store, recoveryClock);
        Assert.True(await recovery.ReconcileOnceAsync());

        var reconciled = (await store.FindTaskAsync(seed.TaskId))!;
        Assert.Equal(BackupTaskStatus.Pending, reconciled.Status);
        Assert.Equal(BackupTaskStage.VerifyLocal, reconciled.CurrentStage);
        Assert.Equal(1, _adapters.Backups);
        Assert.Equal(1, _adapters.IdentityInspections);

        await Runner(store, factory).RunOnceAsync(CancellationToken.None);
        Assert.Equal(BackupTaskStatus.Succeeded, (await store.FindTaskAsync(seed.TaskId))!.Status);
        Assert.Equal(1, _adapters.Backups);
        await using var db = database.CreateContext();
        Assert.Single(await db.BackupFiles.Where(file => file.TaskId == seed.TaskId).ToArrayAsync());
    }

    [Fact]
    public async Task MissingSqlEvidenceReleasesLeaseAndRemainsEligibleForRecheck()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        _adapters.Unknown = true;
        await Runner(store, factory).RunOnceAsync(CancellationToken.None);

        _adapters.Unknown = false;
        _adapters.IdentityStatus = TargetSqlBackupIdentityStatus.NotFound;
        var recoveryClock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        var recovery = Recovery(store, recoveryClock);
        Assert.True(await recovery.ReconcileOnceAsync());
        var inconclusive = (await store.FindTaskAsync(seed.TaskId))!;
        Assert.Equal(BackupTaskStatus.NeedsAttention, inconclusive.Status);
        Assert.Equal(BackupTaskStage.Backup, inconclusive.CurrentStage);
        Assert.Equal("reconciliation.backup_record_not_found", inconclusive.ErrorCode);
        Assert.Equal(1, inconclusive.ReconciliationAttemptCount);

        _adapters.IdentityStatus = TargetSqlBackupIdentityStatus.CompletedMatching;
        Assert.False(await recovery.ReconcileOnceAsync());
        recoveryClock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await recovery.ReconcileOnceAsync());
        Assert.Equal(BackupTaskStatus.Pending, (await store.FindTaskAsync(seed.TaskId))!.Status);
        Assert.Equal(2, _adapters.IdentityInspections);
        Assert.Equal(1, _adapters.Backups);
    }

    [Fact]
    public async Task ReadOnlyRecoveryCompletesUnknownVerifyAndRegistersFileOnce()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        _adapters.VerificationUnknown = true;
        await Runner(store, factory).RunOnceAsync(CancellationToken.None);
        var unknown = (await store.FindTaskAsync(seed.TaskId))!;
        Assert.Equal(BackupTaskStatus.NeedsAttention, unknown.Status);
        Assert.Equal(BackupTaskStage.VerifyLocal, unknown.CurrentStage);

        _adapters.VerificationUnknown = false;
        Assert.True(await Recovery(store).ReconcileOnceAsync());

        Assert.Equal(BackupTaskStatus.Succeeded, (await store.FindTaskAsync(seed.TaskId))!.Status);
        Assert.Equal(1, _adapters.Backups);
        Assert.Equal(2, _adapters.Verifications);
        await using var db = database.CreateContext();
        Assert.Single(await db.BackupFiles.Where(file => file.TaskId == seed.TaskId).ToArrayAsync());
    }

    [Fact]
    public async Task RecoveryRenewsLeaseWhileReadOnlyProbeIsRunning()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        _adapters.Unknown = true;
        await Runner(store, factory).RunOnceAsync(CancellationToken.None);
        // 探针由测试显式放行；租约给出裕量，并以观察到 LeaseExpiresAtUtc 前进来确认续租，不依赖真实毫秒边界。
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new BackupReconciliationOptions(
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(1),
            100);
        var recovery = new BackupTaskRecovery(
            store,
            new GatedSuccessfulEvidenceProbe(gate.Task),
            TimeProvider.System,
            options);

        var reconciling = recovery.ReconcileOnceAsync();
        var acquired = await WaitForLeaseExpiryAsync(seed.TaskId, current => current is not null, TimeSpan.FromSeconds(30));
        await WaitForLeaseExpiryAsync(seed.TaskId, current => current > acquired, TimeSpan.FromSeconds(30));
        gate.SetResult();
        Assert.True(await reconciling);

        var reconciled = (await store.FindTaskAsync(seed.TaskId))!;
        Assert.Equal(BackupTaskStatus.Pending, reconciled.Status);
        Assert.Equal(BackupTaskStage.VerifyLocal, reconciled.CurrentStage);
        Assert.Equal(1, _adapters.Backups);
    }

    [Fact]
    public async Task RecoveryRereadsAfterHeartbeatConflictWithoutRestartingProbe()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        _adapters.Unknown = true;
        await Runner(store, factory).RunOnceAsync(CancellationToken.None);
        var faultingStore = new FaultInjectingBackupTaskExecutionStore(
            store, seed.TaskId, BackupTaskStage.Backup, StoreFaultPoint.RenewalConflict);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new GatedSuccessfulEvidenceProbe(gate.Task);
        var recovery = new BackupTaskRecovery(
            faultingStore, probe, TimeProvider.System,
            new(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(1), 100));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var reconciling = recovery.ReconcileTaskOnceAsync(seed.TaskId, deadline.Token);
        await faultingStore.RenewalFault.Task.WaitAsync(deadline.Token);
        var acquired = await WaitForLeaseExpiryAsync(seed.TaskId, current => current is not null, TimeSpan.FromSeconds(10));
        await WaitForLeaseExpiryAsync(seed.TaskId, current => current > acquired, TimeSpan.FromSeconds(10));
        gate.SetResult();

        Assert.True(await reconciling);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(1, faultingStore.ReconciliationCommits);
        Assert.Equal(1, _adapters.Backups);
        Assert.Equal(BackupTaskStage.VerifyLocal, (await store.FindTaskAsync(seed.TaskId))!.CurrentStage);
    }

    [Fact]
    public async Task RecoveryCancelsProbeAndDiscardsEvidenceAfterHeartbeatLeaseLoss()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        _adapters.Unknown = true;
        await Runner(store, factory).RunOnceAsync(CancellationToken.None);
        var faultingStore = new FaultInjectingBackupTaskExecutionStore(
            store, seed.TaskId, BackupTaskStage.Backup, StoreFaultPoint.RenewalLeaseLost);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new GatedSuccessfulEvidenceProbe(gate.Task);
        var recovery = new BackupTaskRecovery(
            faultingStore, probe, TimeProvider.System,
            new(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(1), 100));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Assert.True(await recovery.ReconcileTaskOnceAsync(seed.TaskId, deadline.Token));
        await probe.CancellationObserved.Task.WaitAsync(deadline.Token);
        Assert.False(gate.Task.IsCompleted);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(0, faultingStore.ReconciliationCommits);
        Assert.Equal(BackupTaskStatus.NeedsAttention, (await store.FindTaskAsync(seed.TaskId))!.Status);
        Assert.Equal(1, _adapters.Backups);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryDisposesPendingHeartbeatTimerOnCompletionOrHostStop(bool stopHost)
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        _adapters.Unknown = true;
        await Runner(store, factory).RunOnceAsync(CancellationToken.None);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new GatedSuccessfulEvidenceProbe(gate.Task);
        var clock = new TrackingTimeProvider();
        var recovery = new BackupTaskRecovery(
            store, probe, clock,
            new(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3), TimeSpan.FromMilliseconds(1), 100));
        using var stop = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var reconciling = recovery.ReconcileTaskOnceAsync(seed.TaskId, stop.Token);
        await clock.TimerCreated.Task.WaitAsync(deadline.Token);
        Assert.Equal(1, clock.ActiveTimers);
        if (stopHost)
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconciling.WaitAsync(deadline.Token));
            await probe.CancellationObserved.Task.WaitAsync(deadline.Token);
            Assert.Equal(BackupTaskStatus.NeedsAttention, (await store.FindTaskAsync(seed.TaskId))!.Status);
        }
        else
        {
            gate.SetResult();
            Assert.True(await reconciling.WaitAsync(deadline.Token));
            Assert.Equal(BackupTaskStage.VerifyLocal, (await store.FindTaskAsync(seed.TaskId))!.CurrentStage);
        }

        Assert.Equal(0, clock.ActiveTimers);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(1, _adapters.Backups);
    }

    private async Task<DateTimeOffset?> WaitForLeaseExpiryAsync(
        Guid taskId,
        Func<DateTimeOffset?, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        DateTimeOffset? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var db = database.CreateContext();
            last = (await db.BackupTasks.AsNoTracking().SingleAsync(task => task.Id == taskId)).LeaseExpiresAtUtc;
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"未在超时内满足核对租约条件；最近值={last:O}。");
    }
    [Fact]
    public async Task LeaseLossDuringBackupPreventsFurtherStagesAndResultCommit()
    {
        using var provider = database.CreateServiceProvider(); var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory); var seed = await SeedAsync(store);
        var expired = false;
        var mutation = Guid.NewGuid();
        _adapters.BeforeComplete = async token =>
        {
            // 与心跳续租竞争时 ExpireExecutionLeaseAsync 会返回并发冲突；在时间预算内重试，直到真正注入租约过期。
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var result = await store.ExpireExecutionLeaseAsync(new(seed.TaskId, mutation, DateTimeOffset.UtcNow.AddMinutes(5), "synthetic_expiry", "合成租约过期。"), CancellationToken.None);
                if (result.IsSucceeded) { expired = true; return; }
                Assert.Equal(BackupTaskStoreResultCode.ConcurrencyConflict, result.Code);
                await Task.Delay(10, token);
            }
        };
        await Runner(store, factory).RunOnceAsync(CancellationToken.None);
        Assert.True(expired, "必须成功注入租约过期，不能把与心跳的版本竞争当作过期成功。");
        Assert.Equal(1, _adapters.Backups); Assert.Equal(0, _adapters.Verifications);
        Assert.Equal(BackupTaskStatus.NeedsAttention, (await store.FindTaskAsync(seed.TaskId))!.Status);
        await using var db = database.CreateContext(); Assert.Empty(await db.BackupFiles.ToArrayAsync());
    }
    [Fact]
    public async Task HostStopLeavesRunningTaskForExpiryInsteadOfClaimingFailureOrReplaying()
    {
        using var provider = database.CreateServiceProvider(); var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory); var seed = await SeedAsync(store);
        using var stop = new CancellationTokenSource();
        _adapters.BeforeComplete = token => { stop.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(store, factory).RunOnceAsync(stop.Token));
        Assert.Equal(BackupTaskStatus.Running, (await store.FindTaskAsync(seed.TaskId))!.Status);
        await store.ExpireExecutionLeaseAsync(new(seed.TaskId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5), "synthetic_expiry", "合成停止恢复。"));
        Assert.Equal(BackupTaskStatus.NeedsAttention, (await store.FindTaskAsync(seed.TaskId))!.Status);
        Assert.Equal(1, _adapters.Backups);
    }
    [Theory]
    [InlineData(true, false, "backup_file_already_exists")]
    [InlineData(false, true, "verified_file_unavailable")]
    public async Task ExistingOrEmptySourceNeverProducesSuccess(bool exists, bool empty, string error)
    {
        using var provider = database.CreateServiceProvider(); var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory); var seed = await SeedAsync(store);
        _adapters.ExistsInitially = exists; _adapters.Empty = empty;
        await Runner(store, factory).RunOnceAsync(CancellationToken.None);
        var task = await store.FindTaskAsync(seed.TaskId);
        Assert.Equal(BackupTaskStatus.Failed, task!.Status); Assert.Equal(error, task.ErrorCode);
        Assert.Equal(exists ? 0 : 1, _adapters.Backups);
        await using var db = database.CreateContext(); Assert.Empty(await db.BackupFiles.ToArrayAsync());
    }
    [Fact]
    public async Task DirectoryPreparationFailureDoesNotMarkOrInvokeBackup()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        _adapters.DirectoryPreparationFails = true;

        await Runner(store, factory).RunOnceAsync(CancellationToken.None);

        var task = await store.FindTaskAsync(seed.TaskId);
        Assert.Equal(BackupTaskStatus.Failed, task!.Status);
        Assert.Equal(nameof(BackupFileStorageFailureCode.ConnectionInterrupted), task.ErrorCode);
        Assert.Equal(1, _adapters.DirectoryPreparations);
        Assert.Equal(0, _adapters.Backups);
        await using var db = database.CreateContext();
        var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == seed.TaskId);
        Assert.Equal(BackupInvocationStatus.Prepared, attempt.BackupInvocationStatus);
        Assert.Null(attempt.BackupStartedAtUtc);
        Assert.Empty(await db.BackupFiles.Where(x => x.TaskId == seed.TaskId).ToArrayAsync());
    }
    [Fact]
    public async Task CancellationDuringDirectoryPreparationCancelsBeforeInvocation()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        var mutationId = Guid.NewGuid();
        _adapters.BeforeDirectoryPreparation = async token =>
        {
            for (var retry = 0; retry < 10; retry++)
            {
                var requested = await store.RequestCancellationAsync(
                    new(
                        seed.TaskId,
                        mutationId,
                        DateTimeOffset.UtcNow,
                        seed.Actor.AdminUserId,
                        seed.Actor.SecurityStamp),
                    CancellationToken.None);
                if (requested.IsSucceeded)
                {
                    break;
                }

                Assert.Equal(BackupTaskStoreResultCode.ConcurrencyConflict, requested.Code);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Runner(store, factory).RunOnceAsync(deadline.Token);

        var task = await store.FindTaskAsync(seed.TaskId);
        Assert.Equal(BackupTaskStatus.Cancelled, task!.Status);
        Assert.Equal(0, _adapters.Backups);
        await using var db = database.CreateContext();
        var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == seed.TaskId);
        Assert.Equal(BackupInvocationStatus.Prepared, attempt.BackupInvocationStatus);
        Assert.Null(attempt.BackupStartedAtUtc);
    }
    [Fact]
    public async Task CancellationAfterInvocationReconcilesAndVerifiesCompletedBackupWithoutRetry()
    {
        using var provider = database.CreateServiceProvider(); var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory); var seed = await SeedAsync(store);
        var request = Guid.NewGuid();
        _adapters.BeforeComplete = async token =>
        {
            for (var i = 0; i < 10; i++)
            {
                var result = await store.RequestCancellationAsync(new(seed.TaskId, request, DateTimeOffset.UtcNow, seed.Actor.AdminUserId, seed.Actor.SecurityStamp), CancellationToken.None);
                if (result.IsSucceeded) break;
                Assert.Equal(BackupTaskStoreResultCode.ConcurrencyConflict, result.Code);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Runner(store, factory).RunOnceAsync(deadline.Token);
        var task = (await store.FindTaskAsync(seed.TaskId))!;
        Assert.NotNull(task.CancellationRequestedAtUtc);
        Assert.Equal(BackupTaskStatus.NeedsAttention, task.Status);
        Assert.Equal(0, _adapters.Verifications);

        var recoveryClock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(await Recovery(store, recoveryClock).ReconcileOnceAsync());
        var pendingVerification = (await store.FindTaskAsync(seed.TaskId))!;
        Assert.Equal(BackupTaskStatus.Pending, pendingVerification.Status);
        Assert.Equal(BackupTaskStage.VerifyLocal, pendingVerification.CurrentStage);
        Assert.NotNull(pendingVerification.CancellationRequestedAtUtc);

        await Runner(store, factory).RunOnceAsync(deadline.Token);

        var completed = (await store.FindTaskAsync(seed.TaskId))!;
        Assert.Equal(BackupTaskStatus.Succeeded, completed.Status);
        Assert.NotNull(completed.CancellationRequestedAtUtc);
        Assert.Equal(1, _adapters.Backups);
        Assert.Equal(1, _adapters.Verifications);
        await using var db = database.CreateContext();
        Assert.Single(await db.BackupFiles.Where(file => file.TaskId == seed.TaskId).ToArrayAsync());
    }

    [Fact]
    public async Task CancellationBeforeClaimNeverInvokesBackupOrCreatesAttempt()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);

        var cancelled = await store.RequestCancellationAsync(new BackupTaskMutationCommand(
            seed.TaskId,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            seed.Actor.AdminUserId,
            seed.Actor.SecurityStamp));

        Assert.Equal(BackupTaskStoreResultCode.Succeeded, cancelled.Code);
        Assert.Equal(BackupTaskStatus.Cancelled, cancelled.Value!.Status);
        Assert.False(await Runner(store, factory).RunOnceAsync(CancellationToken.None));
        Assert.Equal(0, _adapters.Backups);
        Assert.Equal(0, _adapters.Verifications);
        await using var db = database.CreateContext();
        Assert.Empty(await db.BackupAttempts.Where(attempt => attempt.TaskId == seed.TaskId).ToArrayAsync());
        Assert.Empty(await db.BackupFiles.Where(file => file.TaskId == seed.TaskId).ToArrayAsync());
        await AssertAtomicTaskSideEffectsAsync(db, seed.TaskId);
    }

    [Fact]
    public async Task ExternalSuccessBeforePlatformCommitRecoversWithoutRepeatingBackup()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var faultingStore = new FaultInjectingBackupTaskExecutionStore(
            store,
            seed.TaskId,
            BackupTaskStage.Backup,
            StoreFaultPoint.BeforeStageCommit);

        await Assert.ThrowsAsync<InjectedStoreFaultException>(() =>
            Runner(faultingStore, factory, clock).RunOnceAsync(CancellationToken.None));

        Assert.Equal(1, _adapters.Backups);
        Assert.Equal(BackupTaskStatus.Running, (await store.FindTaskAsync(seed.TaskId))!.Status);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(await Runner(store, factory, clock).RunOnceAsync(CancellationToken.None));
        Assert.Equal(BackupTaskStatus.NeedsAttention, (await store.FindTaskAsync(seed.TaskId))!.Status);

        _adapters.IdentityStatus = TargetSqlBackupIdentityStatus.CompletedMatching;
        Assert.True(await Recovery(store, clock).ReconcileOnceAsync());
        Assert.True(await Runner(store, factory, clock).RunOnceAsync(CancellationToken.None));

        Assert.Equal(BackupTaskStatus.Succeeded, (await store.FindTaskAsync(seed.TaskId))!.Status);
        Assert.Equal(1, _adapters.Backups);
        Assert.Equal(1, _adapters.Verifications);
        await using var db = database.CreateContext();
        Assert.Single(await db.BackupAttempts.Where(attempt => attempt.TaskId == seed.TaskId).ToArrayAsync());
        Assert.Single(await db.BackupFiles.Where(file => file.TaskId == seed.TaskId).ToArrayAsync());
        await AssertAtomicTaskSideEffectsAsync(db, seed.TaskId);
    }

    [Fact]
    public async Task SuccessfulVerifyCommitWithLostResponseRemainsExactlyOnce()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        var faultingStore = new FaultInjectingBackupTaskExecutionStore(
            store,
            seed.TaskId,
            BackupTaskStage.VerifyLocal,
            StoreFaultPoint.AfterStageCommit);

        await Assert.ThrowsAsync<InjectedStoreFaultException>(() =>
            Runner(faultingStore, factory).RunOnceAsync(CancellationToken.None));

        Assert.Equal(BackupTaskStatus.Succeeded, (await store.FindTaskAsync(seed.TaskId))!.Status);
        Assert.Equal(1, _adapters.Backups);
        Assert.Equal(1, _adapters.Verifications);
        await using var db = database.CreateContext();
        var historyCount = await db.BackupTaskStateChanges.CountAsync(change => change.TaskId == seed.TaskId);
        var eventCount = await db.TaskEvents.CountAsync(taskEvent => taskEvent.TaskId == seed.TaskId);
        var auditCount = await db.AuditRecords.CountAsync(audit => audit.TargetId == seed.TaskId.ToString("N"));
        Assert.Single(await db.BackupFiles.Where(file => file.TaskId == seed.TaskId).ToArrayAsync());

        Assert.False(await Runner(store, factory).RunOnceAsync(CancellationToken.None));

        Assert.Equal(historyCount, await db.BackupTaskStateChanges.CountAsync(change => change.TaskId == seed.TaskId));
        Assert.Equal(eventCount, await db.TaskEvents.CountAsync(taskEvent => taskEvent.TaskId == seed.TaskId));
        Assert.Equal(auditCount, await db.AuditRecords.CountAsync(audit => audit.TargetId == seed.TaskId.ToString("N")));
        Assert.Single(await db.BackupFiles.Where(file => file.TaskId == seed.TaskId).ToArrayAsync());
        await AssertAtomicTaskSideEffectsAsync(db, seed.TaskId);
    }

    [Fact]
    public async Task TwoRecoveryLoopsCompeteButOnlyOneInspectsAndCommits()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store);
        _adapters.Unknown = true;
        await Runner(store, factory).RunOnceAsync(CancellationToken.None);
        _adapters.Unknown = false;
        _adapters.IdentityStatus = TargetSqlBackupIdentityStatus.CompletedMatching;
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));

        var results = await Task.WhenAll(
            Recovery(store, clock).ReconcileOnceAsync(),
            Recovery(store, clock).ReconcileOnceAsync());

        Assert.Equal(1, results.Count(result => result));
        Assert.Equal(1, _adapters.IdentityInspections);
        Assert.Equal(1, _adapters.Backups);
        Assert.Equal(BackupTaskStage.VerifyLocal, (await store.FindTaskAsync(seed.TaskId))!.CurrentStage);
        Assert.True(await Runner(store, factory, clock).RunOnceAsync(CancellationToken.None));
        Assert.Equal(BackupTaskStatus.Succeeded, (await store.FindTaskAsync(seed.TaskId))!.Status);
        await using var db = database.CreateContext();
        Assert.Single(await db.BackupFiles.Where(file => file.TaskId == seed.TaskId).ToArrayAsync());
        await AssertAtomicTaskSideEffectsAsync(db, seed.TaskId);
    }

    [Fact]
    public async Task RevokedActorCannotMutateOrReadAndManualPolicyCannotBeScheduled()
    {
        using var provider = database.CreateServiceProvider(); var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory); var seed = await SeedAsync(store);
        var manager = new BackupManagementService(factory, store);
        var revoked = seed.Actor with { SecurityStamp = "revoked" };
        Assert.Equal(BackupManagementCode.AuthenticationRequired, (await manager.ListAsync(revoked)).Code);
        Assert.Equal(BackupManagementCode.AuthenticationRequired, (await manager.CancelAsync(revoked, seed.TaskId, Guid.NewGuid())).Code);
        Assert.Equal(BackupManagementCode.AuthenticationRequired, (await manager.RequestReconciliationAsync(revoked, seed.TaskId, Guid.NewGuid())).Code);
        Assert.Equal(BackupManagementCode.AuthenticationRequired, (await manager.StartAsync(revoked, seed.PolicyId, Guid.NewGuid())).Code);
        var request = Guid.NewGuid();
        Assert.Equal(BackupTaskStoreResultCode.ConfigurationUnavailable, (await store.CreateTaskAsync(new(request, seed.PolicyId,
            BackupTaskTriggerType.Scheduled, DateTimeOffset.UtcNow, request, DateTimeOffset.UtcNow))).Code);
        var replay = await manager.StartAsync(seed.Actor, seed.PolicyId, seed.TaskId);
        Assert.Equal(BackupManagementCode.Succeeded, replay.Code);
        await using var db = database.CreateContext(); Assert.Single(await db.BackupTasks.ToArrayAsync());
    }
    [Fact]
    public async Task EventInsertFailureRollsBackFileAndSuccessState()
    {
        using var provider = database.CreateServiceProvider(); var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory); var seed = await SeedAsync(store);
        var now = DateTimeOffset.UtcNow;
        var claimed = (await store.ClaimNextAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "synthetic-worker", now, now.AddMinutes(5)))).Value!;
        var marked = (await store.MarkBackupInvocationStartedAsync(claimed.Lease, claimed.Attempt.RowVersion, DateTimeOffset.UtcNow)).Value!;
        var backupDone = (await store.CommitStageAsync(marked, new(Guid.NewGuid(), BackupStageOutcome.Succeeded, DateTimeOffset.UtcNow))).Value!;
        var mutation = Guid.NewGuid();
        await using (var db = database.CreateContext())
        { db.TaskEvents.Add(new(mutation, seed.TaskId, DateTimeOffset.UtcNow)); await db.SaveChangesAsync(); }
        var result = await store.CommitStageAsync(backupDone.Lease!, new(mutation, BackupStageOutcome.Succeeded, DateTimeOffset.UtcNow, SourceLengthBytes: 4096));
        Assert.False(result.IsSucceeded);
        await using var verify = database.CreateContext();
        Assert.Empty(await verify.BackupFiles.ToArrayAsync());
        Assert.Equal(BackupTaskStatus.Running, (await store.FindTaskAsync(seed.TaskId))!.Status);
        Assert.False(await verify.BackupTaskStateChanges.AnyAsync(x => x.MutationId == mutation));
    }

    private BackupTaskRecovery Recovery(
        IBackupTaskExecutionStore store,
        TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var options = new BackupReconciliationOptions(
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromMilliseconds(1),
            100);
        var probe = new SqlSmbBackupReconciliationEvidenceProbe(
            _adapters,
            _adapters,
            _adapters,
            _adapters,
            clock,
            options);
        return new BackupTaskRecovery(store, probe, clock, options);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private static async Task AssertAtomicTaskSideEffectsAsync(PlatformDbContext context, Guid taskId)
    {
        var stateChanges = await context.BackupTaskStateChanges
            .CountAsync(change => change.TaskId == taskId);
        Assert.Equal(
            stateChanges,
            await context.TaskEvents.CountAsync(taskEvent => taskEvent.TaskId == taskId));
        Assert.Equal(
            stateChanges,
            await context.AuditRecords.CountAsync(audit => audit.TargetId == taskId.ToString("N")));
    }

    private enum StoreFaultPoint
    {
        BeforeStageCommit,
        AfterStageCommit,
        RenewalConflict,
        RenewalLeaseLost,
    }

    private sealed class InjectedStoreFaultException : Exception;

    private sealed class FaultInjectingBackupTaskExecutionStore(
        IBackupTaskExecutionStore inner,
        Guid taskId,
        BackupTaskStage stage,
        StoreFaultPoint faultPoint) : IBackupTaskExecutionStore
    {
        private int _remainingFaults = 1;
        public TaskCompletionSource RenewalFault { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReconciliationCommits { get; private set; }

        public Task<BackupTaskStoreResult<BackupTaskStateModel>> CreateTaskAsync(
            CreateBackupTaskCommand command,
            CancellationToken cancellationToken = default) =>
            inner.CreateTaskAsync(command, cancellationToken);

        public Task<BackupTaskStoreResult<BackupExecutionWorkItem>> ClaimNextAsync(
            ClaimNextBackupTaskCommand command,
            CancellationToken cancellationToken = default) =>
            inner.ClaimNextAsync(command, cancellationToken);

        public Task<BackupTaskStoreResult<BackupExecutionWorkItem>> ClaimTaskAsync(
            Guid requestedTaskId,
            ClaimNextBackupTaskCommand command,
            CancellationToken cancellationToken = default) =>
            inner.ClaimTaskAsync(requestedTaskId, command, cancellationToken);

        public Task<BackupTaskStoreResult<LeaseHandle>> MarkBackupInvocationStartedAsync(
            LeaseHandle lease,
            byte[] attemptRowVersion,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken = default) =>
            inner.MarkBackupInvocationStartedAsync(lease, attemptRowVersion, startedAtUtc, cancellationToken);

        public Task<BackupTaskStoreResult<LeaseHandle>> RenewLeaseAsync(
            LeaseHandle lease,
            DateTimeOffset utcNow,
            DateTimeOffset expiresAtUtc,
            CancellationToken cancellationToken = default)
        {
            if (faultPoint is StoreFaultPoint.RenewalConflict or StoreFaultPoint.RenewalLeaseLost
                && lease.TaskId == taskId
                && lease.Stage == stage
                && Interlocked.Exchange(ref _remainingFaults, 0) == 1)
            {
                RenewalFault.SetResult();
                return Task.FromResult(new BackupTaskStoreResult<LeaseHandle>(
                    faultPoint == StoreFaultPoint.RenewalConflict
                        ? BackupTaskStoreResultCode.ConcurrencyConflict
                        : BackupTaskStoreResultCode.LeaseLost));
            }

            return inner.RenewLeaseAsync(lease, utcNow, expiresAtUtc, cancellationToken);
        }

        public Task<BackupTaskStoreResult<BackupExecutionWorkItem>> RefreshLeaseWorkItemAsync(
            Guid requestedTaskId,
            Guid leaseToken,
            BackupLeasePurpose purpose,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            inner.RefreshLeaseWorkItemAsync(requestedTaskId, leaseToken, purpose, utcNow, cancellationToken);

        public async Task<BackupTaskStoreResult<BackupTaskTransitionModel>> CommitStageAsync(
            LeaseHandle lease,
            BackupStageCommitCommand command,
            CancellationToken cancellationToken = default)
        {
            ThrowIfMatched(lease, StoreFaultPoint.BeforeStageCommit);
            var result = await inner.CommitStageAsync(lease, command, cancellationToken);
            ThrowIfMatched(lease, StoreFaultPoint.AfterStageCommit);
            return result;
        }

        public Task<BackupTaskStoreResult<BackupTaskStateModel>> RequestCancellationAsync(
            BackupTaskMutationCommand command,
            CancellationToken cancellationToken = default) =>
            inner.RequestCancellationAsync(command, cancellationToken);

        public Task<BackupTaskStoreResult<BackupTaskStateModel>> RetryFailedAsync(
            BackupTaskMutationCommand command,
            CancellationToken cancellationToken = default) =>
            inner.RetryFailedAsync(command, cancellationToken);

        public Task<BackupTaskStoreResult<BackupTaskStateModel>> RequestReconciliationAsync(
            BackupTaskMutationCommand command,
            CancellationToken cancellationToken = default) =>
            inner.RequestReconciliationAsync(command, cancellationToken);

        public Task<IReadOnlyList<Guid>> FindExpiredExecutionTaskIdsAsync(
            DateTimeOffset utcNow,
            int maximumCount,
            CancellationToken cancellationToken = default) =>
            inner.FindExpiredExecutionTaskIdsAsync(utcNow, maximumCount, cancellationToken);

        public Task<BackupTaskStoreResult<BackupTaskStateModel>> ExpireExecutionLeaseAsync(
            ExpireExecutionLeaseCommand command,
            CancellationToken cancellationToken = default) =>
            inner.ExpireExecutionLeaseAsync(command, cancellationToken);

        public Task<IReadOnlyList<Guid>> FindReconciliationCandidateTaskIdsAsync(
            DateTimeOffset utcNow,
            int maximumCount,
            CancellationToken cancellationToken = default) =>
            inner.FindReconciliationCandidateTaskIdsAsync(utcNow, maximumCount, cancellationToken);

        public Task<BackupTaskStoreResult<BackupExecutionWorkItem>> AcquireReconciliationLeaseAsync(
            AcquireReconciliationLeaseCommand command,
            CancellationToken cancellationToken = default) =>
            inner.AcquireReconciliationLeaseAsync(command, cancellationToken);

        public Task<BackupTaskStoreResult<BackupTaskStateModel>> CommitReconciliationAsync(
            LeaseHandle lease,
            ReconciliationCommitCommand command,
            CancellationToken cancellationToken = default)
        {
            ReconciliationCommits++;
            return inner.CommitReconciliationAsync(lease, command, cancellationToken);
        }

        public Task<BackupTaskStoreResult<BackupTaskStateModel>> ConfirmNeedsAttentionAsync(
            ConfirmNeedsAttentionCommand command,
            CancellationToken cancellationToken = default) =>
            inner.ConfirmNeedsAttentionAsync(command, cancellationToken);

        public Task<BackupTaskStateModel?> FindTaskAsync(
            Guid requestedTaskId,
            CancellationToken cancellationToken = default) =>
            inner.FindTaskAsync(requestedTaskId, cancellationToken);

        private void ThrowIfMatched(LeaseHandle lease, StoreFaultPoint candidate)
        {
            if (candidate == faultPoint
                && lease.TaskId == taskId
                && lease.Stage == stage
                && Interlocked.Exchange(ref _remainingFaults, 0) == 1)
            {
                throw new InjectedStoreFaultException();
            }
        }
    }

    private sealed class FakeAdapters : ITargetSqlBackupExecutor, ITargetSqlReadOnlyProbe,
        ITargetSqlBackupEvidenceProbe, IBackupSourceProbe,
        IBackupFileStorageProbe, IBackupDirectoryPreparer, IBackupFileTransferExecutor,
        IBackupFileDeletionExecutor
    {
        private readonly HashSet<string> _invokedPaths = new(StringComparer.Ordinal);
        public int Backups, Verifications, IdentityInspections, DirectoryPreparations;
        public bool Unknown, ConfirmedFailure, ExistsInitially, Empty, VerificationUnknown,
            DirectoryPreparationFails;
        public TargetSqlBackupIdentityStatus IdentityStatus = TargetSqlBackupIdentityStatus.CompletedMatching;
        public int DelayMilliseconds;
        public Func<CancellationToken, Task>? BeforeComplete;
        public Func<CancellationToken, Task>? BeforeDirectoryPreparation;
        public async Task<TargetSqlResult<TargetSqlFullBackupCompletion>> ExecuteFullBackupAsync(TargetSqlConnectionInput c, TargetSqlFullBackupRequest r, CancellationToken token = default)
        {
            Interlocked.Increment(ref Backups);
            _invokedPaths.Add(r.LocalSqlFilePath);
            if (BeforeComplete is not null) await BeforeComplete(token);
            if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds, token);
            return ConfirmedFailure ? TargetSqlResult.ConfirmedFailure<TargetSqlFullBackupCompletion>(TargetSqlFailureCode.CommandRejected, TargetSqlFailurePhase.BackupExecution)
                : Unknown ? TargetSqlResult.Indeterminate<TargetSqlFullBackupCompletion>(TargetSqlFailureCode.ConnectionInterrupted, TargetSqlFailurePhase.BackupExecution)
                : TargetSqlResult.Succeeded(new TargetSqlFullBackupCompletion(true, true, false));
        }
        public Task<TargetSqlResult<TargetSqlBackupVerification>> VerifyBackupAsync(TargetSqlConnectionInput c, TargetSqlBackupVerificationRequest r, CancellationToken token = default)
        {
            Verifications++;
            return Task.FromResult(VerificationUnknown
                ? TargetSqlResult.Indeterminate<TargetSqlBackupVerification>(
                    TargetSqlFailureCode.ConnectionInterrupted,
                    TargetSqlFailurePhase.BackupVerification)
                : TargetSqlResult.Succeeded(new TargetSqlBackupVerification(true)));
        }
        public Task<BackupFileProbeResult> InspectAsync(BackupTaskSnapshotModel s, BackupAttemptModel a, CancellationToken token)
        { SmbBackupSourceProbe.ValidatePath(s, a); return Task.FromResult(new BackupFileProbeResult(true, ExistsInitially || _invokedPaths.Contains(a.LocalSqlFilePath), _invokedPaths.Contains(a.LocalSqlFilePath) ? Empty ? 0 : 4096 : null)); }
        public Task<TargetSqlResult<TargetSqlServerInfo>> ProbeServerAsync(TargetSqlConnectionInput c, CancellationToken token = default) => throw new NotSupportedException();
        public Task<TargetSqlResult<TargetSqlDatabaseCatalog>> DiscoverDatabasesAsync(TargetSqlConnectionInput c, CancellationToken token = default) => throw new NotSupportedException();
        public Task<TargetSqlResult<TargetSqlBackupIdentity>> InspectBackupIdentityAsync(
            TargetSqlConnectionInput connection,
            TargetSqlBackupIdentityRequest request,
            CancellationToken cancellationToken = default)
        {
            IdentityInspections++;
            return Task.FromResult(TargetSqlResult.Succeeded(new TargetSqlBackupIdentity(IdentityStatus)));
        }

        public Task<BackupFileStorageResult<BackupFileMetadata>> InspectAsync(
            BackupFileEndpointInput endpoint,
            string path,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("仅本地任务不应访问远程文件端口。");

        public async Task<BackupFileStorageResult<BackupFileMutationReceipt>> PrepareParentAsync(
            BackupDirectoryPreparationRequest request,
            CancellationToken cancellationToken = default)
        {
            DirectoryPreparations++;
            if (BeforeDirectoryPreparation is not null)
            {
                await BeforeDirectoryPreparation(cancellationToken);
            }

            return DirectoryPreparationFails
                ? BackupFileStorageResult.ConfirmedFailure<BackupFileMutationReceipt>(
                    BackupFileStorageFailureCode.ConnectionInterrupted,
                    BackupFileStorageFailurePhase.DirectoryPrepare)
                : BackupFileStorageResult.Succeeded(BackupFileMutationReceipt.Instance);
        }

        public Task<BackupFileStorageResult<BackupFileTransferReceipt>> TransferAsync(
            BackupFileTransferRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("仅本地任务不应访问远程文件端口。");

        public Task<BackupFileStorageResult<BackupFileMutationReceipt>> RenameAsync(
            BackupFileRenameRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("仅本地任务不应访问远程文件端口。");

        public Task<BackupFileStorageResult<BackupFileMutationReceipt>> DeleteAsync(
            BackupFileDeleteRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("仅本地任务不应访问远程文件端口。");
    }

    private sealed class GatedSuccessfulEvidenceProbe(Task release) : IBackupReconciliationEvidenceProbe
    {
        public int Calls { get; private set; }
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BackupReconciliationEvidence> InspectAsync(
            BackupExecutionWorkItem workItem,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            try
            {
                await release.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
            return new BackupReconciliationEvidence(
                workItem.Task.TaskId,
                workItem.Attempt.Id,
                workItem.Lease.Stage,
                DateTimeOffset.UtcNow,
                BackupReconciliationConclusion.Succeeded,
                BackupArtifactObservation.PresentStable,
                TargetBackupObservation.CompletedMatching,
                BackupVerificationObservation.NotAttempted,
                "reconciliation.backup_completed");
        }
    }

    private sealed class TrackingTimeProvider : TimeProvider
    {
        private int _activeTimers;
        public int ActiveTimers => Volatile.Read(ref _activeTimers);
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _activeTimers);
            var timer = new TrackingTimer(System.CreateTimer(callback, state, dueTime, period), this);
            TimerCreated.TrySetResult();
            return timer;
        }

        private sealed class TrackingTimer(ITimer inner, TrackingTimeProvider owner) : ITimer
        {
            private int _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    inner.Dispose();
                    Interlocked.Decrement(ref owner._activeTimers);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
