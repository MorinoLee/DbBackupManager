using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.BackupTasks;

public enum BackupSchedulePolicyResultCode
{
    Created = 1,
    AlreadyExists = 2,
    NotDue = 3,
    // 4 保留空缺：原 NotSchedulable 全仓库无产生点，已由 P5.9.R1 删除。不重新编号其余成员，
    // 避免改变既有取值。
    InvalidTimeZone = 5,
    ConfigurationUnavailable = 6,
    CreateFailed = 7
}

public sealed record SchedulableBackupPolicy(
    Guid PolicyId,
    BackupScheduleType ScheduleType,
    TimeOnly LocalTime,
    BackupWeekdays DaysOfWeek,
    string TimeZoneId,
    DateTimeOffset ScheduleEffectiveFromUtc);

public sealed record BackupSchedulePolicyResult(
    Guid PolicyId,
    BackupSchedulePolicyResultCode Code,
    DateTimeOffset? SlotUtc = null,
    Guid? TaskId = null);

public sealed record BackupScheduleCycleResult(IReadOnlyList<BackupSchedulePolicyResult> Policies);

public interface ISchedulableBackupPolicyReader
{
    Task<IReadOnlyList<SchedulableBackupPolicy>> ListAsync(CancellationToken cancellationToken = default);
}

public interface IBackupTaskScheduler
{
    Task<BackupScheduleCycleResult> RunOnceAsync(CancellationToken cancellationToken = default);
}

public sealed record BackupScheduleWorkerOptions(TimeSpan ScanInterval, TimeSpan FailureBackoff)
{
    public static BackupScheduleWorkerOptions Default { get; } = new(
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60));

    public void Validate()
    {
        if (ScanInterval < TimeSpan.FromSeconds(1)
            || ScanInterval > TimeSpan.FromHours(1)
            || FailureBackoff < ScanInterval
            || FailureBackoff > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException("计划调度扫描间隔或失败退避配置无效。");
        }
    }
}
