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

    /// <summary>结束 RemoteOnly 的 Cleanup。保留暂存时不要求文件已删除，并记下具体保护原因；释放时必须已有清理证据。</summary>
    Task<BackupExecutionContractCode> CompleteRemoteCleanupAsync(LeaseHandle lease, bool retained,
        string? outcomeReason = null, CancellationToken cancellationToken = default);

    /// <summary>暂存已经确认解开、任务停在 NeedsAttention/Cleanup 且没有活动租约时，结束该任务。</summary>
    Task<BackupExecutionContractCode> CloseDeferredRemoteCleanupAsync(Guid taskId,
        CancellationToken cancellationToken = default);

    /// <summary>结束本库已经留下清理证据、却停在 NeedsAttention/Cleanup 的 RemoteOnly 任务。</summary>
    Task<BackupExecutionContractCode> CloseReadyRemoteCleanupsAsync(Guid databaseId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// RemoteOnly 暂存清理的只读核对：暂存记录已经解开时补齐终态；暂存仍在时只释放核对租约、
    /// 保留原 Attempt 和文件，由执行路径按原阶段重试。核对本身不删除任何文件。
    /// </summary>
    Task<BackupExecutionContractCode> ReleaseDeferredCleanupForExecutionAsync(LeaseHandle lease,
        CancellationToken cancellationToken = default);
}

public sealed record ReconcileBackupPlanTaskRequest(Guid TaskId, Guid RequestMutationId);
public interface IBackupPlanTaskReconciler
{
    Task<BackupExecutionContractCode> ReconcilePlanTaskOnceAsync(ReconcileBackupPlanTaskRequest request,
        CancellationToken cancellationToken = default);
}
