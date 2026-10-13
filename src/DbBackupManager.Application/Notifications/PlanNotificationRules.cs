using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Notifications;

namespace DbBackupManager.Application.Notifications;

/// <summary>
/// 统一计划通知只报告已经提交的运行事实。不生成开启日志备份的建议。
/// </summary>
public static class PlanNotificationRules
{
    public const string LocationNotRestorable = "location.not_restorable";

    public static NotificationType? ForCommittedTask(BackupTaskStatus status) => status switch
    {
        BackupTaskStatus.Failed => NotificationType.TaskFailed,
        BackupTaskStatus.NeedsAttention => NotificationType.TaskIndeterminate,
        _ => null,
    };

    public static bool IsRemoteGapReason(string? reason) =>
        reason is PlanLocationRecovery.RemoteBaseUnconfirmed
            or PlanLocationRecovery.RemoteDependentUnconfirmed
            or RecoveryProtectionReason.BaseUnavailableAtLocation;

    /// <summary>
    /// 只在投影已经标成远端暂时无法恢复时返回错误码。未解析基线保持未知，不返回码。
    /// </summary>
    public static string? RemoteErrorCode(BackupLocationRecovery location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.Location != BackupFileLocation.Remote || !location.RemoteUnrecoverable)
        {
            return null;
        }

        return location.GapReasonCodes.Count > 0
            ? location.GapReasonCodes[0]
            : LocationNotRestorable;
    }

    public static string PreferRemoteCode(string? current, string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        if (current is null)
        {
            return candidate;
        }

        var currentGap = current != LocationNotRestorable;
        var candidateGap = candidate != LocationNotRestorable;
        if (currentGap != candidateGap)
        {
            return candidateGap ? candidate : current;
        }

        return string.CompareOrdinal(candidate, current) < 0 ? candidate : current;
    }

    public static string Fallback(NotificationType type) => type switch
    {
        NotificationType.TaskFailed => "stage.confirmed_failed",
        NotificationType.TaskIndeterminate => "stage.indeterminate",
        NotificationType.RemoteUnrecoverable => LocationNotRestorable,
        NotificationType.RetentionDeleteFailed => "file.delete_failed",
        NotificationType.RetentionMissing => "file.missing",
        NotificationType.AdminTest => "smtp.test",
        _ => "notification.unknown",
    };
}
