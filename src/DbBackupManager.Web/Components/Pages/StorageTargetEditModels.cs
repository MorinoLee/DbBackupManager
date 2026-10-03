using System.ComponentModel.DataAnnotations;

namespace DbBackupManager.Web.Components.Pages;

/// <summary>
/// 存储目标编辑弹窗使用的表单模型；保存时映射回 Application 输入记录。
/// 字段级校验只提供早期提示，协议、路径、凭据类型与主机密钥指纹的最终校验由服务端完成。
/// </summary>
public sealed class StorageTargetFormModel : IValidatableObject
{
    [Required(ErrorMessage = "请输入存储目标名称。"), StringLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>1 = SMB，2 = SFTP。</summary>
    public int Protocol { get; set; } = 1;

    [Required(ErrorMessage = "请输入主机。"), StringLength(255)]
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    [Required(ErrorMessage = "请输入根路径。"), StringLength(2048)]
    public string BasePath { get; set; } = string.Empty;

    public Guid CredentialId { get; set; }

    public string Fingerprint { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (CredentialId == Guid.Empty)
        {
            yield return new ValidationResult("请选择访问凭据。", [nameof(CredentialId)]);
        }

        if (Protocol != 2)
        {
            yield break;
        }

        if (Port is < 1 or > 65535)
        {
            yield return new ValidationResult("SFTP 端口必须在 1 到 65535 之间。", [nameof(Port)]);
        }

        if (string.IsNullOrWhiteSpace(Fingerprint)
            || !Fingerprint.StartsWith("SHA256:", StringComparison.Ordinal))
        {
            yield return new ValidationResult("请输入 SHA256: 开头的 SFTP 主机密钥指纹。", [nameof(Fingerprint)]);
        }
    }
}
