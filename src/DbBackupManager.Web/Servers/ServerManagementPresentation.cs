using DbBackupManager.Application.Servers;
namespace DbBackupManager.Web.Servers;

internal static class ServerManagementPresentation
{
    public static (int Status, string Code, string Message) Error(ManagementCode code) => code switch
    {
        ManagementCode.AuthenticationRequired => (401, "authentication_required", "登录状态已失效，请重新登录。"),
        ManagementCode.ValidationFailed => (400, "configuration_invalid", "请检查必填信息、路径、凭据类型和启用状态。"),
        ManagementCode.NotFound => (404, "configuration_not_found", "记录已不存在，请刷新。"),
        ManagementCode.Conflict => (409, "configuration_conflict", "名称重复、发现的库名存在冲突或配置已更新，请刷新后重试。"),
        ManagementCode.TargetFailed => (422, "target_probe_failed", "目标连接或数据库发现失败，请检查实例配置和连接状态。"),
        _ => (503, "configuration_unavailable", "暂时无法访问配置，请稍后重试。"),
    };
}
