using DbBackupManager.Application.Servers;

namespace DbBackupManager.Web.Notifications;

internal static class SmtpSettingsPresentation
{
    public static (int Status, string Code, string Message) Error(ManagementCode code) => code switch
    {
        ManagementCode.AuthenticationRequired => (401, "authentication_required", "登录状态已失效，请重新登录。"),
        ManagementCode.ValidationFailed => (400, "smtp_settings_invalid", "请检查主机、端口、安全模式、发件人、收件人、凭据和版本信息。"),
        ManagementCode.NotFound => (404, "smtp_settings_not_found", "尚未保存 SMTP 设置，请先保存后再继续。"),
        ManagementCode.Conflict => (409, "smtp_settings_conflict", "设置已被其他操作更新，或收件人重复，请刷新后重试。"),
        _ => (503, "smtp_settings_unavailable", "暂时无法访问通知设置，请稍后重试。"),
    };
}
