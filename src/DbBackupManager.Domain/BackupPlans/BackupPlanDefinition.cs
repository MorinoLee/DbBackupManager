using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.BackupPlans;

public readonly record struct RecurringBackupSchedule
{
    private const BackupWeekdays AllWeekdays =
        BackupWeekdays.Monday
        | BackupWeekdays.Tuesday
        | BackupWeekdays.Wednesday
        | BackupWeekdays.Thursday
        | BackupWeekdays.Friday
        | BackupWeekdays.Saturday
        | BackupWeekdays.Sunday;

    public RecurringBackupSchedule(BackupScheduleType scheduleType, TimeOnly localTime, BackupWeekdays daysOfWeek)
    {
        ScheduleType = scheduleType;
        LocalTime = localTime;
        DaysOfWeek = daysOfWeek;
        Validate();
    }

    public BackupScheduleType ScheduleType { get; }

    public TimeOnly LocalTime { get; }

    public BackupWeekdays DaysOfWeek { get; }

    public void Validate()
    {
        ConfigurationValues.RequireDefined(ScheduleType, nameof(ScheduleType));
        if ((DaysOfWeek & ~AllWeekdays) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DaysOfWeek), "星期组合包含无效位。");
        }

        if (ScheduleType == BackupScheduleType.Daily && DaysOfWeek != BackupWeekdays.None)
        {
            throw new ArgumentException("Daily 调度不能指定星期。", nameof(DaysOfWeek));
        }

        if (ScheduleType == BackupScheduleType.Weekly && DaysOfWeek == BackupWeekdays.None)
        {
            throw new ArgumentException("Weekly 调度至少选择一个星期。", nameof(DaysOfWeek));
        }
    }
}

public readonly record struct LogBackupInterval
{
    public LogBackupInterval(int intervalMinutes)
    {
        IntervalMinutes = intervalMinutes;
        Validate();
    }

    public int IntervalMinutes { get; }

    public void Validate()
    {
        ConfigurationValues.RequirePositive(IntervalMinutes, 1_440, nameof(IntervalMinutes));
    }
}

public readonly record struct LogBackupSchedule
{
    public LogBackupSchedule(int intervalMinutes, DateTimeOffset anchorUtc)
    {
        IntervalMinutes = intervalMinutes;
        AnchorUtc = anchorUtc;
        Validate();
    }

    public int IntervalMinutes { get; }

    public DateTimeOffset AnchorUtc { get; }

    public void Validate()
    {
        ConfigurationValues.RequirePositive(IntervalMinutes, 1_440, nameof(IntervalMinutes));
        ConfigurationValues.RequireUtc(AnchorUtc, nameof(AnchorUtc));
    }
}

public sealed record BackupPlanDefinition
{
    public BackupPlanDefinition(
        BackupPlanMode mode,
        RecurringBackupSchedule fullSchedule,
        RecurringBackupSchedule? differentialSchedule,
        LogBackupInterval? logInterval,
        string timeZoneId,
        BackupStorageMode storageMode,
        Guid? storageTargetId,
        int? localRecoveryWindowDays,
        int? remoteRecoveryWindowDays,
        bool useChecksum,
        bool useCompression,
        int backupTimeoutMinutes,
        int verifyTimeoutMinutes,
        int transferTimeoutMinutes)
    {
        ConfigurationValues.RequireDefined(mode, nameof(mode));
        fullSchedule.Validate();
        differentialSchedule?.Validate();
        logInterval?.Validate();
        RequireSchedule(mode, BackupType.Differential, differentialSchedule.HasValue, "差异备份");
        RequireSchedule(mode, BackupType.Log, logInterval.HasValue, "日志备份");
        ConfigurationValues.RequireDefined(storageMode, nameof(storageMode));
        ValidateWindows(storageMode, storageTargetId, localRecoveryWindowDays, remoteRecoveryWindowDays);

        Mode = mode;
        FullSchedule = fullSchedule;
        DifferentialSchedule = differentialSchedule;
        LogInterval = logInterval;
        TimeZoneId = ConfigurationValues.RequireTimeZoneId(timeZoneId, nameof(timeZoneId));
        StorageMode = storageMode;
        StorageTargetId = storageTargetId;
        LocalRecoveryWindowDays = localRecoveryWindowDays;
        RemoteRecoveryWindowDays = remoteRecoveryWindowDays;
        UseChecksum = useChecksum;
        UseCompression = useCompression;
        BackupTimeoutMinutes = ConfigurationValues.RequirePositive(backupTimeoutMinutes, 1_440, nameof(backupTimeoutMinutes));
        VerifyTimeoutMinutes = ConfigurationValues.RequirePositive(verifyTimeoutMinutes, 1_440, nameof(verifyTimeoutMinutes));
        TransferTimeoutMinutes = ConfigurationValues.RequirePositive(transferTimeoutMinutes, 1_440, nameof(transferTimeoutMinutes));
    }

    public BackupPlanMode Mode { get; }

    public RecurringBackupSchedule FullSchedule { get; }

    public RecurringBackupSchedule? DifferentialSchedule { get; }

    public LogBackupInterval? LogInterval { get; }

    public string TimeZoneId { get; }

    public BackupStorageMode StorageMode { get; }

    public Guid? StorageTargetId { get; }

    public int? LocalRecoveryWindowDays { get; }

    public int? RemoteRecoveryWindowDays { get; }

    public bool UseChecksum { get; }

    public bool UseCompression { get; }

    public int BackupTimeoutMinutes { get; }

    public int VerifyTimeoutMinutes { get; }

    public int TransferTimeoutMinutes { get; }

    private static void RequireSchedule(BackupPlanMode mode, BackupType backupType, bool isPresent, string label)
    {
        var expected = BackupPlanRules.Includes(mode, backupType);
        if (expected == isPresent)
        {
            return;
        }

        throw new ArgumentException(
            expected ? $"该计划模式必须配置{label}时间。" : $"该计划模式不能配置{label}时间。",
            backupType == BackupType.Differential ? nameof(DifferentialSchedule) : nameof(LogInterval));
    }

    private static void ValidateWindows(
        BackupStorageMode storageMode,
        Guid? storageTargetId,
        int? localRecoveryWindowDays,
        int? remoteRecoveryWindowDays)
    {
        static void RequireWindow(int? value, string parameterName)
        {
            if (value is null || value is < 1 or > 36_500)
            {
                throw new ArgumentOutOfRangeException(parameterName, "恢复窗口必须介于 1 与 36500 天之间。");
            }
        }

        switch (storageMode)
        {
            case BackupStorageMode.LocalOnly:
                if (storageTargetId is not null || remoteRecoveryWindowDays is not null)
                {
                    throw new ArgumentException("仅本地模式不能配置远程存储目标或远端恢复窗口。", nameof(storageMode));
                }

                RequireWindow(localRecoveryWindowDays, nameof(localRecoveryWindowDays));
                break;
            case BackupStorageMode.LocalAndRemote:
                ConfigurationValues.RequireId(storageTargetId ?? Guid.Empty, nameof(storageTargetId));
                RequireWindow(localRecoveryWindowDays, nameof(localRecoveryWindowDays));
                RequireWindow(remoteRecoveryWindowDays, nameof(remoteRecoveryWindowDays));
                break;
            case BackupStorageMode.RemoteOnly:
                ConfigurationValues.RequireId(storageTargetId ?? Guid.Empty, nameof(storageTargetId));
                if (localRecoveryWindowDays is not null)
                {
                    throw new ArgumentException("仅远程模式不能配置本地恢复窗口。", nameof(localRecoveryWindowDays));
                }

                RequireWindow(remoteRecoveryWindowDays, nameof(remoteRecoveryWindowDays));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(storageMode), storageMode, "枚举值无效。");
        }
    }
}
