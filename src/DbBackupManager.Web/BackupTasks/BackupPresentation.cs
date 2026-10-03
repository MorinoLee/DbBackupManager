using DbBackupManager.Application.BackupTasks;
namespace DbBackupManager.Web.BackupTasks;

internal static class BackupPresentation
{
    public static string? FailureSummary(string? code) => FailureReason(code)?.Split(['，', '。'])[0];
    public static string Invocation(string value) => value switch
    {
        "Prepared" => "已准备",
        "Running" => "调用中",
        "Succeeded" => "调用成功",
        "ConfirmedFailed" => "确认失败",
        "Indeterminate" => "结果待核对",
        _ => value,
    };
    public static string HistoryReason(string value) => value switch
    {
        "task.created" => "任务已提交，等待 Worker 领取",
        "execution.claimed" => "Worker 已领取任务",
        "execution.preparation_failed" => "备份路径无效，任务未开始执行",
        "task.cancellation_requested" => "已请求取消",
        "task.retry_requested" => "已提交手动重试",
        "execution.lease_expired" => "执行租约过期，需要核对结果",
        "stage.succeeded" => "本阶段执行成功",
        "stage.confirmed_failed" => "本阶段确认失败",
        "stage.indeterminate" => "本阶段结果不确定，需要核对",
        "task.cancelled_safe_boundary" => "已在安全边界取消",
        "reconciliation.succeeded" => "核对确认成功",
        "reconciliation.safe_to_retry" => "核对确认可以重试",
        "reconciliation.failed" => "核对确认失败",
        "reconciliation.cancelled" => "核对确认已取消",
        "reconciliation.inconclusive" => "核对证据不足，继续等待",
        "admin_reconciliation_requested" => "管理员已请求重新核对",
        "admin.confirmed_failed" => "管理员核对证据后确认本阶段失败",
        _ => "状态已更新",
    };

    public static string ReconciliationReason(string? code) => code switch
    {
        null or "" => "尚无核对结论",
        "reconciliation.file_missing" => "只读核对未找到任务专属备份文件",
        "reconciliation_evidence_insufficient" => "核对证据不足，任务继续等待核对",
        "reconciliation.inconclusive" => "核对证据不足，任务继续等待核对",
        "admin_reconciliation_requested" => "管理员已请求重新核对",
        "connection_lost" => "连接中断，结果需要核对",
        "lease_expired" => "执行租约过期，需要核对结果",
        "result_unknown" => "外部执行结果尚未确定",
        "backup_result_unknown" => "外部执行结果尚未确定",
        _ => "核对结果待确认",
    };
    public static string? FailureReason(string? code) => code switch
    {
        null => null,
        "admin_confirmed_failed" => "管理员已核对证据并确认本阶段失败；处理原因后可单独提交重试。",
        "backup_path_invalid" => "完整备份路径或路径规则无效，任务未开始执行。请缩短目录或名称，并以修正后的配置创建新任务。",
        "smb_credential_unavailable" => "无法加载文件访问凭据，尚未进行 SMB 登录。请检查凭据启用状态及 Web、Worker 的共享业务密钥配置。",
        "smb_credential_reference_invalid" => "文件访问凭据不存在、已停用或类型不匹配，请检查凭据配置。",
        "smb_credential_version_unsupported" => "文件凭据保护格式不受支持，请通过凭据页面重新保存密码。",
        "smb_key_ring_unavailable" => "Worker 无法读取业务密钥，请检查启动环境、共享密钥目录及服务身份权限。",
        "smb_credential_decryption_failed" => "文件访问密码解密失败，尚未进行 SMB 登录。请确认 Web 与 Worker 使用相同业务密钥并保留历史密钥。",
        "smb_authentication_failed" => "Windows 无法建立文件访问身份，请检查账号、密码及账号所属计算机或域。",
        "smb_source_unavailable" => "无法读取 SMB 暂存位置，请检查网络、共享路径及共享和文件权限。",
        "smb_path_rejected" => "暂存文件路径校验未通过，请检查目录映射及重解析点。",
        "smb_platform_unsupported" => "SMB 暂存访问需要在 Windows 上运行 Worker。",
        _ => "任务未能完成，请查看阶段与技术详情，并在处理原因后按任务状态操作。",
    };
    public static (int Status, string Code, string Message) Error(BackupManagementCode code) => code switch
    {
        BackupManagementCode.AuthenticationRequired => (401, "authentication_required", "登录状态已失效，请重新登录。"),
        BackupManagementCode.Invalid => (400, "backup_configuration_invalid", "请检查备份设置、完整路径长度、存储模式、存储目标、数据库纳管和凭据启用状态。"),
        BackupManagementCode.NotFound => (404, "backup_not_found", "配置或任务不存在，请刷新后重试。"),
        BackupManagementCode.Conflict => (409, "backup_conflict", "配置已改变、名称重复或任务状态不允许此操作，请刷新后重试。"),
        _ => (503, "backup_unavailable", "备份管理暂不可用，请稍后重试。"),
    };
    public static string State(string value) => value switch
    { "Pending" => "等待执行", "Running" => "执行中", "NeedsAttention" => "待核对", "Succeeded" => "成功", "Failed" => "失败", "Cancelled" => "已取消", _ => value };
    public static string Stage(string? value) => value switch
    { "Backup" => "生成备份", "VerifyLocal" => "本地校验", "Transfer" => "传输", "ValidateCopy" => "副本校验", "Cleanup" => "清理", _ => "—" };
}
