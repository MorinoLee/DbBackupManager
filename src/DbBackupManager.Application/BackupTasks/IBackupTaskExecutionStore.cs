using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.BackupTasks;

public interface IBackupTaskExecutionStore
{
    Task<BackupTaskStoreResult<BackupTaskStateModel>> CreateTaskAsync(
        CreateBackupTaskCommand command,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupExecutionWorkItem>> ClaimNextAsync(
        ClaimNextBackupTaskCommand command,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupExecutionWorkItem>> ClaimTaskAsync(
        Guid taskId,
        ClaimNextBackupTaskCommand command,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<LeaseHandle>> MarkBackupInvocationStartedAsync(
        LeaseHandle lease,
        byte[] attemptRowVersion,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<LeaseHandle>> RenewLeaseAsync(
        LeaseHandle lease,
        DateTimeOffset utcNow,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupExecutionWorkItem>> RefreshLeaseWorkItemAsync(
        Guid taskId,
        Guid leaseToken,
        BackupLeasePurpose purpose,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupTaskTransitionModel>> CommitStageAsync(
        LeaseHandle lease,
        BackupStageCommitCommand command,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupTaskStateModel>> RequestCancellationAsync(
        BackupTaskMutationCommand command,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupTaskStateModel>> RetryFailedAsync(
        BackupTaskMutationCommand command,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupTaskStateModel>> RequestReconciliationAsync(
        BackupTaskMutationCommand command,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> FindExpiredExecutionTaskIdsAsync(
        DateTimeOffset utcNow,
        int maximumCount,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupTaskStateModel>> ExpireExecutionLeaseAsync(
        ExpireExecutionLeaseCommand command,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> FindReconciliationCandidateTaskIdsAsync(
        DateTimeOffset utcNow,
        int maximumCount,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupExecutionWorkItem>> AcquireReconciliationLeaseAsync(
        AcquireReconciliationLeaseCommand command,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupTaskStateModel>> CommitReconciliationAsync(
        LeaseHandle lease,
        ReconciliationCommitCommand command,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupTaskStateModel>> ConfirmNeedsAttentionAsync(
        ConfirmNeedsAttentionCommand command,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStateModel?> FindTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);
}
