using DbBackupManager.Application.Servers;

namespace DbBackupManager.Web.StorageTargets;

internal static class StorageTargetPresentation
{
    public static (int Status, string Code, string Message) Error(ManagementCode code) => code switch
    {
        ManagementCode.AuthenticationRequired => (401, "authentication_required", "登录状态已失效，请重新登录。"),
        ManagementCode.ValidationFailed => (400, "storage_target_invalid", "请检查名称、协议、主机、路径、凭据类型和版本信息。"),
        ManagementCode.NotFound => (404, "storage_target_not_found", "该存储目标已不存在，请刷新列表。"),
        ManagementCode.Conflict => (409, "storage_target_conflict", "名称已被使用，或目标已被其他操作更新，请刷新后重试。"),
        ManagementCode.TargetFailed => (422, "storage_target_probe_failed", "存储目标连接测试失败，请检查协议、凭据、路径和主机密钥。"),
        _ => (503, "storage_target_unavailable", "暂时无法访问存储目标，请稍后重试。"),
    };
}
