using DbBackupManager.Application.BackupPlans;

namespace DbBackupManager.Web.BackupPlans;

internal static class BackupPlanPresentation
{
    public static (int Status, string Code, string Message) Error(BackupPlanCatalogCode code) => code switch
    {
        BackupPlanCatalogCode.AuthenticationRequired => (401, "authentication_required", "登录状态已失效，请重新登录。"),
        BackupPlanCatalogCode.Invalid => (400, "backup_plan_invalid", "请检查计划模式、时间、时区、存储位置和恢复窗口。"),
        BackupPlanCatalogCode.NotFound => (404, "backup_plan_not_found", "当前数据库还没有备份计划，或数据库不存在。"),
        BackupPlanCatalogCode.AlreadyExists => (409, "backup_plan_exists", "该数据库已有当前备份计划（含已暂停）。请改用现有计划。"),
        BackupPlanCatalogCode.VersionConflict => (409, "backup_plan_version_conflict", "备份计划已被其他人修改，请刷新后重试。"),
        BackupPlanCatalogCode.LogUnsupported => (400, "backup_plan_log_unsupported", "首版不接受包含日志备份的计划，也不会修改数据库恢复模式。"),
        BackupPlanCatalogCode.Conflict => (409, "backup_plan_conflict", "备份计划保存冲突，请刷新后重试。"),
        _ => (503, "backup_plan_unavailable", "备份计划暂不可用，请稍后重试。"),
    };
}
