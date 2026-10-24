namespace DbBackupManager.Application.BackupLogs;

/// <summary>只提交固定 Prepared；读取和提交均不访问目标 SQL 或文件。</summary>
public interface ILogAssessmentCommitStore
{
    Task<LogAssessmentReadResult> ReadAsync(Guid databaseId, Guid mutationId,
        CancellationToken cancellationToken = default);

    Task<LogAssessmentCommitResult> CommitAsync(PreparedLogAssessment prepared,
        CancellationToken cancellationToken = default);
}

/// <summary>后续预算执行卡的端口合同；13-C 不实现成功预约、占用或释放。</summary>
public enum LogBudgetReservationCode { StageNotOpen, Unavailable, Conflict }
public sealed record LogBudgetReservationRequest(
    Guid DatabaseId,
    Guid MutationId,
    Guid ConfigurationVersion,
    Guid ObservationVersion,
    Guid? TaskId,
    Guid? AttemptId,
    Guid? PermitId,
    long? RequestedBytes,
    bool HasUnknownArtifacts);
public sealed record LogBudgetReservationResult(
    LogBudgetReservationCode Code,
    string ReasonCode,
    long? ReservedFileCount = null,
    long? ReservedBytes = null);

public interface ILogBudgetReservationStore
{
    Task<LogBudgetReservationResult> TryReserveAsync(LogBudgetReservationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableLogBudgetReservationStore : ILogBudgetReservationStore
{
    public Task<LogBudgetReservationResult> TryReserveAsync(LogBudgetReservationRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LogBudgetReservationResult(LogBudgetReservationCode.StageNotOpen,
            "log.stage_not_open"));
}
