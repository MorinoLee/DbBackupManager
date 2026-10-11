using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.TargetSql;

public enum TargetSqlInvocationTerminationState { Active, Terminated, Unknown }
public sealed record TargetSqlInvocationTerminationEvidence(TargetSqlInvocationTerminationState State,
    BackupInvocationBinding OriginalBinding, DateTimeOffset ObservedAtUtc, string ReasonCode);
public interface IPreparedTargetSqlBackupInvocation : IAsyncDisposable
{
    BackupInvocationBinding Binding { get; }
    Task<TargetSqlResult<TargetSqlBackupCompletion>> InvokeOnceAsync(Guid invocationPermitId,
        CancellationToken cancellationToken = default);
}
public interface ITargetSqlInvocationPreparation
{
    Task<TargetSqlResult<IPreparedTargetSqlBackupInvocation>> PrepareAsync(TargetSqlConnectionInput connection,
        TargetSqlBackupRequest request, Guid callerIncarnationId, CancellationToken cancellationToken = default);
}
public interface ITargetSqlInvocationTerminationProbe
{
    Task<TargetSqlInvocationTerminationEvidence> ReadAsync(TargetSqlConnectionInput connection,
        Guid invocationPermitId, BackupInvocationBinding originalBinding, int timeoutSeconds,
        CancellationToken cancellationToken = default);
}
