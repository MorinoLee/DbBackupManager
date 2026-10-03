using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
namespace DbBackupManager.Contracts.Api.V1;

public sealed class SaveManualBackupRequest
{
    [StringLength(16)] public string? Version { get; init; }
    [JsonRequired] public Guid DatabaseId { get; init; }
    [Required, StringLength(200)] public string Name { get; init; } = "";
    [JsonRequired, Range(1, 36500)] public int RetentionDays { get; init; }
    [JsonRequired, Range(1, 1440)] public int BackupTimeoutMinutes { get; init; }
    [JsonRequired, Range(1, 1440)] public int VerifyTimeoutMinutes { get; init; }
    [JsonRequired] public bool UseCompression { get; init; }
    [JsonRequired] public bool IsEnabled { get; init; }
    [Range(1, 3)] public int StorageMode { get; init; } = 1;
    public Guid? StorageTargetId { get; init; }
    [Range(1, 36500)] public int? RemoteRetentionDays { get; init; }
    [Range(1, 1440)] public int TransferTimeoutMinutes { get; init; } = 60;
}
public sealed class BackupTaskCommandRequest
{
    [JsonRequired] public Guid RequestId { get; init; }
}
public sealed record BackupTaskCommandResponse(Guid TaskId);
public sealed class ConfirmBackupTaskFailedRequest
{
    [JsonRequired] public Guid RequestId { get; init; }
    [JsonRequired] public bool EvidenceReviewed { get; init; }
}
public sealed record ManualBackupInputResponse(Guid DatabaseId, string Name, int RetentionDays, int BackupTimeoutMinutes, int VerifyTimeoutMinutes, bool UseCompression, bool IsEnabled,
    int StorageMode, Guid? StorageTargetId, int? RemoteRetentionDays, int TransferTimeoutMinutes);
public sealed record ManualBackupPolicyResponse(Guid Id, ManualBackupInputResponse Settings, string Version);
public sealed record BackupDatabaseChoiceResponse(Guid Id, string Label);
public sealed record BackupTaskSummaryResponse(Guid Id, string PolicyName, string DatabaseName, string Status, string? Stage,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc, string? ErrorCode, bool CancellationRequested, long? FileLength);
public sealed record BackupTaskHistoryResponse(DateTimeOffset AtUtc, string Status, string? Stage, string Reason);
public sealed record BackupAttemptSummaryResponse(int Number, string InvocationStatus, long? Length, DateTimeOffset? VerifiedAtUtc);
public sealed record BackupReconciliationResponse(int AttemptCount, DateTimeOffset? NextAtUtc, string? ReasonCode, bool CanRequest);
public sealed record BackupTaskDetailResponse(BackupTaskSummaryResponse Task, IReadOnlyList<BackupTaskHistoryResponse> History,
    IReadOnlyList<BackupAttemptSummaryResponse> Attempts, string? VerifiedSqlPath, BackupReconciliationResponse Reconciliation,
    IReadOnlyList<BackupFileSummaryResponse> Files);
public sealed record BackupDashboardResponse(IReadOnlyList<BackupDatabaseChoiceResponse> Databases, IReadOnlyList<ManualBackupPolicyResponse> Policies,
    IReadOnlyList<BackupTaskSummaryResponse> Tasks, bool HasMore);

public sealed class SaveScheduledBackupRequest
{
    [StringLength(16)] public string? Version { get; init; }
    [JsonRequired] public Guid DatabaseId { get; init; }
    [Required, StringLength(200)] public string Name { get; init; } = "";
    [JsonRequired, Range(1, 2)] public int ScheduleType { get; init; }
    [Required] public TimeOnly LocalTime { get; init; }
    [Range(0, 127)] public int DaysOfWeek { get; init; }
    [Required, StringLength(150)] public string TimeZoneId { get; init; } = "";
    [Range(1, 3)] public int StorageMode { get; init; } = 1;
    public Guid? StorageTargetId { get; init; }
    [Range(1, 36500)] public int? LocalRetentionDays { get; init; }
    [Range(1, 36500)] public int? RemoteRetentionDays { get; init; }
    [JsonRequired, Range(1, 1440)] public int BackupTimeoutMinutes { get; init; }
    [JsonRequired, Range(1, 1440)] public int VerifyTimeoutMinutes { get; init; }
    [Range(1, 1440)] public int TransferTimeoutMinutes { get; init; } = 60;
    [JsonRequired] public bool UseCompression { get; init; }
    [JsonRequired] public bool IsEnabled { get; init; }
}

public sealed record ScheduledBackupInputResponse(
    Guid DatabaseId, string Name, int ScheduleType, TimeOnly LocalTime, int DaysOfWeek, string TimeZoneId,
    int StorageMode, Guid? StorageTargetId, int? LocalRetentionDays, int? RemoteRetentionDays,
    int BackupTimeoutMinutes, int VerifyTimeoutMinutes, int TransferTimeoutMinutes, bool UseCompression, bool IsEnabled);

public sealed record ScheduledBackupPolicyResponse(
    Guid Id, ScheduledBackupInputResponse Settings, string Version, DateTimeOffset? ScheduleEffectiveFromUtc,
    DateTimeOffset? NextSlotUtc, DateTime? NextSlotLocal, string? NextSlotTimeZoneId);

public sealed record ScheduledBackupDashboardResponse(
    IReadOnlyList<BackupDatabaseChoiceResponse> Databases, IReadOnlyList<ScheduledBackupPolicyResponse> Policies);
