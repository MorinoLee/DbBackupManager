using System.ComponentModel.DataAnnotations;

namespace DbBackupManager.Contracts.Api.V1;

public sealed class BackupSearchRequest
{
    [Range(0, 10000)] public int Page { get; init; }
    [StringLength(200)] public string? Search { get; init; }
    [StringLength(30)] public string? Status { get; init; }
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? UntilUtc { get; init; }
    public bool OldestFirst { get; init; }
}
public sealed record BackupFileSummaryResponse(Guid TaskId, Guid DatabaseId, string DatabaseName, string InstanceName,
    string PolicyName, string SqlPath, long LengthBytes, DateTimeOffset VerifiedAtUtc, int RetentionDays,
    Guid FileId, string Location, string Protocol, string? TargetDisplayName, string Status,
    DateTimeOffset? DeletedAtUtc, string? ErrorCode, string Path);
public sealed record WorkerPresenceResponse(string Status, DateTimeOffset? LastSeenAtUtc, int OfflineAfterSeconds);
public sealed record BackupTaskPageResponse(IReadOnlyList<BackupTaskSummaryResponse> Items, int Page, long TotalCount, int PageSize, bool HasMore);
public sealed record BackupFilePageResponse(IReadOnlyList<BackupFileSummaryResponse> Items, int Page, long TotalCount, int PageSize, bool HasMore);
public sealed record BackupOverviewResponse(DateTimeOffset GeneratedAtUtc, DateTimeOffset RecentFromUtc,
    int ManagedDatabases, int WithoutEnabledPolicy, int NeverVerified, int WithVerifiedFile,
    int PendingTasks, int RunningTasks, int NeedsAttentionTasks, int RecentFailedTasks,
    WorkerPresenceResponse Worker, IReadOnlyList<BackupTaskSummaryResponse> RecentTasks,
    IReadOnlyList<BackupTaskSummaryResponse> RecentFailures, IReadOnlyList<BackupFileSummaryResponse> RecentFiles);
