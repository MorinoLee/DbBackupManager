using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.BackupPlans;

public enum BackupPlanCatalogCode
{
    Succeeded,
    AuthenticationRequired,
    Invalid,
    NotFound,
    Conflict,
    Unavailable,
    AlreadyExists,
    VersionConflict,
    LogUnsupported
}

public sealed record BackupPlanCatalogResult<T>(
    BackupPlanCatalogCode Code,
    T? Value = default,
    Guid? ExistingPlanId = null);

public sealed record BackupPlanScheduleInput(int ScheduleType, TimeOnly LocalTime, int DaysOfWeek);

public sealed record BackupPlanInput(
    Guid DatabaseId,
    string Name,
    int Mode,
    BackupPlanScheduleInput FullSchedule,
    BackupPlanScheduleInput? DifferentialSchedule,
    string TimeZoneId,
    int StorageMode,
    Guid? StorageTargetId,
    int? LocalRecoveryWindowDays,
    int? RemoteRecoveryWindowDays,
    bool? UseChecksum,
    bool UseCompression,
    int BackupTimeoutMinutes,
    int VerifyTimeoutMinutes,
    int TransferTimeoutMinutes,
    bool IsPaused);

public sealed record BackupPlanView(
    Guid Id,
    Guid DatabaseId,
    string Name,
    bool IsPaused,
    string Version,
    Guid CurrentVersionId,
    int VersionNumber,
    BackupPlanMode Mode,
    RecurringBackupSchedule FullSchedule,
    RecurringBackupSchedule? DifferentialSchedule,
    string TimeZoneId,
    BackupStorageMode StorageMode,
    Guid? StorageTargetId,
    int? LocalRecoveryWindowDays,
    int? RemoteRecoveryWindowDays,
    bool UseChecksum,
    bool UseCompression,
    int BackupTimeoutMinutes,
    int VerifyTimeoutMinutes,
    int TransferTimeoutMinutes,
    DateTimeOffset EffectiveFromUtc,
    IReadOnlyList<string> Notices);

public sealed record BackupLocationRecovery(
    BackupFileLocation Location,
    Guid? StorageTargetId,
    int? WindowDays,
    LocationRecoveryConclusion Conclusion,
    string Baseline,
    Guid? ManagedBaselineBackupSetId,
    bool Unknown,
    bool RemoteUnrecoverable,
    IReadOnlyList<string> GapReasonCodes,
    IReadOnlyList<string> UnknownReasonCodes);

public sealed record BackupRecoverySummary(
    Guid DatabaseId,
    bool HasCurrentPlan,
    IReadOnlyList<BackupLocationRecovery> Locations,
    string? BaselineNotice = null);

public sealed record BackupPlanListItem(
    Guid Id,
    Guid DatabaseId,
    string Name,
    bool IsPaused,
    string Version,
    int VersionNumber,
    int Mode,
    int StorageMode,
    bool UseChecksum);

public interface IBackupPlanCatalogService
{
    Task<BackupPlanCatalogResult<IReadOnlyList<BackupPlanListItem>>> ListAsync(
        AdminSession actor,
        CancellationToken cancellationToken = default);

    Task<BackupPlanCatalogResult<BackupPlanView>> GetByDatabaseAsync(
        AdminSession actor,
        Guid databaseId,
        CancellationToken cancellationToken = default);

    Task<BackupPlanCatalogResult<BackupPlanView>> GetAsync(
        AdminSession actor,
        Guid planId,
        CancellationToken cancellationToken = default);

    Task<BackupPlanCatalogResult<BackupPlanView>> CreateAsync(
        AdminSession actor,
        BackupPlanInput input,
        CancellationToken cancellationToken = default);

    Task<BackupPlanCatalogResult<BackupPlanView>> UpdateAsync(
        AdminSession actor,
        Guid planId,
        string? version,
        BackupPlanInput input,
        CancellationToken cancellationToken = default);

    Task<BackupPlanCatalogResult<BackupRecoverySummary>> RecoveryAsync(
        AdminSession actor,
        Guid databaseId,
        CancellationToken cancellationToken = default);
}
