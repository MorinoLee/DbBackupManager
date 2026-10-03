using System.ComponentModel.DataAnnotations;

namespace DbBackupManager.Web.Components.Pages;

internal sealed class SmtpSettingsFormModel
{
    [Required(ErrorMessage = "请填写 SMTP 主机。")]
    [StringLength(255)]
    public string Host { get; set; } = string.Empty;

    [Range(1, 65535, ErrorMessage = "端口必须在 1 到 65535 之间。")]
    public int Port { get; set; } = 587;

    public int SecurityMode { get; set; } = 1;

    [Required(ErrorMessage = "请填写发件人地址。")]
    [StringLength(320)]
    public string FromAddress { get; set; } = string.Empty;

    [Range(1, 300, ErrorMessage = "超时必须在 1 到 300 秒之间。")]
    public int TimeoutSeconds { get; set; } = 30;

    public bool IsEnabled { get; set; }
}
