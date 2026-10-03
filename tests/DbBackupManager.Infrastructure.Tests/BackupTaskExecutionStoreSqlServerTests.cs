using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupTaskExecutionStoreSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>, IAsyncLifetime
{
    public Task InitializeAsync()
    {
        return database.ClearBackupTaskDataAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task LegacyExceptionSurvivesSnapshotPersistenceAndInstanceRevocation()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly, legacy: true);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var now = DateTimeOffset.UtcNow;
        var command = CreateManualTaskCommand(graph.PolicyId, now);
        var created = await store.CreateTaskAsync(command);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, created.Code);
        await using (var db = database.CreateContext())
        {
            var snapshot = await db.BackupTaskSnapshots.SingleAsync(x => x.TaskId == command.TaskId);
            Assert.True(snapshot.AllowLegacyTls);
            Assert.Equal("legacy fixture", snapshot.LegacyTlsReason);
            var instance = await db.DatabaseInstances.AsTracking().SingleAsync(x => x.Id == snapshot.InstanceId);
            instance.UpdateConnection(instance.ConnectionAddress, instance.SqlCredentialReferenceId, true,
                instance.TrustServerCertificate, instance.CertificateTrustReason, instance.ConnectionTimeoutSeconds);
            await db.SaveChangesAsync();
        }
        var work = await store.ClaimNextAsync(CreateClaimCommand("synthetic-worker", now.AddSeconds(1)));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, work.Code);
        Assert.True(work.Value!.Snapshot.SqlTarget.AllowLegacyTls);
        Assert.Equal("legacy fixture", work.Value.Snapshot.SqlTarget.LegacyTlsReason);
    }

    [Fact]
    public async Task ScheduledCreationAndMutationReplayReturnExistingTaskWithoutDuplicateSnapshot()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        var command = new CreateBackupTaskCommand(
            Guid.NewGuid(),
            graph.PolicyId,
            BackupTaskTriggerType.Scheduled,
            now,
            Guid.NewGuid(),
            now);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();

        var created = await store.CreateTaskAsync(command);
        var replayed = await store.CreateTaskAsync(command);
        var duplicateSlot = await store.CreateTaskAsync(command with
        {
            TaskId = Guid.NewGuid(),
            MutationId = Guid.NewGuid(),
        });

        Assert.Equal(BackupTaskStoreResultCode.Succeeded, created.Code);
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, replayed.Code);
        Assert.Equal(BackupTaskStoreResultCode.AlreadyExists, duplicateSlot.Code);
        Assert.Equal(command.TaskId, duplicateSlot.Value!.TaskId);

        await using var context = database.CreateContext();
        Assert.Equal(1, await context.BackupTasks.CountAsync(x => x.PolicyId == graph.PolicyId));
        Assert.Equal(1, await context.BackupTaskSnapshots.CountAsync(x => x.TaskId == command.TaskId));
        Assert.Equal(
            1,
            await context.BackupTaskStateChanges.CountAsync(x => x.TaskId == command.TaskId));
    }

    [Fact]
    public async Task DisabledConfigurationFailsClosedWithoutCreatingTask()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        await using (var disable = database.CreateContext())
        {
            var policy = await disable.BackupPolicies.AsTracking().SingleAsync(x => x.Id == graph.PolicyId);
            policy.SetEnabled(false);
            await disable.SaveChangesAsync();
        }

        var command = CreateManualTaskCommand(graph.PolicyId, DateTimeOffset.UtcNow);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();

        var result = await store.CreateTaskAsync(command);

        Assert.Equal(BackupTaskStoreResultCode.ConfigurationUnavailable, result.Code);
        await using var context = database.CreateContext();
        Assert.False(await context.BackupTasks.AnyAsync(x => x.Id == command.TaskId));
    }

    [Theory]
    [InlineData(160)]
    [InlineData(2_000)]
    public async Task InvalidCompletePathIsRejectedBeforeTaskCreation(int rootLength)
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        await using (var context = database.CreateContext())
        {
            var policy = await context.BackupPolicies.SingleAsync(x => x.Id == graph.PolicyId);
            var managed = await context.ManagedDatabases.SingleAsync(x => x.Id == policy.DatabaseId);
            var instance = await context.DatabaseInstances.SingleAsync(x => x.Id == managed.InstanceId);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [DatabaseServers] SET [LocalBackupRootPath] = {@"D:\" + new string('R', rootLength)} WHERE [Id] = {instance.ServerId}");
        }

        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var command = CreateManualTaskCommand(graph.PolicyId, DateTimeOffset.UtcNow);
        var result = await store.CreateTaskAsync(command);

        Assert.Equal(BackupTaskStoreResultCode.ConfigurationUnavailable, result.Code);
        await using var db = database.CreateContext();
        Assert.False(await db.BackupTasks.AnyAsync(x => x.Id == command.TaskId));
        Assert.False(await db.BackupTaskSnapshots.AnyAsync(x => x.TaskId == command.TaskId));
        Assert.False(await db.TaskEvents.AnyAsync(x => x.EventId == command.MutationId));
    }

    [Theory]
    [InlineData(160, "v2")]
    [InlineData(2_000, "v2")]
    [InlineData(0, "v1")]
    public async Task InvalidPersistedTaskFailsAtomicallyWithoutBlockingNextTask(int rootLength, string version)
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var now = DateTimeOffset.UtcNow;
        var invalidCommand = CreateManualTaskCommand(graph.PolicyId, now);
        var validCommand = CreateManualTaskCommand(graph.PolicyId, now.AddSeconds(1));
        Assert.True((await store.CreateTaskAsync(invalidCommand)).IsSucceeded);
        Assert.True((await store.CreateTaskAsync(validCommand)).IsSucceeded);
        await using (var context = database.CreateContext())
        {
            // 只在专用测试库模拟旧版本遗留快照，生产快照保持不可变。
            var root = rootLength == 0 ? @"D:\Backup" : @"D:\" + new string('R', rootLength);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [BackupTaskSnapshots] SET [LocalSqlBackupRootPath] = {root}, [FileNameRuleVersion] = {version} WHERE [TaskId] = {invalidCommand.TaskId}");
        }

        var claim = CreateClaimCommand("synthetic-worker", now.AddSeconds(2));
        Assert.Equal(BackupTaskStoreResultCode.ConfigurationUnavailable, (await store.ClaimNextAsync(claim)).Code);
        Assert.Equal(BackupTaskStoreResultCode.ConfigurationUnavailable, (await store.ClaimNextAsync(claim)).Code);
        var next = await store.ClaimNextAsync(CreateClaimCommand("synthetic-worker", now.AddSeconds(3)));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, next.Code);
        Assert.Equal(validCommand.TaskId, next.Value!.Task.TaskId);

        await using var db = database.CreateContext();
        var invalid = await db.BackupTasks.SingleAsync(x => x.Id == invalidCommand.TaskId);
        Assert.Equal(BackupTaskStatus.Failed, invalid.Status);
        Assert.Equal("backup_path_invalid", invalid.ErrorCode);
        Assert.Null(invalid.CurrentBackupAttemptId);
        Assert.Null(invalid.StartedAtUtc);
        Assert.Null(invalid.LeaseToken);
        Assert.Equal(claim.AcquiredAtUtc, invalid.CompletedAtUtc);
        Assert.False(await db.BackupAttempts.AnyAsync(x => x.TaskId == invalid.Id));
        var change = await db.BackupTaskStateChanges.SingleAsync(x => x.MutationId == claim.MutationId);
        Assert.Equal(BackupTaskStatus.Pending, change.FromStatus);
        Assert.Equal(BackupTaskStatus.Failed, change.ToStatus);
        Assert.Equal("execution.preparation_failed", change.ReasonCode);
        Assert.Equal(1, await db.TaskEvents.CountAsync(x => x.EventId == claim.MutationId));
        Assert.Equal(1, await db.NotificationOutbox.CountAsync(x => x.MutationId == claim.MutationId));
        Assert.Equal(1, await db.AuditRecords.CountAsync(x => x.Action == "backup.task.prepare" && x.TargetId == invalid.Id.ToString("N")));
    }

    [Fact]
    public async Task ConcurrentClaimCreatesOneAttemptAndOneLease()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        var task = await CreateTaskAsync(graph.PolicyId, now);
        using var provider = database.CreateServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var firstStore = firstScope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var secondStore = secondScope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();

        var results = await Task.WhenAll(
            firstStore.ClaimNextAsync(CreateClaimCommand("worker-a", now)),
            secondStore.ClaimNextAsync(CreateClaimCommand("worker-b", now)));

        Assert.Single(results, result => result.Code == BackupTaskStoreResultCode.Succeeded);
        Assert.Single(
            results,
            result => result.Code is BackupTaskStoreResultCode.NotFound
                or BackupTaskStoreResultCode.ConcurrencyConflict);
        Assert.Equal(task.TaskId, results.Single(result => result.Value is not null).Value!.Task.TaskId);

        await using var context = database.CreateContext();
        Assert.Equal(1, await context.BackupAttempts.CountAsync(x => x.TaskId == task.TaskId));
        Assert.Equal(
            1,
            await context.BackupTaskStateChanges.CountAsync(
                x => x.TaskId == task.TaskId && x.ReasonCode == "execution.claimed"));
    }

    [Fact]
    public async Task NewTaskAndAttemptUseCurrentHierarchicalPathLayout()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalAndRemote);
        var now = DateTimeOffset.UtcNow;
        var task = await CreateTaskAsync(graph.PolicyId, now);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();

        var claimed = await store.ClaimTaskAsync(
            task.TaskId,
            CreateClaimCommand("worker-layout", now.AddSeconds(1)));

        Assert.Equal(BackupTaskStoreResultCode.Succeeded, claimed.Code);
        var work = claimed.Value!;
        Assert.Equal(BackupTaskPathFactory.CurrentVersion, work.Snapshot.FileNameRuleVersion);
        Assert.Contains(@"\full\", work.Attempt.LocalSqlFilePath, StringComparison.Ordinal);
        Assert.Contains(@"\full\", work.Attempt.WorkerSourceFilePath, StringComparison.Ordinal);
        Assert.Contains("/full/", work.Attempt.RemoteFinalFilePath, StringComparison.Ordinal);
        Assert.Equal(
            work.Attempt.LocalSqlFilePath.Split('\\').Last(),
            work.Attempt.WorkerSourceFilePath.Split('\\').Last());
        Assert.Equal(
            work.Attempt.LocalSqlFilePath.Split('\\').Last(),
            work.Attempt.RemoteFinalFilePath!.Split('/').Last());
    }

    [Fact]
    public async Task ClaimMutationReplayReturnsSameAttemptWithoutDuplicateHistory()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        var task = await CreateTaskAsync(graph.PolicyId, now);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var command = CreateClaimCommand("worker-replay", now);

        var claimed = await store.ClaimNextAsync(command);
        var replayed = await store.ClaimNextAsync(command);

        Assert.Equal(BackupTaskStoreResultCode.Succeeded, claimed.Code);
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, replayed.Code);
        Assert.Equal(claimed.Value!.Attempt.Id, replayed.Value!.Attempt.Id);
        Assert.Equal(claimed.Value.Lease.LeaseToken, replayed.Value.Lease.LeaseToken);

        await using var context = database.CreateContext();
        Assert.Equal(1, await context.BackupAttempts.CountAsync(x => x.TaskId == task.TaskId));
        Assert.Equal(
            1,
            await context.BackupTaskStateChanges.CountAsync(
                x => x.TaskId == task.TaskId && x.MutationId == command.MutationId));
    }

    [Fact]
    public async Task BackupAndVerifySuccessAdvanceWithFreshLeaseAndMutationReplay()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        await CreateTaskAsync(graph.PolicyId, now);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var claimed = await store.ClaimNextAsync(CreateClaimCommand("worker-stage", now));
        var workItem = Assert.IsType<BackupExecutionWorkItem>(claimed.Value);

        var marked = await store.MarkBackupInvocationStartedAsync(
            workItem.Lease,
            workItem.Attempt.RowVersion,
            now.AddSeconds(1));
        var backupMutation = Guid.NewGuid();
        var backupResult = await store.CommitStageAsync(
            marked.Value!,
            new BackupStageCommitCommand(
                backupMutation,
                BackupStageOutcome.Succeeded,
                now.AddSeconds(2)));
        var replay = await store.CommitStageAsync(
            marked.Value!,
            new BackupStageCommitCommand(
                backupMutation,
                BackupStageOutcome.Succeeded,
                now.AddSeconds(2)));

        Assert.Equal(BackupTaskStoreResultCode.Succeeded, backupResult.Code);
        Assert.Equal(BackupTaskStage.VerifyLocal, backupResult.Value!.Task.CurrentStage);
        Assert.NotNull(backupResult.Value.Lease);
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, replay.Code);

        var completed = await store.CommitStageAsync(
            backupResult.Value.Lease!,
            new BackupStageCommitCommand(
                Guid.NewGuid(),
                BackupStageOutcome.Succeeded,
                now.AddSeconds(3),
                SourceLengthBytes: 4096));

        Assert.Equal(BackupTaskStatus.Succeeded, completed.Value!.Task.Status);
        Assert.Null(completed.Value.Lease);
        await using var context = database.CreateContext();
        var attempt = await context.BackupAttempts.SingleAsync(x => x.Id == workItem.Attempt.Id);
        Assert.Equal(BackupInvocationStatus.Succeeded, attempt.BackupInvocationStatus);
        Assert.Equal(4096, attempt.SourceLengthBytes);
        Assert.Equal(
            1,
            await context.BackupTaskStateChanges.CountAsync(x => x.MutationId == backupMutation));
    }

    [Fact]
    public async Task InvalidStageEvidenceLeavesTaskAttemptAndHistoryUnchanged()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        var task = await CreateTaskAsync(graph.PolicyId, now);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var claimed = await store.ClaimNextAsync(CreateClaimCommand("worker-atomic", now));
        var marked = await store.MarkBackupInvocationStartedAsync(
            claimed.Value!.Lease,
            claimed.Value.Attempt.RowVersion,
            now.AddSeconds(1));
        var backup = await store.CommitStageAsync(
            marked.Value!,
            new BackupStageCommitCommand(
                Guid.NewGuid(),
                BackupStageOutcome.Succeeded,
                now.AddSeconds(2)));
        var invalidMutationId = Guid.NewGuid();

        var rejected = await store.CommitStageAsync(
            backup.Value!.Lease!,
            new BackupStageCommitCommand(
                invalidMutationId,
                BackupStageOutcome.Succeeded,
                now.AddSeconds(3)));

        Assert.Equal(BackupTaskStoreResultCode.StateMismatch, rejected.Code);
        await using var context = database.CreateContext();
        var persistedTask = await context.BackupTasks.SingleAsync(x => x.Id == task.TaskId);
        var attempt = await context.BackupAttempts.SingleAsync(x => x.TaskId == task.TaskId);
        Assert.Equal(BackupTaskStatus.Running, persistedTask.Status);
        Assert.Equal(BackupTaskStage.VerifyLocal, persistedTask.CurrentStage);
        Assert.Null(attempt.SourceLengthBytes);
        Assert.False(await context.BackupTaskStateChanges.AnyAsync(x => x.MutationId == invalidMutationId));
    }

    [Fact]
    public async Task ConfirmedBackupFailureRetryCreatesNewAttempt()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        var task = await CreateTaskAsync(graph.PolicyId, now);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var firstClaim = await store.ClaimNextAsync(CreateClaimCommand("worker-retry", now));
        var firstWork = firstClaim.Value!;
        var marked = await store.MarkBackupInvocationStartedAsync(
            firstWork.Lease,
            firstWork.Attempt.RowVersion,
            now.AddSeconds(1));

        var failed = await store.CommitStageAsync(
            marked.Value!,
            new BackupStageCommitCommand(
                Guid.NewGuid(),
                BackupStageOutcome.ConfirmedFailed,
                now.AddSeconds(2),
                ErrorCode: "backup_failed",
                ErrorMessage: "合成的明确失败"));
        var retried = await store.RetryFailedAsync(new BackupTaskMutationCommand(
            task.TaskId,
            Guid.NewGuid(),
            now.AddSeconds(3)));
        var secondClaim = await store.ClaimNextAsync(CreateClaimCommand("worker-retry", now.AddSeconds(4)));

        Assert.Equal(BackupTaskStatus.Failed, failed.Value!.Task.Status);
        Assert.Equal(BackupTaskStatus.Pending, retried.Value!.Status);
        Assert.NotEqual(firstWork.Attempt.Id, secondClaim.Value!.Attempt.Id);
        Assert.Equal(2, secondClaim.Value.Attempt.AttemptNumber);
    }

    [Fact]
    public async Task LaterStageRetryReusesAttemptAndReturnsToSafeTransferBoundary()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalAndRemote);
        var now = DateTimeOffset.UtcNow;
        var task = await CreateTaskAsync(graph.PolicyId, now);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var claim = await store.ClaimNextAsync(CreateClaimCommand("worker-later-retry", now));
        var attemptId = claim.Value!.Attempt.Id;
        var marked = await store.MarkBackupInvocationStartedAsync(
            claim.Value.Lease,
            claim.Value.Attempt.RowVersion,
            now.AddSeconds(1));
        var backup = await store.CommitStageAsync(
            marked.Value!,
            new BackupStageCommitCommand(
                Guid.NewGuid(),
                BackupStageOutcome.Succeeded,
                now.AddSeconds(2)));
        var verified = await store.CommitStageAsync(
            backup.Value!.Lease!,
            new BackupStageCommitCommand(
                Guid.NewGuid(),
                BackupStageOutcome.Succeeded,
                now.AddSeconds(3),
                SourceLengthBytes: 4096));
        var transferred = await store.CommitStageAsync(
            verified.Value!.Lease!,
            new BackupStageCommitCommand(
                Guid.NewGuid(),
                BackupStageOutcome.Succeeded,
                now.AddSeconds(4)));
        var failed = await store.CommitStageAsync(
            transferred.Value!.Lease!,
            new BackupStageCommitCommand(
                Guid.NewGuid(),
                BackupStageOutcome.ConfirmedFailed,
                now.AddSeconds(5),
                ErrorCode: "remote_validation_failed",
                ErrorMessage: "合成的远程校验失败"));
        var retry = await store.RetryFailedAsync(new BackupTaskMutationCommand(
            task.TaskId,
            Guid.NewGuid(),
            now.AddSeconds(6)));
        var secondClaim = await store.ClaimNextAsync(
            CreateClaimCommand("worker-later-retry", now.AddSeconds(7)));

        Assert.Equal(BackupTaskStage.ValidateCopy, failed.Value!.Task.CurrentStage);
        Assert.Equal(BackupTaskStage.Transfer, retry.Value!.CurrentStage);
        Assert.Equal(attemptId, secondClaim.Value!.Attempt.Id);
        Assert.Equal(1, secondClaim.Value.Attempt.AttemptNumber);
    }

    [Fact]
    public async Task CancellationInvalidatesOldHandleAndCanFinishAtSafeBoundaryAfterRefresh()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        var task = await CreateTaskAsync(graph.PolicyId, now);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var claimed = await store.ClaimNextAsync(CreateClaimCommand("worker-cancel", now));
        var originalLease = claimed.Value!.Lease;

        var requested = await store.RequestCancellationAsync(new BackupTaskMutationCommand(
            task.TaskId,
            Guid.NewGuid(),
            now.AddSeconds(1)));
        var staleCommit = await store.CommitStageAsync(
            originalLease,
            new BackupStageCommitCommand(
                Guid.NewGuid(),
                BackupStageOutcome.Cancelled,
                now.AddSeconds(2)));
        var refreshed = await store.RefreshLeaseWorkItemAsync(
            originalLease.TaskId,
            originalLease.LeaseToken,
            BackupLeasePurpose.Execution,
            now.AddSeconds(2));
        var cancelled = await store.CommitStageAsync(
            refreshed.Value!.Lease,
            new BackupStageCommitCommand(
                Guid.NewGuid(),
                BackupStageOutcome.Cancelled,
                now.AddSeconds(3)));

        Assert.NotNull(requested.Value!.CancellationRequestedAtUtc);
        Assert.Equal(BackupTaskStoreResultCode.ConcurrencyConflict, staleCommit.Code);
        Assert.Equal(BackupTaskStatus.Cancelled, cancelled.Value!.Task.Status);
    }

    [Fact]
    public async Task LeaseRenewalIsIdempotentAndRejectsOldWrongOrExpiredCapability()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        await CreateTaskAsync(graph.PolicyId, now);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var claimed = await store.ClaimNextAsync(CreateClaimCommand("worker-lease", now));
        var lease = claimed.Value!.Lease;
        var newExpiry = now.AddMinutes(10);

        var renewed = await store.RenewLeaseAsync(lease, now.AddSeconds(1), newExpiry);
        var replay = await store.RenewLeaseAsync(lease, now.AddSeconds(1), newExpiry);
        var oldHandle = await store.RenewLeaseAsync(lease, now.AddSeconds(2), now.AddMinutes(11));
        var wrongToken = new LeaseHandle(
            renewed.Value!.TaskId,
            Guid.NewGuid(),
            renewed.Value.Purpose,
            renewed.Value.Stage,
            renewed.Value.BackupAttemptId,
            renewed.Value.ExpiresAtUtc,
            renewed.Value.RowVersion);
        var wrong = await store.RenewLeaseAsync(wrongToken, now.AddSeconds(2), now.AddMinutes(11));
        var expired = await store.RenewLeaseAsync(
            renewed.Value,
            newExpiry,
            newExpiry.AddMinutes(1));

        Assert.Equal(BackupTaskStoreResultCode.Succeeded, renewed.Code);
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, replay.Code);
        Assert.Equal(BackupTaskStoreResultCode.ConcurrencyConflict, oldHandle.Code);
        Assert.Equal(BackupTaskStoreResultCode.LeaseLost, wrong.Code);
        Assert.Equal(BackupTaskStoreResultCode.LeaseLost, expired.Code);
    }

    [Fact]
    public async Task ExpiredExecutionEntersNeedsAttentionAndSafeRetryUsesNewBackupAttempt()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        var task = await CreateTaskAsync(graph.PolicyId, now);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var claimCommand = CreateClaimCommand("worker-expire", now) with
        {
            ExpiresAtUtc = now.AddSeconds(1),
        };
        var claimed = await store.ClaimNextAsync(claimCommand);
        var firstAttemptId = claimed.Value!.Attempt.Id;
        var expiredIds = await store.FindExpiredExecutionTaskIdsAsync(now.AddSeconds(2), 10);
        var expireCommand = new ExpireExecutionLeaseCommand(
            task.TaskId,
            Guid.NewGuid(),
            now.AddSeconds(2),
            "lease_expired",
            "执行租约已过期，需要核对");

        var expired = await store.ExpireExecutionLeaseAsync(expireCommand);
        var replay = await store.ExpireExecutionLeaseAsync(expireCommand);
        var reconciledLease = await store.AcquireReconciliationLeaseAsync(
            new AcquireReconciliationLeaseCommand(
                task.TaskId,
                Guid.NewGuid(),
                "worker-reconcile",
                now.AddSeconds(3),
                now.AddMinutes(3)));
        var reconciled = await store.CommitReconciliationAsync(
            reconciledLease.Value!.Lease,
            new ReconciliationCommitCommand(
                Guid.NewGuid(),
                BackupReconciliationOutcome.SafeToRetry,
                now.AddSeconds(4)));
        var nextClaim = await store.ClaimNextAsync(
            CreateClaimCommand("worker-after-reconcile", now.AddSeconds(5)));

        Assert.Contains(task.TaskId, expiredIds);
        Assert.Equal(BackupTaskStatus.NeedsAttention, expired.Value!.Status);
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, replay.Code);
        Assert.Equal(BackupTaskStatus.Pending, reconciled.Value!.Status);
        Assert.NotEqual(firstAttemptId, nextClaim.Value!.Attempt.Id);
        Assert.Equal(2, nextClaim.Value.Attempt.AttemptNumber);
    }

    [Fact]
    public async Task IndeterminateBackupCanBeReconciledToSucceededAndAdvance()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        var task = await CreateTaskAsync(graph.PolicyId, now);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var claimed = await store.ClaimNextAsync(CreateClaimCommand("worker-unknown", now));
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
                ErrorCode: "connection_lost",
                ErrorMessage: "连接中断，结果不确定"));
        var reconciliation = await store.AcquireReconciliationLeaseAsync(
            new AcquireReconciliationLeaseCommand(
                task.TaskId,
                Guid.NewGuid(),
                "worker-probe",
                now.AddSeconds(3),
                now.AddMinutes(3)));
        var completed = await store.CommitReconciliationAsync(
            reconciliation.Value!.Lease,
            new ReconciliationCommitCommand(
                Guid.NewGuid(),
                BackupReconciliationOutcome.Succeeded,
                now.AddSeconds(4)));

        Assert.Equal(BackupTaskStatus.NeedsAttention, unknown.Value!.Task.Status);
        Assert.Equal(BackupTaskStatus.Pending, completed.Value!.Status);
        Assert.Equal(BackupTaskStage.VerifyLocal, completed.Value.CurrentStage);
        await using var context = database.CreateContext();
        var attempt = await context.BackupAttempts.SingleAsync(x => x.Id == claimed.Value.Attempt.Id);
        Assert.Equal(BackupInvocationStatus.Succeeded, attempt.BackupInvocationStatus);
    }

    [Fact]
    public async Task InconclusiveReconciliationKeepsTaskAndAttemptAndReleasesLeaseAtomically()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        var task = await CreateTaskAsync(graph.PolicyId, now);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var claimed = await store.ClaimNextAsync(CreateClaimCommand("worker-inconclusive", now));
        var marked = await store.MarkBackupInvocationStartedAsync(
            claimed.Value!.Lease,
            claimed.Value.Attempt.RowVersion,
            now.AddSeconds(1));
        await store.CommitStageAsync(
            marked.Value!,
            new BackupStageCommitCommand(
                Guid.NewGuid(),
                BackupStageOutcome.Indeterminate,
                now.AddSeconds(2),
                ErrorCode: "connection_lost",
                ErrorMessage: "连接中断，结果不确定"));
        var reconciliation = await store.AcquireReconciliationLeaseAsync(
            new AcquireReconciliationLeaseCommand(
                task.TaskId,
                Guid.NewGuid(),
                "worker-reconcile",
                now.AddSeconds(3),
                now.AddMinutes(3)));
        var mutationId = Guid.NewGuid();
        var command = new ReconciliationCommitCommand(
            mutationId,
            BackupReconciliationOutcome.Inconclusive,
            now.AddSeconds(4),
            ErrorCode: "reconciliation_evidence_insufficient",
            ErrorMessage: "只读证据不足，任务继续等待核对");

        var result = await store.CommitReconciliationAsync(reconciliation.Value!.Lease, command);
        var replay = await store.CommitReconciliationAsync(reconciliation.Value.Lease, command);
        var delayedCandidates = await store.FindReconciliationCandidateTaskIdsAsync(
            now.AddSeconds(5),
            10);
        var delayedReacquire = await store.AcquireReconciliationLeaseAsync(
            new AcquireReconciliationLeaseCommand(
                task.TaskId,
                Guid.NewGuid(),
                "worker-reconcile-next",
                now.AddSeconds(5),
                now.AddMinutes(4)));
        var eligibleAt = now.AddMinutes(1).AddSeconds(4);
        var candidates = await store.FindReconciliationCandidateTaskIdsAsync(eligibleAt, 10);
        var reacquired = await store.AcquireReconciliationLeaseAsync(
            new AcquireReconciliationLeaseCommand(
                task.TaskId,
                Guid.NewGuid(),
                "worker-reconcile-after-delay",
                eligibleAt,
                eligibleAt.AddMinutes(1)));

        Assert.Equal(BackupTaskStoreResultCode.Succeeded, result.Code);
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, replay.Code);
        Assert.Equal(BackupTaskStatus.NeedsAttention, result.Value!.Status);
        Assert.Equal(BackupTaskStage.Backup, result.Value.CurrentStage);
        Assert.Equal("reconciliation_evidence_insufficient", result.Value.ErrorCode);
        Assert.Equal("只读证据不足，任务继续等待核对", result.Value.ErrorMessage);
        Assert.Null(result.Value.CompletedAtUtc);
        Assert.Equal(1, result.Value.ReconciliationAttemptCount);
        Assert.Equal(now.AddMinutes(1).AddSeconds(4), result.Value.NextReconciliationAtUtc);
        Assert.Empty(delayedCandidates);
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch, delayedReacquire.Code);
        Assert.Contains(task.TaskId, candidates);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, reacquired.Code);

        await using var context = database.CreateContext();
        var attempt = await context.BackupAttempts.SingleAsync(x => x.Id == claimed.Value.Attempt.Id);
        Assert.Equal(BackupInvocationStatus.Indeterminate, attempt.BackupInvocationStatus);
        Assert.Equal(
            1,
            await context.BackupTaskStateChanges.CountAsync(x => x.MutationId == mutationId));
        Assert.Equal(1, await context.TaskEvents.CountAsync(x => x.EventId == mutationId));
        Assert.Equal(
            1,
            await context.AuditRecords.CountAsync(x =>
                x.TargetId == task.TaskId.ToString("N")
                && x.Action == "backup.task.reconcile"
                && x.ReasonCode == "reconciliation.inconclusive"));
    }

    [Fact]
    public async Task AdminReconciliationRequestIsAtomicIdempotentAndPreservesEvidence()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var taskId = await P57ReconciliationStoreHarness.SeedIndeterminateNeedsAttentionAsync(
            store,
            graph.PolicyId,
            now,
            "worker-admin-request");
        var lease = await P57ReconciliationStoreHarness.AcquireAsync(
            store,
            taskId,
            now.AddSeconds(3),
            now.AddMinutes(1),
            "worker-admin-request-first");
        await store.CommitReconciliationAsync(
            lease.Lease,
            P57ReconciliationStoreHarness.InconclusiveCommand(
                now.AddSeconds(4),
                errorCode: "reconciliation.file_missing",
                errorMessage: "未发现稳定文件证据"));
        var actorId = Guid.NewGuid();
        const string securityStamp = "admin-reconciliation-stamp";
        await using (var seed = database.CreateContext())
        {
            seed.AdminUsers.Add(new AdminUser(
                actorId,
                "reconciliation-admin",
                "RECONCILIATION-ADMIN",
                "synthetic-password-hash",
                securityStamp));
            await seed.SaveChangesAsync();
        }

        var mutationId = Guid.NewGuid();
        var requestedAt = now.AddSeconds(10);
        var command = new BackupTaskMutationCommand(
            taskId,
            mutationId,
            requestedAt,
            actorId,
            securityStamp);
        var result = await store.RequestReconciliationAsync(command);
        var replay = await store.RequestReconciliationAsync(command);
        var differentAction = await store.RequestCancellationAsync(command);

        Assert.Equal(BackupTaskStoreResultCode.Succeeded, result.Code);
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, replay.Code);
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch, differentAction.Code);
        Assert.Equal(BackupTaskStatus.NeedsAttention, result.Value!.Status);
        Assert.Equal(BackupTaskStage.Backup, result.Value.CurrentStage);
        Assert.Equal(1, result.Value.ReconciliationAttemptCount);
        Assert.Equal(requestedAt, result.Value.NextReconciliationAtUtc);
        Assert.Equal("reconciliation.file_missing", result.Value.ErrorCode);
        Assert.Equal("未发现稳定文件证据", result.Value.ErrorMessage);

        await using var context = database.CreateContext();
        Assert.Equal(1, await context.BackupTaskStateChanges.CountAsync(x =>
            x.MutationId == mutationId
            && x.FromStatus == BackupTaskStatus.NeedsAttention
            && x.ToStatus == BackupTaskStatus.NeedsAttention
            && x.FromStage == BackupTaskStage.Backup
            && x.ToStage == BackupTaskStage.Backup
            && x.ReasonCode == "admin_reconciliation_requested"));
        Assert.Equal(1, await context.TaskEvents.CountAsync(x => x.EventId == mutationId));
        Assert.Equal(1, await context.AuditRecords.CountAsync(x =>
            x.ActorAdminUserId == actorId
            && x.TargetId == taskId.ToString("N")
            && x.Action == "backup.task.reconciliation.request"
            && x.ReasonCode == "admin_reconciliation_requested"));
        Assert.Equal(0, await context.BackupFiles.CountAsync(x => x.TaskId == taskId));
    }

    [Fact]
    public async Task AdminReconciliationRequestRejectsInvalidActorAndActiveLeaseWithoutSideEffects()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var taskId = await P57ReconciliationStoreHarness.SeedIndeterminateNeedsAttentionAsync(
            store,
            graph.PolicyId,
            now,
            "worker-admin-reject");
        var invalidMutation = Guid.NewGuid();

        var invalidActor = await store.RequestReconciliationAsync(new BackupTaskMutationCommand(
            taskId,
            invalidMutation,
            now.AddSeconds(3),
            Guid.NewGuid(),
            "revoked"));
        var lease = await P57ReconciliationStoreHarness.AcquireAsync(
            store,
            taskId,
            now.AddSeconds(4),
            now.AddMinutes(1),
            "worker-admin-active");
        var actorId = Guid.NewGuid();
        const string securityStamp = "admin-active-lease-stamp";
        await using (var seed = database.CreateContext())
        {
            seed.AdminUsers.Add(new AdminUser(
                actorId,
                "active-lease-admin",
                "ACTIVE-LEASE-ADMIN",
                "synthetic-password-hash",
                securityStamp));
            await seed.SaveChangesAsync();
        }
        var activeMutation = Guid.NewGuid();
        var activeLease = await store.RequestReconciliationAsync(new BackupTaskMutationCommand(
            taskId,
            activeMutation,
            now.AddSeconds(5),
            actorId,
            securityStamp));

        Assert.Equal(BackupTaskStoreResultCode.AuthenticationRequired, invalidActor.Code);
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch, activeLease.Code);
        Assert.Equal(BackupLeasePurpose.Reconciliation, lease.Lease.Purpose);
        await using var context = database.CreateContext();
        Assert.False(await context.BackupTaskStateChanges.AnyAsync(x =>
            x.MutationId == invalidMutation || x.MutationId == activeMutation));
        Assert.False(await context.TaskEvents.AnyAsync(x =>
            x.EventId == invalidMutation || x.EventId == activeMutation));
        Assert.False(await context.AuditRecords.AnyAsync(x =>
            x.ActorAdminUserId == actorId
            && x.Action == "backup.task.reconciliation.request"));
    }

    [Fact]
    public async Task ExpiredReconciliationLeaseIsCandidateAndCanBeReplaced()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        var task = await CreateTaskAsync(graph.PolicyId, now);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var claimed = await store.ClaimNextAsync(CreateClaimCommand("worker-expired-reconcile", now));
        await store.ExpireExecutionLeaseAsync(new ExpireExecutionLeaseCommand(
            task.TaskId,
            Guid.NewGuid(),
            claimed.Value!.Lease.ExpiresAtUtc,
            "lease_expired",
            "执行租约已过期，需要核对"));
        var first = await store.AcquireReconciliationLeaseAsync(
            new AcquireReconciliationLeaseCommand(
                task.TaskId,
                Guid.NewGuid(),
                "reconciler-expired",
                claimed.Value.Lease.ExpiresAtUtc.AddSeconds(1),
                claimed.Value.Lease.ExpiresAtUtc.AddSeconds(2)));
        var afterExpiry = claimed.Value.Lease.ExpiresAtUtc.AddSeconds(3);

        var candidates = await store.FindReconciliationCandidateTaskIdsAsync(afterExpiry, 10);
        var replacement = await store.AcquireReconciliationLeaseAsync(
            new AcquireReconciliationLeaseCommand(
                task.TaskId,
                Guid.NewGuid(),
                "reconciler-replacement",
                afterExpiry,
                afterExpiry.AddMinutes(1)));

        Assert.Equal(BackupTaskStoreResultCode.Succeeded, first.Code);
        Assert.Contains(task.TaskId, candidates);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, replacement.Code);
        Assert.NotEqual(first.Value!.Lease.LeaseToken, replacement.Value!.Lease.LeaseToken);
        Assert.Equal(BackupLeasePurpose.Reconciliation, replacement.Value.Lease.Purpose);
    }

    [Theory]
    [InlineData(BackupReconciliationOutcome.Failed, BackupTaskStatus.Failed)]
    [InlineData(BackupReconciliationOutcome.Cancelled, BackupTaskStatus.Cancelled)]
    public async Task ReconciliationCanCommitTerminalOutcome(
        BackupReconciliationOutcome outcome,
        BackupTaskStatus expectedStatus)
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        var task = await CreateTaskAsync(graph.PolicyId, now);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var claimed = await store.ClaimNextAsync(CreateClaimCommand("worker-terminal", now));
        var marked = await store.MarkBackupInvocationStartedAsync(
            claimed.Value!.Lease,
            claimed.Value.Attempt.RowVersion,
            now.AddSeconds(1));
        await store.CommitStageAsync(
            marked.Value!,
            new BackupStageCommitCommand(
                Guid.NewGuid(),
                BackupStageOutcome.Indeterminate,
                now.AddSeconds(2),
                ErrorCode: "result_unknown",
                ErrorMessage: "合成的不确定结果"));
        var reconciliation = await store.AcquireReconciliationLeaseAsync(
            new AcquireReconciliationLeaseCommand(
                task.TaskId,
                Guid.NewGuid(),
                "worker-terminal-reconcile",
                now.AddSeconds(3),
                now.AddMinutes(3)));

        var result = await store.CommitReconciliationAsync(
            reconciliation.Value!.Lease,
            new ReconciliationCommitCommand(
                Guid.NewGuid(),
                outcome,
                now.AddSeconds(4),
                ErrorCode: outcome == BackupReconciliationOutcome.Failed
                    ? "confirmed_failed"
                    : null,
                ErrorMessage: outcome == BackupReconciliationOutcome.Failed
                    ? "合成的确认失败"
                    : null));

        Assert.Equal(expectedStatus, result.Value!.Status);
        Assert.NotNull(result.Value.CompletedAtUtc);
    }

    [Fact]
    public async Task AdminCanConfirmFailedNeedsAttentionWithoutLeaseAndReplay()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        var task = await CreateTaskAsync(graph.PolicyId, now);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var claimed = await store.ClaimNextAsync(CreateClaimCommand("worker-confirm", now));
        var marked = await store.MarkBackupInvocationStartedAsync(
            claimed.Value!.Lease,
            claimed.Value.Attempt.RowVersion,
            now.AddSeconds(1));
        await store.CommitStageAsync(
            marked.Value!,
            new BackupStageCommitCommand(
                Guid.NewGuid(),
                BackupStageOutcome.Indeterminate,
                now.AddSeconds(2),
                ErrorCode: "result_unknown",
                ErrorMessage: "合成的不确定结果"));
        var mutationId = Guid.NewGuid();
        var command = new ConfirmNeedsAttentionCommand(
            task.TaskId,
            mutationId,
            now.AddSeconds(4),
            BackupReconciliationOutcome.Failed,
            "admin.confirmed_failed",
            "维护者确认中断备份未产生有效副本");

        var first = await store.ConfirmNeedsAttentionAsync(command);
        var replay = await store.ConfirmNeedsAttentionAsync(command);

        Assert.Equal(BackupTaskStatus.Failed, first.Value!.Status);
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, replay.Code);
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.NotificationOutbox.CountAsync(item => item.MutationId == mutationId));
        Assert.True(await db.AuditRecords.AnyAsync(item =>
            item.Action == "backup.task.reconciliation.confirm_failed"
            && item.ReasonCode == "admin.confirmed_failed"));
    }

    [Fact]
    public async Task AdminCanConfirmCancelledNeedsAttentionAndRejectsOutcomeReuse()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        var task = await CreateTaskAsync(graph.PolicyId, now);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var claimed = await store.ClaimNextAsync(CreateClaimCommand("worker-confirm-cancel", now));
        var marked = await store.MarkBackupInvocationStartedAsync(
            claimed.Value!.Lease,
            claimed.Value.Attempt.RowVersion,
            now.AddSeconds(1));
        await store.CommitStageAsync(
            marked.Value!,
            new BackupStageCommitCommand(
                Guid.NewGuid(),
                BackupStageOutcome.Indeterminate,
                now.AddSeconds(2),
                ErrorCode: "result_unknown",
                ErrorMessage: "合成的不确定结果"));
        var mutationId = Guid.NewGuid();
        var command = new ConfirmNeedsAttentionCommand(
            task.TaskId,
            mutationId,
            now.AddSeconds(4),
            BackupReconciliationOutcome.Cancelled);

        var first = await store.ConfirmNeedsAttentionAsync(command);
        var replay = await store.ConfirmNeedsAttentionAsync(command);
        var reused = await store.ConfirmNeedsAttentionAsync(command with
        {
            Outcome = BackupReconciliationOutcome.Failed,
            ErrorCode = "admin.confirmed_failed",
            ErrorMessage = "维护者确认失败",
        });

        Assert.Equal(BackupTaskStatus.Cancelled, first.Value!.Status);
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, replay.Code);
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch, reused.Code);
        await using var db = database.CreateContext();
        Assert.Equal(0, await db.NotificationOutbox.CountAsync(item => item.MutationId == mutationId));
        Assert.True(await db.AuditRecords.AnyAsync(item =>
            item.Action == "backup.task.reconciliation.confirm_cancelled"
            && item.ReasonCode == "admin.confirmed_cancelled"));
    }

    [Fact]
    public async Task ReconciliationCandidatesExcludeRunningTerminalAndActiveLeaseAndHonorLimitOrder()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();

        var failed = await P57ReconciliationStoreHarness.CreateTaskAsync(store, graph.PolicyId, now);
        var failedClaim = await store.ClaimNextAsync(
            P57ReconciliationStoreHarness.CreateClaimCommand("worker-failed", now));
        await store.CommitStageAsync(
            failedClaim.Value!.Lease,
            new BackupStageCommitCommand(
                Guid.NewGuid(),
                BackupStageOutcome.ConfirmedFailed,
                now.AddSeconds(1),
                ErrorCode: "backup_rejected",
                ErrorMessage: "目标明确拒绝备份"));

        var cancelled = await P57ReconciliationStoreHarness.CreateTaskAsync(
            store,
            graph.PolicyId,
            now.AddSeconds(2));
        await store.RequestCancellationAsync(new BackupTaskMutationCommand(
            cancelled.TaskId,
            Guid.NewGuid(),
            now.AddSeconds(3)));

        var succeeded = await P57ReconciliationStoreHarness.CreateTaskAsync(
            store,
            graph.PolicyId,
            now.AddSeconds(4));
        var succeededClaim = await store.ClaimNextAsync(
            P57ReconciliationStoreHarness.CreateClaimCommand("worker-succeeded", now.AddSeconds(4)));
        var marked = await store.MarkBackupInvocationStartedAsync(
            succeededClaim.Value!.Lease,
            succeededClaim.Value.Attempt.RowVersion,
            now.AddSeconds(5));
        var backup = await store.CommitStageAsync(
            marked.Value!,
            new BackupStageCommitCommand(
                Guid.NewGuid(),
                BackupStageOutcome.Succeeded,
                now.AddSeconds(6)));
        await store.CommitStageAsync(
            backup.Value!.Lease!,
            new BackupStageCommitCommand(
                Guid.NewGuid(),
                BackupStageOutcome.Succeeded,
                now.AddSeconds(7),
                SourceLengthBytes: 2048));

        var running = await P57ReconciliationStoreHarness.CreateTaskAsync(
            store,
            graph.PolicyId,
            now.AddSeconds(8));
        await store.ClaimNextAsync(
            P57ReconciliationStoreHarness.CreateClaimCommand("worker-running", now.AddSeconds(8)));

        var firstCandidate = await P57ReconciliationStoreHarness.SeedNeedsAttentionAsync(
            store,
            graph.PolicyId,
            now.AddSeconds(10),
            "worker-candidate-1");
        var secondCandidate = await P57ReconciliationStoreHarness.SeedNeedsAttentionAsync(
            store,
            graph.PolicyId,
            now.AddSeconds(12),
            "worker-candidate-2");
        var thirdCandidate = await P57ReconciliationStoreHarness.SeedNeedsAttentionAsync(
            store,
            graph.PolicyId,
            now.AddSeconds(14),
            "worker-candidate-3");
        var leaseAnchor = now.AddMinutes(1);
        var firstLease = await P57ReconciliationStoreHarness.AcquireAsync(
            store,
            firstCandidate,
            leaseAnchor,
            leaseAnchor.AddSeconds(1),
            "reconciler-1");
        await P57ReconciliationStoreHarness.AcquireAsync(
            store,
            secondCandidate,
            leaseAnchor.AddSeconds(1),
            leaseAnchor.AddSeconds(2),
            "reconciler-2");
        await P57ReconciliationStoreHarness.AcquireAsync(
            store,
            thirdCandidate,
            leaseAnchor.AddSeconds(2),
            leaseAnchor.AddSeconds(3),
            "reconciler-3");

        var activeLeaseTask = await P57ReconciliationStoreHarness.SeedNeedsAttentionAsync(
            store,
            graph.PolicyId,
            now.AddSeconds(18),
            "worker-active");
        await P57ReconciliationStoreHarness.AcquireAsync(
            store,
            activeLeaseTask,
            leaseAnchor.AddSeconds(5),
            leaseAnchor.AddHours(1),
            "reconciler-active");

        var pending = await P57ReconciliationStoreHarness.CreateTaskAsync(
            store,
            graph.PolicyId,
            now.AddSeconds(20));

        var queriedAt = leaseAnchor.AddMinutes(1);
        var limited = await store.FindReconciliationCandidateTaskIdsAsync(queriedAt, 2);
        var all = await store.FindReconciliationCandidateTaskIdsAsync(queriedAt, 10);

        Assert.Equal([firstCandidate, secondCandidate], limited);
        Assert.Equal([firstCandidate, secondCandidate, thirdCandidate], all);
        Assert.DoesNotContain(failed.TaskId, all);
        Assert.DoesNotContain(cancelled.TaskId, all);
        Assert.DoesNotContain(succeeded.TaskId, all);
        Assert.DoesNotContain(running.TaskId, all);
        Assert.DoesNotContain(activeLeaseTask, all);
        Assert.DoesNotContain(pending.TaskId, all);
        Assert.Equal(BackupLeasePurpose.Reconciliation, firstLease.Lease.Purpose);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.FindReconciliationCandidateTaskIdsAsync(queriedAt, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.FindReconciliationCandidateTaskIdsAsync(queriedAt, 1001));
    }

    [Fact]
    public async Task ReleasedReconciliationLeaseHonorsRetryDelayBeforeBecomingCandidate()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var taskId = await P57ReconciliationStoreHarness.SeedIndeterminateNeedsAttentionAsync(
            store,
            graph.PolicyId,
            now,
            "worker-released");
        var work = await P57ReconciliationStoreHarness.AcquireAsync(
            store,
            taskId,
            now.AddSeconds(3),
            now.AddMinutes(1),
            "reconciler-released");
        var committed = await store.CommitReconciliationAsync(
            work.Lease,
            P57ReconciliationStoreHarness.InconclusiveCommand(now.AddSeconds(4)));

        var delayed = await store.FindReconciliationCandidateTaskIdsAsync(now.AddSeconds(5), 10);
        var candidates = await store.FindReconciliationCandidateTaskIdsAsync(
            now.AddMinutes(1).AddSeconds(4),
            10);

        Assert.Equal(BackupTaskStoreResultCode.Succeeded, committed.Code);
        Assert.Empty(delayed);
        Assert.Equal([taskId], candidates);
    }

    [Fact]
    public async Task ConcurrentStoresReplaceExpiredReconciliationLeaseOnceAndRejectOtherTokens()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        using var provider = database.CreateServiceProvider();
        using var seedScope = provider.CreateScope();
        var seedStore = seedScope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var taskId = await P57ReconciliationStoreHarness.SeedNeedsAttentionAsync(
            seedStore,
            graph.PolicyId,
            now,
            "worker-seed");
        var original = await P57ReconciliationStoreHarness.AcquireAsync(
            seedStore,
            taskId,
            now.AddSeconds(1),
            now.AddSeconds(2),
            "reconciler-original");
        var afterExpiry = now.AddSeconds(3);

        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var firstStore = firstScope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var secondStore = secondScope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var firstCommand = new AcquireReconciliationLeaseCommand(
            taskId,
            Guid.NewGuid(),
            "reconciler-a",
            afterExpiry,
            afterExpiry.AddMinutes(1));
        var secondCommand = new AcquireReconciliationLeaseCommand(
            taskId,
            Guid.NewGuid(),
            "reconciler-b",
            afterExpiry,
            afterExpiry.AddMinutes(1));

        var results = await Task.WhenAll(
            firstStore.AcquireReconciliationLeaseAsync(firstCommand),
            secondStore.AcquireReconciliationLeaseAsync(secondCommand));

        Assert.Single(results, result => result.Code == BackupTaskStoreResultCode.Succeeded);
        Assert.Single(
            results,
            result => result.Code is BackupTaskStoreResultCode.ConcurrencyConflict
                or BackupTaskStoreResultCode.StateMismatch);
        var winner = results.Single(result => result.Code == BackupTaskStoreResultCode.Succeeded).Value!;
        var winnerStore = results[0].Code == BackupTaskStoreResultCode.Succeeded ? firstStore : secondStore;

        var originalRejected = await winnerStore.CommitReconciliationAsync(
            original.Lease,
            P57ReconciliationStoreHarness.InconclusiveCommand(afterExpiry.AddSeconds(1)));
        var foreignRejected = await winnerStore.CommitReconciliationAsync(
            P57ReconciliationStoreHarness.WithToken(winner.Lease, Guid.NewGuid()),
            P57ReconciliationStoreHarness.InconclusiveCommand(afterExpiry.AddSeconds(1)));
        var committed = await winnerStore.CommitReconciliationAsync(
            winner.Lease,
            P57ReconciliationStoreHarness.InconclusiveCommand(afterExpiry.AddSeconds(2)));

        Assert.Equal(BackupTaskStoreResultCode.LeaseLost, originalRejected.Code);
        Assert.Equal(BackupTaskStoreResultCode.LeaseLost, foreignRejected.Code);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, committed.Code);
        Assert.Equal(BackupTaskStatus.NeedsAttention, committed.Value!.Status);
    }

    [Fact]
    public async Task ReplacedReconciliationTokenCannotCommitAfterTakeover()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var taskId = await P57ReconciliationStoreHarness.SeedNeedsAttentionAsync(
            store,
            graph.PolicyId,
            now,
            "worker-replaced");
        var first = await P57ReconciliationStoreHarness.AcquireAsync(
            store,
            taskId,
            now.AddSeconds(1),
            now.AddSeconds(2),
            "reconciler-first");
        var replacement = await P57ReconciliationStoreHarness.AcquireAsync(
            store,
            taskId,
            now.AddSeconds(3),
            now.AddMinutes(1),
            "reconciler-second");

        var replaced = await store.CommitReconciliationAsync(
            first.Lease,
            P57ReconciliationStoreHarness.InconclusiveCommand(now.AddSeconds(4)));
        var current = await store.CommitReconciliationAsync(
            replacement.Lease,
            P57ReconciliationStoreHarness.InconclusiveCommand(now.AddSeconds(5)));

        Assert.Equal(BackupTaskStoreResultCode.LeaseLost, replaced.Code);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, current.Code);
    }

    [Theory]
    [InlineData(null, "只读证据不足，任务继续等待核对")]
    [InlineData("reconciliation_evidence_insufficient", null)]
    [InlineData("", "只读证据不足，任务继续等待核对")]
    [InlineData("reconciliation_evidence_insufficient", "")]
    public async Task InvalidInconclusiveCommitRollsBackHistoryAuditEventAndFile(
        string? errorCode,
        string? errorMessage)
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var taskId = await P57ReconciliationStoreHarness.SeedIndeterminateNeedsAttentionAsync(
            store,
            graph.PolicyId,
            now,
            "worker-invalid");
        var work = await P57ReconciliationStoreHarness.AcquireAsync(
            store,
            taskId,
            now.AddSeconds(3),
            now.AddMinutes(1),
            "reconciler-invalid");

        var mutationId = Guid.NewGuid();
        var rejected = await store.CommitReconciliationAsync(
            work.Lease,
            new ReconciliationCommitCommand(
                mutationId,
                BackupReconciliationOutcome.Inconclusive,
                now.AddSeconds(4),
                ErrorCode: errorCode,
                ErrorMessage: errorMessage));

        Assert.Equal(BackupTaskStoreResultCode.StateMismatch, rejected.Code);
        var stillHeld = await store.FindTaskAsync(taskId);
        Assert.Equal(BackupTaskStatus.NeedsAttention, stillHeld!.Status);
        Assert.Equal("connection_lost", stillHeld.ErrorCode);

        await using var context = database.CreateContext();
        Assert.Equal(0, await context.BackupTaskStateChanges.CountAsync(x => x.MutationId == mutationId));
        Assert.Equal(0, await context.TaskEvents.CountAsync(x => x.EventId == mutationId));
        Assert.Equal(
            0,
            await context.AuditRecords.CountAsync(x =>
                x.TargetId == taskId.ToString("N")
                && x.Action == "backup.task.reconcile"
                && x.ReasonCode == "reconciliation.inconclusive"));
        Assert.Equal(0, await context.BackupFiles.CountAsync(x => x.TaskId == taskId));
        var attempt = await context.BackupAttempts.SingleAsync(x => x.Id == work.Attempt.Id);
        Assert.Equal(BackupInvocationStatus.Indeterminate, attempt.BackupInvocationStatus);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task InconclusiveCommitRejectsNonPositiveSourceLengthWithoutSideEffects(
        long sourceLengthBytes)
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var taskId = await P57ReconciliationStoreHarness.SeedIndeterminateNeedsAttentionAsync(
            store,
            graph.PolicyId,
            now,
            "worker-length");
        var work = await P57ReconciliationStoreHarness.AcquireAsync(
            store,
            taskId,
            now.AddSeconds(3),
            now.AddMinutes(1),
            "reconciler-length");

        var mutationId = Guid.NewGuid();
        var result = await store.CommitReconciliationAsync(
            work.Lease,
            P57ReconciliationStoreHarness.InconclusiveCommand(
                now.AddSeconds(4),
                mutationId,
                sourceLengthBytes: sourceLengthBytes));

        Assert.Equal(BackupTaskStoreResultCode.StateMismatch, result.Code);
        var stillHeld = await store.FindTaskAsync(taskId);
        Assert.Equal(BackupTaskStatus.NeedsAttention, stillHeld!.Status);
        Assert.Equal("connection_lost", stillHeld.ErrorCode);

        await using var context = database.CreateContext();
        Assert.Equal(0, await context.BackupTaskStateChanges.CountAsync(x => x.MutationId == mutationId));
        Assert.Equal(0, await context.TaskEvents.CountAsync(x => x.EventId == mutationId));
        Assert.Equal(
            0,
            await context.AuditRecords.CountAsync(x =>
                x.TargetId == taskId.ToString("N")
                && x.Action == "backup.task.reconcile"
                && x.ReasonCode == "reconciliation.inconclusive"));
        Assert.Equal(0, await context.BackupFiles.CountAsync(x => x.TaskId == taskId));
        var attempt = await context.BackupAttempts.SingleAsync(x => x.Id == work.Attempt.Id);
        Assert.Equal(BackupInvocationStatus.Indeterminate, attempt.BackupInvocationStatus);
        Assert.Null(attempt.SourceLengthBytes);
    }

    [Fact]
    public async Task InconclusiveCommitWithWrongAttemptRollsBackAndKeepsLease()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var taskId = await P57ReconciliationStoreHarness.SeedNeedsAttentionAsync(
            store,
            graph.PolicyId,
            now,
            "worker-wrong-attempt");
        var work = await P57ReconciliationStoreHarness.AcquireAsync(
            store,
            taskId,
            now.AddSeconds(1),
            now.AddMinutes(1),
            "reconciler-wrong-attempt");
        var mutationId = Guid.NewGuid();

        var rejected = await store.CommitReconciliationAsync(
            P57ReconciliationStoreHarness.WithAttempt(work.Lease, Guid.NewGuid()),
            P57ReconciliationStoreHarness.InconclusiveCommand(now.AddSeconds(2), mutationId));

        Assert.Equal(BackupTaskStoreResultCode.LeaseLost, rejected.Code);
        await using var context = database.CreateContext();
        Assert.Equal(0, await context.BackupTaskStateChanges.CountAsync(x => x.MutationId == mutationId));
        Assert.Equal(0, await context.TaskEvents.CountAsync(x => x.EventId == mutationId));
        Assert.Equal(
            0,
            await context.AuditRecords.CountAsync(x =>
                x.TargetId == taskId.ToString("N")
                && x.Action == "backup.task.reconcile"
                && x.ReasonCode == "reconciliation.inconclusive"));
        Assert.Equal(0, await context.BackupFiles.CountAsync(x => x.TaskId == taskId));
        var stillHeld = await store.FindTaskAsync(taskId);
        Assert.Equal(BackupTaskStatus.NeedsAttention, stillHeld!.Status);
    }

    [Fact]
    public async Task InconclusiveMutationReplayIsIdempotentAndDifferentReasonIsRejected()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var taskId = await P57ReconciliationStoreHarness.SeedNeedsAttentionAsync(
            store,
            graph.PolicyId,
            now,
            "worker-mutation");
        var work = await P57ReconciliationStoreHarness.AcquireAsync(
            store,
            taskId,
            now.AddSeconds(1),
            now.AddMinutes(1),
            "reconciler-mutation");
        var mutationId = Guid.NewGuid();
        var command = P57ReconciliationStoreHarness.InconclusiveCommand(now.AddSeconds(2), mutationId);

        var first = await store.CommitReconciliationAsync(work.Lease, command);
        var replay = await store.CommitReconciliationAsync(work.Lease, command);
        var differentReason = await store.CommitReconciliationAsync(
            work.Lease,
            command with { Outcome = BackupReconciliationOutcome.Failed });

        Assert.Equal(BackupTaskStoreResultCode.Succeeded, first.Code);
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, replay.Code);
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch, differentReason.Code);
        Assert.Equal(BackupTaskStatus.NeedsAttention, first.Value!.Status);

        await using var context = database.CreateContext();
        Assert.Equal(1, await context.BackupTaskStateChanges.CountAsync(x => x.MutationId == mutationId));
        Assert.Equal(1, await context.TaskEvents.CountAsync(x => x.EventId == mutationId));
        Assert.Equal(
            1,
            await context.AuditRecords.CountAsync(x =>
                x.TargetId == taskId.ToString("N")
                && x.Action == "backup.task.reconcile"
                && x.ReasonCode == "reconciliation.inconclusive"));
        Assert.Equal(0, await context.BackupFiles.CountAsync(x => x.TaskId == taskId));
    }

    [Fact]
    public async Task MutationIdCannotBeReusedForDifferentCommand()
    {
        var graph = await AddConfigurationGraphAsync(BackupStorageMode.LocalOnly);
        var now = DateTimeOffset.UtcNow;
        var create = CreateManualTaskCommand(graph.PolicyId, now);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        await store.CreateTaskAsync(create);

        var collision = await store.RequestCancellationAsync(new BackupTaskMutationCommand(
            create.TaskId,
            create.MutationId,
            now.AddSeconds(1)));

        Assert.Equal(BackupTaskStoreResultCode.StateMismatch, collision.Code);
        var state = await store.FindTaskAsync(create.TaskId);
        Assert.Equal(BackupTaskStatus.Pending, state!.Status);
    }

    private async Task<BackupTaskStateModel> CreateTaskAsync(Guid policyId, DateTimeOffset now)
    {
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var result = await store.CreateTaskAsync(CreateManualTaskCommand(policyId, now));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, result.Code);
        return result.Value!;
    }

    private static CreateBackupTaskCommand CreateManualTaskCommand(
        Guid policyId,
        DateTimeOffset now)
    {
        return new CreateBackupTaskCommand(
            Guid.NewGuid(),
            policyId,
            BackupTaskTriggerType.Manual,
            null,
            Guid.NewGuid(),
            now);
    }

    private static ClaimNextBackupTaskCommand CreateClaimCommand(
        string owner,
        DateTimeOffset now)
    {
        return new ClaimNextBackupTaskCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            owner,
            now,
            now.AddMinutes(5));
    }

    private async Task<ConfigurationGraph> AddConfigurationGraphAsync(BackupStorageMode storageMode, bool legacy = false)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var sourceCredential = new CredentialReference(
            Guid.NewGuid(),
            $"StoreSourceCredential-{suffix}",
            CredentialKind.SmbPassword,
            "synthetic-source-user",
            "protected:synthetic-source-secret",
            "dp-v1");
        var sqlCredential = new CredentialReference(
            Guid.NewGuid(),
            $"StoreSqlCredential-{suffix}",
            CredentialKind.SqlPassword,
            "synthetic-sql-user",
            "protected:synthetic-sql-secret",
            "dp-v1");
        var server = new DatabaseServer(
            Guid.NewGuid(),
            $"StoreServer-{suffix}",
            @"D:\Synthetic\SqlBackup",
            new FileEndpointSettings(
                FileTransferProtocol.Smb,
                "synthetic-source-host",
                null,
                "synthetic-source-share",
                sourceCredential.Id,
                null));
        var instance = new DatabaseInstance(
            Guid.NewGuid(),
            server.Id,
            $"StoreInstance-{suffix}",
            $"synthetic-sql-{suffix}",
            sqlCredential.Id,
            encryptConnection: true,
            trustServerCertificate: false,
            certificateTrustReason: null,
            connectionTimeoutSeconds: 30, allowLegacyTls: legacy, legacyTlsReason: legacy ? "legacy fixture" : null);
        var managedDatabase = new ManagedDatabase(
            Guid.NewGuid(),
            instance.Id,
            $"StoreDatabase_{suffix}",
            isSystemDatabase: false,
            isAvailable: true,
            DateTimeOffset.UtcNow,
            "FULL",
            "ONLINE");
        managedDatabase.SetManaged(true);

        CredentialReference? remoteCredential = null;
        StorageTarget? target = null;
        if (storageMode != BackupStorageMode.LocalOnly)
        {
            remoteCredential = new CredentialReference(
                Guid.NewGuid(),
                $"StoreRemoteCredential-{suffix}",
                CredentialKind.SftpPassword,
                "synthetic-remote-user",
                "protected:synthetic-remote-secret",
                "dp-v1");
            target = new StorageTarget(
                Guid.NewGuid(),
                $"StoreTarget-{suffix}",
                new FileEndpointSettings(
                    FileTransferProtocol.Sftp,
                    "synthetic-remote-host",
                    22,
                    "/synthetic/remote",
                    remoteCredential.Id,
                    "SHA256:synthetic-host-key"));
        }

        var policy = new BackupPolicy(
            Guid.NewGuid(),
            $"StorePolicy-{suffix}",
            managedDatabase.Id,
            new BackupPolicySettings(
                storageMode,
                target?.Id,
                BackupScheduleType.Daily,
                new TimeOnly(2, 0),
                BackupWeekdays.None,
                "Taipei Standard Time",
                LocalRetentionDays: storageMode == BackupStorageMode.RemoteOnly ? null : 7,
                RemoteRetentionDays: storageMode == BackupStorageMode.LocalOnly ? null : 30,
                UseChecksum: true,
                UseCompression: true,
                UseCopyOnly: false,
                BackupTimeoutMinutes: 120,
                VerifyTimeoutMinutes: 60,
                TransferTimeoutMinutes: 180),
            isEnabled: true,
            nowUtc: DateTimeOffset.UtcNow);

        await using var context = database.CreateContext();
        context.AddRange(sourceCredential, sqlCredential, server, instance, managedDatabase, policy);
        if (target is not null && remoteCredential is not null)
        {
            context.AddRange(remoteCredential, target);
        }

        await context.SaveChangesAsync();
        return new ConfigurationGraph(policy.Id);
    }

    private sealed record ConfigurationGraph(Guid PolicyId);
}
