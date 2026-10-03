using DbBackupManager.Application.Identity;

namespace DbBackupManager.Application.BackupTasks;

public sealed record ScheduledBackupPolicyInput(
    Guid DatabaseId,
    string Name,
    int ScheduleType,
    TimeOnly LocalTime,
    int DaysOfWeek,
    string TimeZoneId,
    int StorageMode,
    Guid? StorageTargetId,
    int? LocalRetentionDays,
    int? RemoteRetentionDays,
    int BackupTimeoutMinutes,
    int VerifyTimeoutMinutes,
    int TransferTimeoutMinutes,
    bool UseCompression,
    bool IsEnabled);

public sealed record ScheduledBackupPolicy(
    Guid Id,
    ScheduledBackupPolicyInput Settings,
    string Version,
    DateTimeOffset? ScheduleEffectiveFromUtc,
    DateTimeOffset? NextSlotUtc,
    DateTime? NextSlotLocal,
    string? NextSlotTimeZoneId);

public sealed record ScheduledBackupDashboard(
    IReadOnlyList<BackupDatabaseChoice> Databases,
    IReadOnlyList<ScheduledBackupPolicy> Policies);

public interface IScheduledBackupPolicyService
{
    Task<BackupManagementResult<ScheduledBackupDashboard>> ListAsync(
        AdminSession actor,
        CancellationToken token = default);

    Task<BackupManagementResult<ScheduledBackupPolicy>> GetAsync(
        AdminSession actor,
        Guid id,
        CancellationToken token = default);

    Task<BackupManagementResult<ScheduledBackupPolicy>> SaveAsync(
        AdminSession actor,
        Guid? id,
        string? version,
        ScheduledBackupPolicyInput input,
        CancellationToken token = default);
}
