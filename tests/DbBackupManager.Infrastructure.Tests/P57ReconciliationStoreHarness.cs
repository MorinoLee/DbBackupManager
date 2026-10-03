using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Infrastructure.Tests;

internal static class P57ReconciliationStoreHarness
{
    public static CreateBackupTaskCommand CreateManualTaskCommand(Guid policyId, DateTimeOffset now)
    {
        return new CreateBackupTaskCommand(
            Guid.NewGuid(),
            policyId,
            BackupTaskTriggerType.Manual,
            null,
            Guid.NewGuid(),
            now);
    }

    public static ClaimNextBackupTaskCommand CreateClaimCommand(string owner, DateTimeOffset now)
    {
        return new ClaimNextBackupTaskCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            owner,
            now,
            now.AddMinutes(5));
    }

    public static async Task<BackupTaskStateModel> CreateTaskAsync(
        IBackupTaskExecutionStore store,
        Guid policyId,
        DateTimeOffset now)
    {
        var result = await store.CreateTaskAsync(CreateManualTaskCommand(policyId, now));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, result.Code);
        return result.Value!;
    }

    public static async Task<Guid> SeedNeedsAttentionAsync(
        IBackupTaskExecutionStore store,
        Guid policyId,
        DateTimeOffset now,
        string owner)
    {
        var task = await CreateTaskAsync(store, policyId, now);
        var claimed = await store.ClaimNextAsync(new ClaimNextBackupTaskCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            owner,
            now,
            now.AddSeconds(1)));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, claimed.Code);
        Assert.Equal(task.TaskId, claimed.Value!.Task.TaskId);
        var expired = await store.ExpireExecutionLeaseAsync(new ExpireExecutionLeaseCommand(
            task.TaskId,
            Guid.NewGuid(),
            claimed.Value.Lease.ExpiresAtUtc,
            "lease_expired",
            "执行租约已过期，需要核对"));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, expired.Code);
        return task.TaskId;
    }

    public static async Task<Guid> SeedIndeterminateNeedsAttentionAsync(
        IBackupTaskExecutionStore store,
        Guid policyId,
        DateTimeOffset now,
        string owner)
    {
        var task = await CreateTaskAsync(store, policyId, now);
        var claimed = await store.ClaimNextAsync(CreateClaimCommand(owner, now));
        Assert.Equal(task.TaskId, claimed.Value!.Task.TaskId);
        var marked = await store.MarkBackupInvocationStartedAsync(
            claimed.Value.Lease,
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
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, unknown.Code);
        return task.TaskId;
    }

    public static async Task<BackupExecutionWorkItem> AcquireAsync(
        IBackupTaskExecutionStore store,
        Guid taskId,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset expiresAtUtc,
        string owner)
    {
        var result = await store.AcquireReconciliationLeaseAsync(
            new AcquireReconciliationLeaseCommand(
                taskId,
                Guid.NewGuid(),
                owner,
                acquiredAtUtc,
                expiresAtUtc));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, result.Code);
        return result.Value!;
    }

    public static ReconciliationCommitCommand InconclusiveCommand(
        DateTimeOffset occurredAtUtc,
        Guid? mutationId = null,
        string errorCode = "reconciliation_evidence_insufficient",
        string errorMessage = "只读证据不足，任务继续等待核对",
        long? sourceLengthBytes = null)
    {
        return new ReconciliationCommitCommand(
            mutationId ?? Guid.NewGuid(),
            BackupReconciliationOutcome.Inconclusive,
            occurredAtUtc,
            ErrorCode: errorCode,
            ErrorMessage: errorMessage,
            SourceLengthBytes: sourceLengthBytes);
    }

    public static LeaseHandle WithAttempt(LeaseHandle lease, Guid backupAttemptId)
    {
        return new LeaseHandle(
            lease.TaskId,
            lease.LeaseToken,
            lease.Purpose,
            lease.Stage,
            backupAttemptId,
            lease.ExpiresAtUtc,
            lease.RowVersion);
    }

    public static LeaseHandle WithToken(LeaseHandle lease, Guid leaseToken)
    {
        return new LeaseHandle(
            lease.TaskId,
            leaseToken,
            lease.Purpose,
            lease.Stage,
            lease.BackupAttemptId,
            lease.ExpiresAtUtc,
            lease.RowVersion);
    }
}
