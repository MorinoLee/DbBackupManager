using System.ComponentModel.DataAnnotations;
using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Web.Components.Pages;

/// <summary>
/// 统一备份计划编辑表单。模式只提供完整备份，以及完整备份加差异备份。
/// 星期位值与持久化列一致，不直接引用领域枚举，避免表单随枚举声明顺序静默变化。
/// </summary>
public sealed class BackupPlanFormModel : IValidatableObject
{
    [Required(ErrorMessage = "请输入计划名称。"), StringLength(200)]
    public string Name { get; set; } = string.Empty;

    public Guid DatabaseId { get; set; }

    /// <summary>1 = 仅完整备份，2 = 完整备份 + 差异备份。不提供日志模式。</summary>
    public int Mode { get; set; } = 1;

    public int FullScheduleType { get; set; } = 1;

    public TimeSpan? FullLocalTime { get; set; } = TimeSpan.FromHours(2);

    public bool FullMonday { get; set; }

    public bool FullTuesday { get; set; }

    public bool FullWednesday { get; set; }

    public bool FullThursday { get; set; }

    public bool FullFriday { get; set; }

    public bool FullSaturday { get; set; }

    public bool FullSunday { get; set; }

    public int DifferentialScheduleType { get; set; } = 1;

    public TimeSpan? DifferentialLocalTime { get; set; } = TimeSpan.FromHours(3);

    public bool DifferentialMonday { get; set; }

    public bool DifferentialTuesday { get; set; }

    public bool DifferentialWednesday { get; set; }

    public bool DifferentialThursday { get; set; }

    public bool DifferentialFriday { get; set; }

    public bool DifferentialSaturday { get; set; }

    public bool DifferentialSunday { get; set; }

    [Required(ErrorMessage = "请选择时区。")]
    public string TimeZoneId { get; set; } = "Taipei Standard Time";

    public int StorageMode { get; set; } = 1;

    public Guid StorageTargetId { get; set; }

    public int LocalRecoveryWindowDays { get; set; } = 7;

    public int RemoteRecoveryWindowDays { get; set; } = 30;

    public bool UseChecksum { get; set; } = true;

    public bool UseCompression { get; set; }

    public int BackupMinutes { get; set; } = 120;

    public int VerifyMinutes { get; set; } = 60;

    public int TransferMinutes { get; set; } = 60;

    public bool IsPaused { get; set; }

    public int FullDaysOfWeek() => Days(FullMonday, FullTuesday, FullWednesday, FullThursday, FullFriday, FullSaturday, FullSunday);

    public int DifferentialDaysOfWeek() => Days(
        DifferentialMonday, DifferentialTuesday, DifferentialWednesday, DifferentialThursday,
        DifferentialFriday, DifferentialSaturday, DifferentialSunday);

    public void SetFullDays(int days) =>
        (FullMonday, FullTuesday, FullWednesday, FullThursday, FullFriday, FullSaturday, FullSunday) = Unpack(days);

    public void SetDifferentialDays(int days) =>
        (DifferentialMonday, DifferentialTuesday, DifferentialWednesday, DifferentialThursday, DifferentialFriday, DifferentialSaturday, DifferentialSunday) = Unpack(days);

    public BackupPlanInput ToInput()
    {
        var localOnly = StorageMode == 1;
        var remoteOnly = StorageMode == 3;
        return new BackupPlanInput(
            DatabaseId,
            Name.Trim(),
            Mode,
            Schedule(FullScheduleType, FullLocalTime, FullDaysOfWeek()),
            Mode == 2 ? Schedule(DifferentialScheduleType, DifferentialLocalTime, DifferentialDaysOfWeek()) : null,
            TimeZoneId.Trim(),
            StorageMode,
            localOnly ? null : StorageTargetId,
            remoteOnly ? null : LocalRecoveryWindowDays,
            localOnly ? null : RemoteRecoveryWindowDays,
            UseChecksum,
            UseCompression,
            BackupMinutes,
            VerifyMinutes,
            TransferMinutes,
            IsPaused,
            Guid.NewGuid());
    }

    public static BackupPlanFormModel FromView(BackupPlanView plan)
    {
        var model = new BackupPlanFormModel
        {
            Name = plan.Name,
            DatabaseId = plan.DatabaseId,
            Mode = (int)plan.Mode,
            FullScheduleType = (int)plan.FullSchedule.ScheduleType,
            FullLocalTime = plan.FullSchedule.LocalTime.ToTimeSpan(),
            DifferentialScheduleType = plan.DifferentialSchedule is { } differential ? (int)differential.ScheduleType : 1,
            DifferentialLocalTime = plan.DifferentialSchedule?.LocalTime.ToTimeSpan() ?? TimeSpan.FromHours(3),
            TimeZoneId = plan.TimeZoneId,
            StorageMode = (int)plan.StorageMode,
            StorageTargetId = plan.StorageTargetId ?? Guid.Empty,
            LocalRecoveryWindowDays = plan.LocalRecoveryWindowDays ?? 7,
            RemoteRecoveryWindowDays = plan.RemoteRecoveryWindowDays ?? 30,
            UseChecksum = plan.UseChecksum,
            UseCompression = plan.UseCompression,
            BackupMinutes = plan.BackupTimeoutMinutes,
            VerifyMinutes = plan.VerifyTimeoutMinutes,
            TransferMinutes = plan.TransferTimeoutMinutes,
            IsPaused = plan.IsPaused,
        };
        model.SetFullDays((int)plan.FullSchedule.DaysOfWeek);
        if (plan.DifferentialSchedule is { } schedule)
        {
            model.SetDifferentialDays((int)schedule.DaysOfWeek);
        }

        return model;
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            yield return new ValidationResult("请输入计划名称。", [nameof(Name)]);
        }

        if (DatabaseId == Guid.Empty)
        {
            yield return new ValidationResult("请选择数据库。", [nameof(DatabaseId)]);
        }

        if (Mode is not (1 or 2))
        {
            yield return new ValidationResult("请选择仅完整备份，或完整备份加差异备份。", [nameof(Mode)]);
        }

        foreach (var result in ValidateSchedule(FullScheduleType, FullLocalTime, FullDaysOfWeek(), nameof(FullScheduleType), nameof(FullMonday), nameof(FullLocalTime)))
        {
            yield return result;
        }

        if (Mode == 2)
        {
            foreach (var result in ValidateSchedule(
                DifferentialScheduleType, DifferentialLocalTime, DifferentialDaysOfWeek(),
                nameof(DifferentialScheduleType), nameof(DifferentialMonday), nameof(DifferentialLocalTime)))
            {
                yield return result;
            }
        }

        if (string.IsNullOrWhiteSpace(TimeZoneId))
        {
            yield return new ValidationResult("请选择时区。", [nameof(TimeZoneId)]);
        }

        if (StorageMode is < 1 or > 3)
        {
            yield return new ValidationResult("请选择存储模式。", [nameof(StorageMode)]);
            yield break;
        }

        if (StorageMode != 3 && LocalRecoveryWindowDays is < 1 or > 36500)
        {
            yield return new ValidationResult("本地恢复窗口必须在 1 到 36500 天之间。", [nameof(LocalRecoveryWindowDays)]);
        }

        if (StorageMode != 1)
        {
            if (StorageTargetId == Guid.Empty)
            {
                yield return new ValidationResult("请选择远程存储目标。", [nameof(StorageTargetId)]);
            }

            if (RemoteRecoveryWindowDays is < 1 or > 36500)
            {
                yield return new ValidationResult("远端恢复窗口必须在 1 到 36500 天之间。", [nameof(RemoteRecoveryWindowDays)]);
            }

            if (TransferMinutes is < 1 or > 1440)
            {
                yield return new ValidationResult("传输超时必须在 1 到 1440 分钟之间。", [nameof(TransferMinutes)]);
            }
        }

        if (BackupMinutes is < 1 or > 1440)
        {
            yield return new ValidationResult("备份超时必须在 1 到 1440 分钟之间。", [nameof(BackupMinutes)]);
        }

        if (VerifyMinutes is < 1 or > 1440)
        {
            yield return new ValidationResult("校验超时必须在 1 到 1440 分钟之间。", [nameof(VerifyMinutes)]);
        }
    }

    private static BackupPlanScheduleInput Schedule(int scheduleType, TimeSpan? localTime, int days)
    {
        if (localTime is null || localTime < TimeSpan.Zero || localTime >= TimeSpan.FromDays(1))
        {
            throw new InvalidOperationException("本地时间无效。");
        }

        return new BackupPlanScheduleInput(scheduleType, TimeOnly.FromTimeSpan(localTime.Value), scheduleType == 2 ? days : 0);
    }

    private static IEnumerable<ValidationResult> ValidateSchedule(
        int scheduleType, TimeSpan? localTime, int days, string typeName, string dayName, string timeName)
    {
        if (scheduleType is not (1 or 2))
        {
            yield return new ValidationResult("请选择每天或每周。", [typeName]);
        }

        if (scheduleType == 2 && days == 0)
        {
            yield return new ValidationResult("每周至少选择一个星期。", [dayName]);
        }

        if (localTime is null || localTime < TimeSpan.Zero || localTime >= TimeSpan.FromDays(1))
        {
            yield return new ValidationResult("请输入有效的本地时间。", [timeName]);
        }
    }

    private static int Days(bool monday, bool tuesday, bool wednesday, bool thursday, bool friday, bool saturday, bool sunday)
    {
        var days = 0;
        if (monday) days |= 1 << 0;
        if (tuesday) days |= 1 << 1;
        if (wednesday) days |= 1 << 2;
        if (thursday) days |= 1 << 3;
        if (friday) days |= 1 << 4;
        if (saturday) days |= 1 << 5;
        if (sunday) days |= 1 << 6;
        return days;
    }

    private static (bool Monday, bool Tuesday, bool Wednesday, bool Thursday, bool Friday, bool Saturday, bool Sunday) Unpack(int days) =>
        ((days & (1 << 0)) != 0, (days & (1 << 1)) != 0, (days & (1 << 2)) != 0, (days & (1 << 3)) != 0,
            (days & (1 << 4)) != 0, (days & (1 << 5)) != 0, (days & (1 << 6)) != 0);
}
