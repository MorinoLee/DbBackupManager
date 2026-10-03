using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.Configuration;

public sealed record BackupPolicySettings(
    BackupStorageMode StorageMode,
    Guid? StorageTargetId,
    BackupScheduleType ScheduleType,
    TimeOnly LocalTime,
    BackupWeekdays DaysOfWeek,
    string TimeZoneId,
    int? LocalRetentionDays,
    int? RemoteRetentionDays,
    bool UseChecksum,
    bool UseCompression,
    bool UseCopyOnly,
    int BackupTimeoutMinutes,
    int VerifyTimeoutMinutes,
    int TransferTimeoutMinutes);

public sealed class BackupPolicy : ConcurrentEntity
{
    private const BackupWeekdays AllWeekdays =
        BackupWeekdays.Monday
        | BackupWeekdays.Tuesday
        | BackupWeekdays.Wednesday
        | BackupWeekdays.Thursday
        | BackupWeekdays.Friday
        | BackupWeekdays.Saturday
        | BackupWeekdays.Sunday;

    private BackupPolicy()
    {
    }

    public BackupPolicy(
        Guid id,
        string name,
        Guid databaseId,
        BackupPolicySettings settings,
        bool isEnabled = false,
        bool isManualOnly = false,
        DateTimeOffset? nowUtc = null)
        : base(id)
    {
        DatabaseId = ConfigurationValues.RequireId(databaseId, nameof(databaseId));
        BackupType = BackupType.Full;
        Apply(name, settings);
        IsManualOnly = isManualOnly;
        ApplyEnabled(isEnabled, nowUtc, creating: true);
    }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    public Guid DatabaseId { get; private set; }

    public BackupType BackupType { get; private set; }

    public BackupStorageMode StorageMode { get; private set; }

    public Guid? StorageTargetId { get; private set; }

    public BackupScheduleType ScheduleType { get; private set; }

    public TimeOnly LocalTime { get; private set; }

    public BackupWeekdays DaysOfWeek { get; private set; }

    public string TimeZoneId { get; private set; } = string.Empty;

    public int? LocalRetentionDays { get; private set; }

    public int? RemoteRetentionDays { get; private set; }

    public bool UseChecksum { get; private set; }

    public bool UseCompression { get; private set; }

    public bool UseCopyOnly { get; private set; }

    public int BackupTimeoutMinutes { get; private set; }

    public int VerifyTimeoutMinutes { get; private set; }

    public int TransferTimeoutMinutes { get; private set; }

    public bool IsEnabled { get; private set; }

    public bool IsManualOnly { get; private set; }

    public DateTimeOffset? ScheduleEffectiveFromUtc { get; private set; }

    public BackupPolicySettings ToSettings() =>
        new(
            StorageMode,
            StorageTargetId,
            ScheduleType,
            LocalTime,
            DaysOfWeek,
            TimeZoneId,
            LocalRetentionDays,
            RemoteRetentionDays,
            UseChecksum,
            UseCompression,
            UseCopyOnly,
            BackupTimeoutMinutes,
            VerifyTimeoutMinutes,
            TransferTimeoutMinutes);

    public void Update(string name, BackupPolicySettings settings)
    {
        UpdateCore(name, settings, nowUtc: null);
    }

    public void Update(string name, BackupPolicySettings settings, DateTimeOffset nowUtc)
    {
        UpdateCore(name, settings, nowUtc);
    }

    public void SetEnabled(bool isEnabled)
    {
        ApplyEnabled(isEnabled, nowUtc: null, creating: false);
    }

    public void SetEnabled(bool isEnabled, DateTimeOffset nowUtc)
    {
        ApplyEnabled(isEnabled, nowUtc, creating: false);
    }

    private void UpdateCore(string name, BackupPolicySettings settings, DateTimeOffset? nowUtc)
    {
        var scheduleChanged = ScheduleChanged(settings);
        if (!IsManualOnly && IsEnabled && scheduleChanged && nowUtc is null)
        {
            throw new ArgumentException("启用中的计划策略修改调度字段必须提供当前 UTC 时刻。");
        }

        Apply(name, settings);
        if (!IsManualOnly && IsEnabled && scheduleChanged)
        {
            ScheduleEffectiveFromUtc = RequireUtc(nowUtc, "启用中的计划策略修改调度字段必须提供当前 UTC 时刻。");
        }
    }

    private bool ScheduleChanged(BackupPolicySettings settings)
    {
        return ScheduleType != settings.ScheduleType
            || LocalTime != settings.LocalTime
            || DaysOfWeek != settings.DaysOfWeek
            || !string.Equals(TimeZoneId, settings.TimeZoneId, StringComparison.Ordinal);
    }

    private void ApplyEnabled(bool isEnabled, DateTimeOffset? nowUtc, bool creating)
    {
        if (IsManualOnly)
        {
            IsEnabled = isEnabled;
            ScheduleEffectiveFromUtc = null;
            return;
        }

        if (isEnabled)
        {
            var enabling = creating || !IsEnabled;
            if (enabling)
            {
                ScheduleEffectiveFromUtc = RequireUtc(nowUtc, "启用计划策略必须提供当前 UTC 时刻。");
            }

            IsEnabled = true;
            return;
        }

        IsEnabled = false;
        ScheduleEffectiveFromUtc = null;
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset? nowUtc, string message)
    {
        if (nowUtc is null)
        {
            throw new ArgumentException(message);
        }

        return nowUtc.Value.ToUniversalTime();
    }

    private void Apply(string name, BackupPolicySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validatedName = ConfigurationValues.RequireText(name, 200, nameof(name));
        ConfigurationValues.RequireDefined(settings.StorageMode, nameof(settings.StorageMode));
        ConfigurationValues.RequireDefined(settings.ScheduleType, nameof(settings.ScheduleType));

        if ((settings.DaysOfWeek & ~AllWeekdays) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "星期组合包含无效位。");
        }

        if (settings.ScheduleType == BackupScheduleType.Daily
            && settings.DaysOfWeek != BackupWeekdays.None)
        {
            throw new ArgumentException("Daily 策略不能指定星期。", nameof(settings));
        }

        if (settings.ScheduleType == BackupScheduleType.Weekly
            && settings.DaysOfWeek == BackupWeekdays.None)
        {
            throw new ArgumentException("Weekly 策略至少选择一个星期。", nameof(settings));
        }

        ValidateStorage(settings);

        var validatedTimeZoneId = ConfigurationValues.RequireTimeZoneId(
            settings.TimeZoneId,
            nameof(settings));
        var validatedBackupTimeout = ConfigurationValues.RequirePositive(
            settings.BackupTimeoutMinutes,
            1440,
            nameof(settings));
        var validatedVerifyTimeout = ConfigurationValues.RequirePositive(
            settings.VerifyTimeoutMinutes,
            1440,
            nameof(settings));
        var validatedTransferTimeout = ConfigurationValues.RequirePositive(
            settings.TransferTimeoutMinutes,
            1440,
            nameof(settings));

        Name = validatedName;
        NormalizedName = ConfigurationValues.Normalize(validatedName);
        StorageMode = settings.StorageMode;
        StorageTargetId = settings.StorageTargetId;
        ScheduleType = settings.ScheduleType;
        LocalTime = settings.LocalTime;
        DaysOfWeek = settings.DaysOfWeek;
        TimeZoneId = validatedTimeZoneId;
        LocalRetentionDays = settings.LocalRetentionDays;
        RemoteRetentionDays = settings.RemoteRetentionDays;
        UseChecksum = settings.UseChecksum;
        UseCompression = settings.UseCompression;
        UseCopyOnly = settings.UseCopyOnly;
        BackupTimeoutMinutes = validatedBackupTimeout;
        VerifyTimeoutMinutes = validatedVerifyTimeout;
        TransferTimeoutMinutes = validatedTransferTimeout;
    }

    private static void ValidateStorage(BackupPolicySettings settings)
    {
        static void ValidateRetention(int? value, string parameterName)
        {
            if (value is null || value is < 1 or > 36_500)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "保留天数必须介于 1 与 36500 之间。");
            }
        }

        switch (settings.StorageMode)
        {
            case BackupStorageMode.LocalOnly:
                if (settings.StorageTargetId is not null || settings.RemoteRetentionDays is not null)
                {
                    throw new ArgumentException("仅本地模式不能配置远程目标或远程保留天数。");
                }

                ValidateRetention(settings.LocalRetentionDays, nameof(settings.LocalRetentionDays));
                break;

            case BackupStorageMode.LocalAndRemote:
                ConfigurationValues.RequireId(
                    settings.StorageTargetId ?? Guid.Empty,
                    nameof(settings.StorageTargetId));
                ValidateRetention(settings.LocalRetentionDays, nameof(settings.LocalRetentionDays));
                ValidateRetention(settings.RemoteRetentionDays, nameof(settings.RemoteRetentionDays));
                break;

            case BackupStorageMode.RemoteOnly:
                ConfigurationValues.RequireId(
                    settings.StorageTargetId ?? Guid.Empty,
                    nameof(settings.StorageTargetId));
                if (settings.LocalRetentionDays is not null)
                {
                    throw new ArgumentException("仅远程模式不能配置本地保留天数。");
                }

                ValidateRetention(settings.RemoteRetentionDays, nameof(settings.RemoteRetentionDays));
                break;
        }
    }
}
