using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupTaskExecutionStore : IBackupTaskExecutionStore
{
    private readonly BackupTaskCommandStore _commands;
    private readonly BackupTaskLeaseStore _leases;
    private readonly BackupTaskStageStore _stages;
    private readonly BackupTaskReconciliationStore _reconciliation;

    public BackupTaskExecutionStore(IDbContextFactory<PlatformDbContext> contextFactory)
    {
        var persistence = new BackupTaskPersistence(contextFactory);
        _commands = new BackupTaskCommandStore(persistence);
        _leases = new BackupTaskLeaseStore(persistence);
        _stages = new BackupTaskStageStore(persistence);
        _reconciliation = new BackupTaskReconciliationStore(persistence);
    }

    public Task<BackupTaskStoreResult<BackupTaskStateModel>> CreateTaskAsync(
        CreateBackupTaskCommand command,
        CancellationToken cancellationToken = default) =>
        _commands.CreateTaskAsync(command, cancellationToken);

    public Task<BackupTaskStoreResult<BackupExecutionWorkItem>> ClaimNextAsync(
        ClaimNextBackupTaskCommand command,
        CancellationToken cancellationToken = default) =>
        _leases.ClaimNextAsync(command, cancellationToken);

    public Task<BackupTaskStoreResult<BackupExecutionWorkItem>> ClaimTaskAsync(
        Guid taskId,
        ClaimNextBackupTaskCommand command,
        CancellationToken cancellationToken = default) =>
        _leases.ClaimTaskAsync(taskId, command, cancellationToken);

    public Task<BackupTaskStoreResult<LeaseHandle>> MarkBackupInvocationStartedAsync(
        LeaseHandle lease,
        byte[] attemptRowVersion,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default) =>
        _stages.MarkBackupInvocationStartedAsync(lease, attemptRowVersion, startedAtUtc, cancellationToken);

    public Task<BackupTaskStoreResult<LeaseHandle>> RenewLeaseAsync(
        LeaseHandle lease,
        DateTimeOffset utcNow,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default) =>
        _leases.RenewLeaseAsync(lease, utcNow, expiresAtUtc, cancellationToken);

    public Task<BackupTaskStoreResult<BackupExecutionWorkItem>> RefreshLeaseWorkItemAsync(
        Guid taskId,
        Guid leaseToken,
        BackupLeasePurpose purpose,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        _leases.RefreshLeaseWorkItemAsync(taskId, leaseToken, purpose, utcNow, cancellationToken);

    public Task<BackupTaskStoreResult<BackupTaskTransitionModel>> CommitStageAsync(
        LeaseHandle lease,
        BackupStageCommitCommand command,
        CancellationToken cancellationToken = default) =>
        _stages.CommitStageAsync(lease, command, cancellationToken);

    public Task<BackupTaskStoreResult<BackupTaskStateModel>> RequestCancellationAsync(
        BackupTaskMutationCommand command,
        CancellationToken cancellationToken = default) =>
        _commands.RequestCancellationAsync(command, cancellationToken);

    public Task<BackupTaskStoreResult<BackupTaskStateModel>> RetryFailedAsync(
        BackupTaskMutationCommand command,
        CancellationToken cancellationToken = default) =>
        _commands.RetryFailedAsync(command, cancellationToken);

    public Task<BackupTaskStoreResult<BackupTaskStateModel>> RequestReconciliationAsync(
        BackupTaskMutationCommand command,
        CancellationToken cancellationToken = default) =>
        _commands.RequestReconciliationAsync(command, cancellationToken);

    public Task<IReadOnlyList<Guid>> FindExpiredExecutionTaskIdsAsync(
        DateTimeOffset utcNow,
        int maximumCount,
        CancellationToken cancellationToken = default) =>
        _leases.FindExpiredExecutionTaskIdsAsync(utcNow, maximumCount, cancellationToken);

    public Task<BackupTaskStoreResult<BackupTaskStateModel>> ExpireExecutionLeaseAsync(
        ExpireExecutionLeaseCommand command,
        CancellationToken cancellationToken = default) =>
        _leases.ExpireExecutionLeaseAsync(command, cancellationToken);

    public Task<IReadOnlyList<Guid>> FindReconciliationCandidateTaskIdsAsync(
        DateTimeOffset utcNow,
        int maximumCount,
        CancellationToken cancellationToken = default) =>
        _reconciliation.FindReconciliationCandidateTaskIdsAsync(utcNow, maximumCount, cancellationToken);

    public Task<BackupTaskStoreResult<BackupExecutionWorkItem>> AcquireReconciliationLeaseAsync(
        AcquireReconciliationLeaseCommand command,
        CancellationToken cancellationToken = default) =>
        _reconciliation.AcquireReconciliationLeaseAsync(command, cancellationToken);

    public Task<BackupTaskStoreResult<BackupTaskStateModel>> CommitReconciliationAsync(
        LeaseHandle lease,
        ReconciliationCommitCommand command,
        CancellationToken cancellationToken = default) =>
        _reconciliation.CommitReconciliationAsync(lease, command, cancellationToken);

    public Task<BackupTaskStoreResult<BackupTaskStateModel>> ConfirmNeedsAttentionAsync(
        ConfirmNeedsAttentionCommand command,
        CancellationToken cancellationToken = default) =>
        _reconciliation.ConfirmNeedsAttentionAsync(command, cancellationToken);

    public Task<BackupTaskStateModel?> FindTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        _commands.FindTaskAsync(taskId, cancellationToken);
}
