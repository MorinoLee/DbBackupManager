using System.ComponentModel.DataAnnotations;

namespace DbBackupManager.Web.Components.Pages;

/// <summary>
/// 手动备份配置编辑弹窗使用的表单模型；保存时映射回 Application 输入记录。
/// 存储模式：1 = 仅本地，2 = 本地加远程，3 = 仅远程；字段级校验只提供早期提示，最终校验由服务端完成。
/// </summary>
public sealed class PolicyFormModel : IValidatableObject
{
    [Required(ErrorMessage = "请输入配置名称。"), StringLength(200)]
    public string Name { get; set; } = string.Empty;

    public Guid DatabaseId { get; set; }

    public int StorageMode { get; set; } = 1;

    public Guid StorageTargetId { get; set; }

    public int RetentionDays { get; set; } = 7;

    public int RemoteRetentionDays { get; set; } = 7;

    public int BackupMinutes { get; set; } = 120;

    public int VerifyMinutes { get; set; } = 60;

    public int TransferMinutes { get; set; } = 60;

    public bool Compression { get; set; }

    public bool Enabled { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
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
