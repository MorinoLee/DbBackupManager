using DbBackupManager.Application.Identity;
namespace DbBackupManager.Application.BackupTasks;

public enum BackupManagementCode { Succeeded, AuthenticationRequired, Invalid, NotFound, Conflict, Unavailable }
public sealed record BackupManagementResult<T>(BackupManagementCode Code, T? Value = default);
public sealed record ManualBackupInput(
    Guid DatabaseId, string Name, int RetentionDays, int BackupTimeoutMinutes, int VerifyTimeoutMinutes,
    bool UseCompression, bool IsEnabled, int StorageMode = 1, Guid? StorageTargetId = null,
    int? RemoteRetentionDays = null, int TransferTimeoutMinutes = 60);
public sealed record ManualBackupPolicy(Guid Id, ManualBackupInput Settings, string Version);
public sealed record BackupDatabaseChoice(Guid Id, string Label);
public sealed record BackupTaskSummary(Guid Id, string PolicyName, string DatabaseName, string Status, string? Stage,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc, string? ErrorCode, bool CancellationRequested, long? FileLength);
public sealed record BackupTaskHistory(DateTimeOffset AtUtc, string Status, string? Stage, string Reason);
public sealed record BackupAttemptSummary(int Number, string InvocationStatus, long? Length, DateTimeOffset? VerifiedAtUtc);
public sealed record BackupReconciliationSummary(int AttemptCount, DateTimeOffset? NextAtUtc, string? ReasonCode, bool CanRequest);
public sealed record BackupTaskDetail(BackupTaskSummary Task, IReadOnlyList<BackupTaskHistory> History,
    IReadOnlyList<BackupAttemptSummary> Attempts, string? VerifiedSqlPath, BackupReconciliationSummary Reconciliation,
    IReadOnlyList<BackupFileSummary> Files);
public sealed record BackupDashboard(IReadOnlyList<BackupDatabaseChoice> Databases, IReadOnlyList<ManualBackupPolicy> Policies,
    IReadOnlyList<BackupTaskSummary> Tasks, bool HasMore);
public interface IBackupManagementService
{
    Task<BackupManagementResult<BackupDashboard>> ListAsync(AdminSession actor, int page = 0, CancellationToken token = default);
    Task<BackupManagementResult<ManualBackupPolicy>> SavePolicyAsync(AdminSession actor, Guid? id, string? version, ManualBackupInput input, CancellationToken token = default);
    Task<BackupManagementResult<Guid>> StartAsync(AdminSession actor, Guid policyId, Guid requestId, CancellationToken token = default);
    Task<BackupManagementResult<Guid>> CancelAsync(AdminSession actor, Guid taskId, Guid requestId, CancellationToken token = default);
    Task<BackupManagementResult<Guid>> RetryAsync(AdminSession actor, Guid taskId, Guid requestId, CancellationToken token = default);
    Task<BackupManagementResult<Guid>> RequestReconciliationAsync(AdminSession actor, Guid taskId, Guid requestId, CancellationToken token = default);
    Task<BackupManagementResult<Guid>> ConfirmFailedAsync(AdminSession actor, Guid taskId, Guid requestId, bool evidenceReviewed, CancellationToken token = default);
    Task<BackupManagementResult<BackupTaskDetail>> DetailAsync(AdminSession actor, Guid taskId, CancellationToken token = default);
}
