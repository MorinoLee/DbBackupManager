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

public readonly record struct LogBackupSchedule
{
    public LogBackupSchedule(int intervalMinutes)
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

public sealed record BackupPlanDefinition
{
    public BackupPlanDefinition(
        BackupPlanMode mode,
        RecurringBackupSchedule fullSchedule,
        RecurringBackupSchedule? differentialSchedule,
        LogBackupSchedule? logSchedule,
        string timeZoneId,
        BackupStorageMode storageMode,
        Guid? storageTargetId,
        int recoveryWindowDays,
        bool useChecksum,
        bool useCompression,
        int backupTimeoutMinutes,
        int verifyTimeoutMinutes,
        int transferTimeoutMinutes)
    {
        ConfigurationValues.RequireDefined(mode, nameof(mode));
        fullSchedule.Validate();
        differentialSchedule?.Validate();
        logSchedule?.Validate();
        RequireSchedule(mode, BackupType.Differential, differentialSchedule.HasValue, "差异备份");
        RequireSchedule(mode, BackupType.Log, logSchedule.HasValue, "日志备份");
        ConfigurationValues.RequireDefined(storageMode, nameof(storageMode));
        if (recoveryWindowDays is < 1 or > 36_500)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryWindowDays), "恢复窗口必须介于 1 与 36500 天之间。");
        }

        switch (storageMode)
        {
            case BackupStorageMode.LocalOnly:
                if (storageTargetId is not null)
                {
                    throw new ArgumentException("仅本地模式不能配置远程存储目标。", nameof(storageTargetId));
                }

                break;
            case BackupStorageMode.LocalAndRemote:
            case BackupStorageMode.RemoteOnly:
                ConfigurationValues.RequireId(storageTargetId ?? Guid.Empty, nameof(storageTargetId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(storageMode), storageMode, "枚举值无效。");
        }

        Mode = mode;
        FullSchedule = fullSchedule;
        DifferentialSchedule = differentialSchedule;
        LogSchedule = logSchedule;
        TimeZoneId = ConfigurationValues.RequireTimeZoneId(timeZoneId, nameof(timeZoneId));
        StorageMode = storageMode;
        StorageTargetId = storageTargetId;
        RecoveryWindowDays = recoveryWindowDays;
        UseChecksum = useChecksum;
        UseCompression = useCompression;
        BackupTimeoutMinutes = ConfigurationValues.RequirePositive(backupTimeoutMinutes, 1_440, nameof(backupTimeoutMinutes));
        VerifyTimeoutMinutes = ConfigurationValues.RequirePositive(verifyTimeoutMinutes, 1_440, nameof(verifyTimeoutMinutes));
        TransferTimeoutMinutes = ConfigurationValues.RequirePositive(transferTimeoutMinutes, 1_440, nameof(transferTimeoutMinutes));
    }

    public BackupPlanMode Mode { get; }

    public RecurringBackupSchedule FullSchedule { get; }

    public RecurringBackupSchedule? DifferentialSchedule { get; }

    public LogBackupSchedule? LogSchedule { get; }

    public string TimeZoneId { get; }

    public BackupStorageMode StorageMode { get; }

    public Guid? StorageTargetId { get; }

    public int RecoveryWindowDays { get; }

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
            backupType == BackupType.Differential ? nameof(DifferentialSchedule) : nameof(LogSchedule));
    }
}
