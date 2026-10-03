using DbBackupManager.Application.Identity;

namespace DbBackupManager.Application.BackupTasks;

public sealed record BackupSearch(int Page = 0, string? Search = null, string? Status = null,
    DateTimeOffset? FromUtc = null, DateTimeOffset? UntilUtc = null, bool OldestFirst = false);
public sealed record BackupPage<T>(IReadOnlyList<T> Items, int Page, long TotalCount, int PageSize = 20)
{
    public bool HasMore => ((long)Page + 1) * PageSize < TotalCount;
}
public sealed record BackupFileSummary(Guid TaskId, Guid DatabaseId, string DatabaseName, string InstanceName,
    string PolicyName, string SqlPath, long LengthBytes, DateTimeOffset VerifiedAtUtc, int RetentionDays,
    Guid FileId, string Location, string Protocol, string? TargetDisplayName, string Status,
    DateTimeOffset? DeletedAtUtc, string? ErrorCode, string Path);
public sealed record WorkerPresence(string Status, DateTimeOffset? LastSeenAtUtc, int OfflineAfterSeconds = 90);
public sealed record BackupOverview(DateTimeOffset GeneratedAtUtc, DateTimeOffset RecentFromUtc,
    int ManagedDatabases, int WithoutEnabledPolicy, int NeverVerified, int WithVerifiedFile,
    int PendingTasks, int RunningTasks, int NeedsAttentionTasks, int RecentFailedTasks,
    WorkerPresence Worker, IReadOnlyList<BackupTaskSummary> RecentTasks,
    IReadOnlyList<BackupTaskSummary> RecentFailures, IReadOnlyList<BackupFileSummary> RecentFiles);

public interface IBackupMonitoringService
{
    Task<BackupManagementResult<BackupOverview>> OverviewAsync(AdminSession actor, CancellationToken token = default);
    Task<BackupManagementResult<BackupPage<BackupTaskSummary>>> TasksAsync(AdminSession actor, BackupSearch search, CancellationToken token = default);
    Task<BackupManagementResult<BackupPage<BackupFileSummary>>> FilesAsync(AdminSession actor, BackupSearch search, CancellationToken token = default);
}

public interface IWorkerHeartbeatStore
{
    Task RegisterAsync(Guid processId, CancellationToken token);
    Task PulseAsync(Guid processId, CancellationToken token);
    Task StopAsync(Guid processId, CancellationToken token);
}
