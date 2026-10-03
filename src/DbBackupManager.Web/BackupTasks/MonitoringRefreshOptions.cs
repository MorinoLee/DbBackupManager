namespace DbBackupManager.Web.BackupTasks;

/// <summary>
/// 监控页漏消息校准选项。30 秒落在 30–60 秒窗口内：短于窗口下限会增加空闲查询，长于上限会拖慢断线后的兜底对齐。
/// 显式重连通知才是断线恢复的主补偿；周期校准只覆盖漏送与长期漂移。
/// </summary>
public sealed class MonitoringRefreshOptions
{
    public static readonly MonitoringRefreshOptions Default = new();

    public TimeSpan CalibrationInterval { get; init; } = TimeSpan.FromSeconds(30);
}
