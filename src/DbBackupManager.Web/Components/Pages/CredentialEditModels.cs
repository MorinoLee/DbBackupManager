using System.ComponentModel.DataAnnotations;
using DbBackupManager.Application.FileCredentials;

namespace DbBackupManager.Web.Components.Pages;

/// <summary>
/// 凭据编辑弹窗使用的表单模型。密码只用于当次保存，提交后立即清空，不做任何回显。
/// </summary>
public sealed class SqlCredentialFormModel
{
    [Required(ErrorMessage = "请输入凭据名称。"), StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入 SQL 用户名。"), StringLength(128)]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入密码。"), StringLength(128)]
    public string Password { get; set; } = string.Empty;
}

public sealed class FileCredentialFormModel : IValidatableObject
{
    public FileCredentialKind Kind { get; set; } = FileCredentialKind.SmbPassword;

    [Required(ErrorMessage = "请输入凭据名称。"), StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入文件访问用户名。"), StringLength(256)]
    public string Username { get; set; } = string.Empty;

    [StringLength(1024)]
    public string Password { get; set; } = string.Empty;

    [StringLength(32768)]
    public string PrivateKey { get; set; } = string.Empty;

    [StringLength(1024)]
    public string? Passphrase { get; set; }

    public bool IsPrivateKey => Kind == FileCredentialKind.SftpPrivateKey;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (IsPrivateKey)
        {
            if (string.IsNullOrWhiteSpace(PrivateKey))
            {
                yield return new ValidationResult("请输入 SFTP 私钥。", [nameof(PrivateKey)]);
            }

            yield break;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            yield return new ValidationResult("请输入密码。", [nameof(Password)]);
        }
    }
}
