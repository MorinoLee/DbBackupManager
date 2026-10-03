using System.ComponentModel.DataAnnotations;

namespace DbBackupManager.Web.Components.Pages;

/// <summary>
/// 计划备份配置编辑弹窗使用的表单模型。调度规则由服务端计算器解释，这里只做字段级提示。
/// </summary>
public sealed class ScheduledPolicyFormModel : IValidatableObject
{
    [Required(ErrorMessage = "请输入配置名称。"), StringLength(200)]
    public string Name { get; set; } = string.Empty;

    public Guid DatabaseId { get; set; }

    public int ScheduleType { get; set; } = 1;

    public TimeSpan? LocalTime { get; set; } = TimeSpan.FromHours(2);

    public bool Monday { get; set; }

    public bool Tuesday { get; set; }

    public bool Wednesday { get; set; }

    public bool Thursday { get; set; }

    public bool Friday { get; set; }

    public bool Saturday { get; set; }

    public bool Sunday { get; set; }

    [Required(ErrorMessage = "请选择时区。")]
    public string TimeZoneId { get; set; } = "Taipei Standard Time";

    public int StorageMode { get; set; } = 1;

    public Guid StorageTargetId { get; set; }

    public int RetentionDays { get; set; } = 7;

    public int RemoteRetentionDays { get; set; } = 7;

    public int BackupMinutes { get; set; } = 120;

    public int VerifyMinutes { get; set; } = 60;

    public int TransferMinutes { get; set; } = 60;

    public bool Compression { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 星期标志与位图的互转。
    /// 这里刻意保留字面量位值，不让生产表单依赖经现有项目引用传递可见的 Domain
    /// <c>BackupWeekdays</c>。位值一致性改由 ScheduledPolicyFormModelTests 绑定具名枚举值锁定。
    /// <c>DaysOfWeek</c> 是持久化列
    /// （<c>BackupPolicies.DaysOfWeek</c>），枚举改位序时必须被测试拦下并人工处置，而不是让表单静默
    /// 跟随，使新策略与既有行使用不同位序。
    /// </summary>
    public int DaysOfWeek()
    {
        var days = 0;
        if (Monday) days |= 1 << 0;
        if (Tuesday) days |= 1 << 1;
        if (Wednesday) days |= 1 << 2;
        if (Thursday) days |= 1 << 3;
        if (Friday) days |= 1 << 4;
        if (Saturday) days |= 1 << 5;
        if (Sunday) days |= 1 << 6;
        return days;
    }

    public void SetDays(int days)
    {
        Monday = (days & (1 << 0)) != 0;
        Tuesday = (days & (1 << 1)) != 0;
        Wednesday = (days & (1 << 2)) != 0;
        Thursday = (days & (1 << 3)) != 0;
        Friday = (days & (1 << 4)) != 0;
        Saturday = (days & (1 << 5)) != 0;
        Sunday = (days & (1 << 6)) != 0;
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ScheduleType is not (1 or 2))
        {
            yield return new ValidationResult("请选择 Daily 或 Weekly。", [nameof(ScheduleType)]);
        }

        if (ScheduleType == 2 && DaysOfWeek() == 0)
        {
            yield return new ValidationResult("Weekly 策略至少选择一个星期。", [nameof(Monday)]);
        }

        if (LocalTime is null || LocalTime < TimeSpan.Zero || LocalTime >= TimeSpan.FromDays(1))
        {
            yield return new ValidationResult("请输入有效的本地时间。", [nameof(LocalTime)]);
        }

        if (StorageMode is < 1 or > 3)
        {
            yield return new ValidationResult("请选择存储模式。", [nameof(StorageMode)]);
            yield break;
        }

        if (StorageMode != 3 && RetentionDays is < 1 or > 36500)
        {
            yield return new ValidationResult("本地保留天数必须在 1 到 36500 之间。", [nameof(RetentionDays)]);
        }

        if (StorageMode == 1)
        {
            yield break;
        }

        if (StorageTargetId == Guid.Empty)
        {
            yield return new ValidationResult("请选择远程存储目标。", [nameof(StorageTargetId)]);
        }

        if (RemoteRetentionDays is < 1 or > 36500)
        {
            yield return new ValidationResult("远程保留天数必须在 1 到 36500 之间。", [nameof(RemoteRetentionDays)]);
        }

        if (TransferMinutes is < 1 or > 1440)
        {
            yield return new ValidationResult("传输超时必须在 1 到 1440 分钟之间。", [nameof(TransferMinutes)]);
        }
    }
}
