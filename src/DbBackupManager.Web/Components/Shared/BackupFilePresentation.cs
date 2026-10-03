namespace DbBackupManager.Web.Components.Shared;

/// <summary>
/// 备份副本投影与文件存储失败分类的中文呈现。
/// 只返回固定中文文案，不向用户回显端点、账号、完整路径或异常原文。
/// </summary>
internal static class BackupFilePresentation
{
    public static string Location(string value) => value switch
    {
        "Local" => "本地",
        "Remote" => "远程",
        _ => value,
    };

    public static string Protocol(string value) => value switch
    {
        "Smb" => "SMB",
        "Sftp" => "SFTP",
        _ => value,
    };

    public static string Status(string value) => value switch
    {
        "Available" => "可用",
        "DeletePending" => "待删除",
        "DeleteFailed" => "删除失败",
        "Deleted" => "已删除",
        "Missing" => "缺失",
        _ => value,
    };

    /// <summary>删除失败显示稳定失败分类；缺失副本说明外部已找不到登记文件，不套用通用操作失败文案。</summary>
    public static string? StatusNote(string status, string? errorCode) => status switch
    {
        "DeleteFailed" => Failure(errorCode),
        "Missing" => "登记副本在目标位置已找不到。",
        _ => null,
    };

    /// <summary>文件存储适配器的稳定失败分类；未知分类使用通用文案，不回显原始代码。</summary>
    public static string Failure(string? code) => code switch
    {
        "InvalidRequest" => "请求内容无效，请检查目标配置。",
        "PlatformUnsupported" => "当前运行平台不支持该文件协议。",
        "CredentialUnavailable" => "访问凭据不可用，请检查凭据是否存在、已启用且类型匹配。",
        "CredentialProtectionUnavailable" => "凭据保护不可用，请联系部署管理员检查业务密钥目录。",
        "CredentialInvalid" => "凭据无法解密或格式无效，请在凭据页面重新保存。",
        "AuthenticationFailed" => "身份验证失败，请检查用户名、密码或私钥。",
        "AuthorizationDenied" => "访问权限不足，请检查共享与目录权限。",
        "HostKeyMismatch" => "SFTP 主机密钥指纹不匹配，已拒绝连接。",
        "ConnectionFailed" => "无法访问目标，请检查主机、路径、网络以及用户名和密码。",
        "PathRejected" => "路径安全检查未通过，请检查根路径配置。",
        "FileNotFound" => "指定文件不存在。",
        "FileAlreadyExists" => "目标位置已存在同名文件。",
        "NotRegularFile" => "目标不是普通文件。",
        "SourceChanged" => "源文件在操作期间发生变化。",
        "LengthMismatch" => "文件长度与登记不一致。",
        "TimedOut" => "操作超时，请稍后重试。",
        "Cancelled" => "操作已取消。",
        "ConnectionInterrupted" => "连接中断，操作结果不确定，请核对后重试。",
        "OperationRejected" => "服务端拒绝了该操作。",
        "InvalidResponse" => "服务端响应无效。",
        _ => "文件操作失败，请检查目标配置后重试。",
    };
}
