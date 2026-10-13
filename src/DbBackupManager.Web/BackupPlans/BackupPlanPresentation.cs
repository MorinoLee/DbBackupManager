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

    public static string Mode(int mode) => mode switch
    {
        1 => "仅完整备份",
        2 => "完整备份 + 差异备份",
        3 => "含日志备份（本阶段不可用）",
        _ => "未知模式",
    };

    public static string StorageMode(int mode) => mode switch
    {
        2 => "本地加远程",
        3 => "仅远程",
        1 => "仅本地",
        _ => "未知存储",
    };

    public static string BackupType(string value) => value switch
    {
        "Full" => "完整",
        "Differential" => "差异",
        "Log" => "日志",
        _ => "未知类型",
    };

    public static string Fact(string value) => value switch
    {
        "Succeeded" => "成功",
        "Failed" => "失败",
        "Indeterminate" => "结果不确定",
        "Cancelled" => "已取消",
        "NotApplicable" => "不适用",
        "Verified" => "已核对",
        "Mismatch" => "不一致",
        "Unknown" => "未知",
        _ => "未知",
    };

    public static string Baseline(string value) => value switch
    {
        "Known" => "受管基线已确认",
        "None" => "无受管基线",
        _ => "基线未知",
    };

    public static string RetentionReason(string? reason) => reason switch
    {
        null or "" => "—",
        "window_unknown" => "恢复窗口无法确定，文件继续受保护",
        "log_retention_deferred" => "日志备份的保留规则尚未启用，文件继续受保护",
        "uncertain" => "备份事实尚不确定，文件继续受保护",
        "active_task_dependency" => "仍有未结束的计划任务依赖它，不能删除",
        "completion_unknown" => "完成时间未知，文件继续受保护",
        "presence_not_deletable" => "副本不在可删除状态",
        "baseline_unresolved" => "活动基线尚未确定，相关完整备份继续保留",
        "active_differential_baseline" => "这是当前差异备份的活动基线，继续保留",
        "differential_base" => "它是仍需保留的差异备份的基准完整备份",
        "latest_available_full" => "它是该位置最新的可用完整备份",
        "latest_restore_point" => "它是该位置最新的可恢复点",
        "window_start_anchor" => "它是恢复窗口起点需要的备份",
        "inside_window" => "它落在该位置的恢复窗口内",
        "base_unavailable_at_location" => "差异备份所需的基准完整备份在该位置不可用",
        "outside_window_unneeded" => "已超出恢复窗口，且不再被恢复依赖需要",
        "dependency_unproven" => "恢复依赖尚未证明，不能删除",
        "artifact.owner_unknown" => "副本归属尚未证明，不能删除",
        "remote_staging_deferred" => "仅远程任务的本地暂存仍需保留",
        "remote_staging_releasable" => "远端恢复依赖已确认，本地暂存可以释放",
        "remote_base_unconfirmed" => "远端还没有确认基准完整备份，暂时不能恢复",
        "remote_dependent_unconfirmed" => "依赖这份备份的差异备份尚未在远端确认",
        "remote.staging_retained" => "本地暂存继续保留，直到远端恢复依赖确认",
        "remote.staging_missing" => "本地暂存已缺失，需要核对",
        "plan.retention.claimed" => "正在按保留规则处理",
        "plan.retention.protected" => "保留规则要求继续保护",
        "plan.retention.deleted" => "已按保留规则删除",
        _ => "保留原因尚未分类，文件继续受保护",
    };
}
