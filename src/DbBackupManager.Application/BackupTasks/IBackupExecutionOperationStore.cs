using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.BackupTasks;

public enum BackupExecutionContractCode { Succeeded, AlreadyApplied, Conflict, NotFound, LeaseLost, DatabaseBlocked, StageNotOpen }
public sealed record BackupExecutionContractResult<T>(BackupExecutionContractCode Code, T? Value = null) where T : class;
public sealed record ReserveBackupExecutionOperation(Guid MutationId, Guid TaskId, Guid AttemptId,
    BackupExecutionOperationKind Kind, int Sequence, Guid IntendedBackupSetId, DateTimeOffset ObservedAtUtc);
public sealed record BackupExecutionObservationInput(int EntryNumber, BackupExecutionObservationSource Source,
    BackupSetEvidenceKind Kind, BackupExecutionFacts Facts);
public sealed record FrozenBackupExecutionOperation(ReserveBackupExecutionOperation Identity,
    BackupExecutionOperationState State, BackupExecutionFacts Facts, IReadOnlyList<BackupExecutionObservationInput> Observations);
public sealed record FreezeBackupExecutionOperation(ReserveBackupExecutionOperation Identity,
    BackupExecutionFacts Facts, IReadOnlyList<BackupExecutionObservationInput> Observations);

public interface IBackupExecutionOperationStore
{
    Task<BackupExecutionContractResult<FrozenBackupExecutionOperation>> ReserveAsync(
        LeaseHandle lease, ReserveBackupExecutionOperation command, CancellationToken cancellationToken = default);
    /// <summary>Admission 的身份/基线绑定与冻结在同一短事务保存；新观察使用新的 MutationId。</summary>
    Task<BackupExecutionContractResult<FrozenBackupExecutionOperation>> FreezeAsync(
        LeaseHandle lease, FreezeBackupExecutionOperation command, CancellationToken cancellationToken = default);
    Task<BackupExecutionContractResult<FrozenBackupExecutionOperation>> ReadAsync(
        ReserveBackupExecutionOperation identity, CancellationToken cancellationToken = default);
    /// <summary>计划阶段应用在后续执行卡开放；当前返回 StageNotOpen，不修改回执或阶段。</summary>
    Task<BackupExecutionContractResult<FrozenBackupExecutionOperation>> ApplyFrozenAsync(
        LeaseHandle lease, ReserveBackupExecutionOperation identity, CancellationToken cancellationToken = default);
    Task<BackupExecutionContractResult<FrozenBackupExecutionOperation>> AbandonAsync(
        LeaseHandle lease, ReserveBackupExecutionOperation identity, CancellationToken cancellationToken = default);
}

public sealed record AuthorizeBackupInvocation(Guid PermitId, Guid MutationId, Guid SqlOperationId,
    Guid DatabaseId, BackupInvocationBinding Binding, DateTimeOffset GrantedAtUtc, byte[] AttemptRowVersion);
public sealed record BackupInvocationAuthorizationModel(Guid PermitId, Guid TaskId, Guid AttemptId,
    Guid DatabaseId, Guid MutationId, Guid SqlOperationId, DateTimeOffset GrantedAtUtc,
    BackupInvocationBinding Binding, DateTimeOffset? TerminalObservedAtUtc,
    BackupInvocationTerminationKind? TerminationKind, Guid? TerminationEvidenceId, Guid? TerminationMutationId,
    LeaseHandle? Lease = null);
public sealed record TerminateBackupInvocation(Guid PermitId, Guid MutationId, Guid EvidenceId,
    BackupInvocationTerminationKind Kind, DateTimeOffset ObservedAtUtc,
    bool OriginalCallTerminated, bool OriginalCallerCannotInvoke);
public interface IBackupInvocationAuthorizationStore
{
    Task<BackupExecutionContractResult<BackupInvocationAuthorizationModel>> AuthorizeAsync(
        LeaseHandle lease, AuthorizeBackupInvocation command, CancellationToken cancellationToken = default);
    Task<BackupExecutionContractResult<BackupInvocationAuthorizationModel>> ReadAsync(
        Guid permitId, CancellationToken cancellationToken = default);
    Task<BackupExecutionContractResult<BackupInvocationAuthorizationModel>> RecordTerminationAsync(
        LeaseHandle lease, TerminateBackupInvocation command, CancellationToken cancellationToken = default);
}

/// <summary>8-5a 仅提供关闭的入口，不领取、不创建 Attempt、不访问外部端口。</summary>
public sealed record RunBackupPlanTaskRequest(Guid TaskId, Guid RequestMutationId);
public interface IBackupPlanTaskRunner
{
    Task<BackupExecutionContractCode> RunTaskOnceAsync(RunBackupPlanTaskRequest request, CancellationToken cancellationToken = default);
}

public sealed class BackupPlanTaskRunner : IBackupPlanTaskRunner
{
    public Task<BackupExecutionContractCode> RunTaskOnceAsync(RunBackupPlanTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TaskId == Guid.Empty || request.RequestMutationId == Guid.Empty)
            throw new ArgumentException("任务与请求幂等标识不能为空。", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BackupExecutionContractCode.StageNotOpen);
    }
}
