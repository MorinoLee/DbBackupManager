using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
namespace DbBackupManager.Contracts.Api.V1;

public sealed class BackupTaskCommandRequest
{
    [JsonRequired] public Guid RequestId { get; init; }
}
public sealed record BackupTaskCommandResponse(Guid TaskId);
public sealed record BackupDatabaseChoiceResponse(Guid Id, string Label);
public sealed record BackupTaskSummaryResponse(Guid Id, string PolicyName, string DatabaseName, string Status, string? Stage,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc, string? ErrorCode, bool CancellationRequested, long? FileLength)
{
    public string BackupType { get; init; } = "Full";

    public Guid? PlanId { get; init; }

    public Guid? PlanVersionId { get; init; }

    public int? PlanVersionNumber { get; init; }

    public string SqlStatus { get; init; } = "Unknown";

    public string FileVerifyStatus { get; init; } = "Unknown";

    public string TransferStatus { get; init; } = "Unknown";

    public string DependencyStatus { get; init; } = "Unknown";
}
public sealed record BackupTaskHistoryResponse(DateTimeOffset AtUtc, string Status, string? Stage, string Reason);
public sealed record BackupAttemptSummaryResponse(int Number, string InvocationStatus, long? Length, DateTimeOffset? VerifiedAtUtc);
public sealed record BackupReconciliationResponse(int AttemptCount, DateTimeOffset? NextAtUtc, string? ReasonCode, bool CanRequest);
public sealed record BackupTaskDetailResponse(BackupTaskSummaryResponse Task, IReadOnlyList<BackupTaskHistoryResponse> History,
    IReadOnlyList<BackupAttemptSummaryResponse> Attempts, string? VerifiedSqlPath, BackupReconciliationResponse Reconciliation,
    IReadOnlyList<BackupFileSummaryResponse> Files);
public sealed record BackupDashboardResponse(IReadOnlyList<BackupDatabaseChoiceResponse> Databases,
    IReadOnlyList<BackupTaskSummaryResponse> Tasks, bool HasMore);

public sealed class BackupPlanScheduleRequest
{
    [JsonRequired, Range(1, 2)] public int ScheduleType { get; init; }
    [Required] public TimeOnly LocalTime { get; init; }
    [Range(0, 127)] public int DaysOfWeek { get; init; }
}

public sealed class SaveBackupPlanRequest
{
    [StringLength(16)] public string? Version { get; init; }
    [JsonRequired] public Guid DatabaseId { get; init; }
    [Required, StringLength(200)] public string Name { get; init; } = "";
    [JsonRequired, Range(1, 3)] public int Mode { get; init; }
    [Required] public BackupPlanScheduleRequest FullSchedule { get; init; } = new();
    public BackupPlanScheduleRequest? DifferentialSchedule { get; init; }
    [Required, StringLength(150)] public string TimeZoneId { get; init; } = "";
    [JsonRequired, Range(1, 3)] public int StorageMode { get; init; }
    public Guid? StorageTargetId { get; init; }
    [Range(1, 36500)] public int? LocalRecoveryWindowDays { get; init; }
    [Range(1, 36500)] public int? RemoteRecoveryWindowDays { get; init; }
    public bool? UseChecksum { get; init; }
    [JsonRequired] public bool UseCompression { get; init; }
    [JsonRequired, Range(1, 1440)] public int BackupTimeoutMinutes { get; init; }
    [JsonRequired, Range(1, 1440)] public int VerifyTimeoutMinutes { get; init; }
    [JsonRequired, Range(1, 1440)] public int TransferTimeoutMinutes { get; init; }
    [JsonRequired] public bool IsPaused { get; init; }
}

public sealed record BackupPlanScheduleResponse(int ScheduleType, TimeOnly LocalTime, int DaysOfWeek);

public sealed record BackupPlanResponse(
    Guid Id, Guid DatabaseId, string Name, bool IsPaused, string Version, Guid CurrentVersionId, int VersionNumber,
    int Mode, BackupPlanScheduleResponse FullSchedule, BackupPlanScheduleResponse? DifferentialSchedule,
    string TimeZoneId, int StorageMode, Guid? StorageTargetId, int? LocalRecoveryWindowDays, int? RemoteRecoveryWindowDays,
    bool UseChecksum, bool UseCompression, int BackupTimeoutMinutes, int VerifyTimeoutMinutes, int TransferTimeoutMinutes,
    DateTimeOffset EffectiveFromUtc, IReadOnlyList<string> Notices);

public sealed record BackupLocationRecoveryResponse(
    string Location, Guid? StorageTargetId, int? WindowDays, string Conclusion, string Baseline,
    Guid? ManagedBaselineBackupSetId, bool Unknown, bool RemoteUnrecoverable,
    IReadOnlyList<string> GapReasonCodes, IReadOnlyList<string> UnknownReasonCodes);

public sealed record BackupRecoverySummaryResponse(
    Guid DatabaseId, bool HasCurrentPlan, IReadOnlyList<BackupLocationRecoveryResponse> Locations);
