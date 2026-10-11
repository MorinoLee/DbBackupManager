using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.BackupTasks;

public sealed record BackupPlanExecutionSession(BackupExecutionWorkItem Work,
    IReadOnlyList<FrozenBackupExecutionOperation> Operations, BackupInvocationAuthorizationModel? Authorization);
public sealed record QualifiedManagedFull(Guid BackupSetId, ManagedFullBackupCandidate Candidate,
    BackupFileEndpointInput Endpoint, string Path, BackupContentEvidence Content);

/// <summary>每次调用只在短事务内读写；返回的会话不持有 Context 或事务。</summary>
public interface IBackupPlanExecutionStore
{
    Task<BackupExecutionContractResult<BackupPlanExecutionSession>> OpenAsync(Guid taskId, Guid requestMutationId,
        bool reconciliationOnly, Guid leaseToken, string owner, DateTimeOffset now, DateTimeOffset expires,
        CancellationToken cancellationToken = default);
    Task<BackupPlanExecutionSession?> RefreshAsync(Guid taskId, Guid leaseToken, CancellationToken cancellationToken = default);
    Task<BackupExecutionContractCode> RenewAsync(Guid taskId, Guid leaseToken, DateTimeOffset expires,
        CancellationToken cancellationToken = default);
    Task<QualifiedManagedFull?> FindQualifiedFullAsync(Guid databaseId, Guid backupSetGuid,
        CancellationToken cancellationToken = default);
    Task<BackupExecutionContractCode> ApplyAsync(LeaseHandle lease, ReserveBackupExecutionOperation identity,
        IVerifiedBackupFileHandle? protectedFile = null, IVerifiedBackupFileHandle? protectedDependency = null,
        CancellationToken cancellationToken = default);
    Task<BackupExecutionContractCode> SuspendAsync(LeaseHandle lease, Guid mutationId, string reason,
        CancellationToken cancellationToken = default);
}

public sealed record ReconcileBackupPlanTaskRequest(Guid TaskId, Guid RequestMutationId);
public interface IBackupPlanTaskReconciler
{
    Task<BackupExecutionContractCode> ReconcilePlanTaskOnceAsync(ReconcileBackupPlanTaskRequest request,
        CancellationToken cancellationToken = default);
}
