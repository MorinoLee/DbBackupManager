using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Notifications;

namespace DbBackupManager.Application.Notifications;

/// <summary>
/// 一次远端缺口投影。保持原样时不新开也不关闭；可恢复时关闭该目标上已打开的缺口；打开时以给出的错误码为当前缺口。
/// </summary>
public readonly record struct RemoteGapProjection
{
    private RemoteGapProjection(bool updatesOpenEpisodes, IReadOnlyList<string> codes)
    {
        UpdatesOpenEpisodes = updatesOpenEpisodes;
        Codes = codes;
    }

    public bool UpdatesOpenEpisodes { get; }

    public IReadOnlyList<string> Codes { get; }

    public static RemoteGapProjection Hold { get; } = new(false, []);

    public static RemoteGapProjection Recovered { get; } = new(true, []);

    public static RemoteGapProjection Open(IReadOnlyList<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        if (codes.Count == 0)
        {
            throw new ArgumentException("打开缺口时至少要有一个错误码。", nameof(codes));
        }

        return new(true, codes);
    }
}

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
        // 执行已结束但备份结果未知：仍按结果未知通知，不伪装成明确失败。
        BackupTaskStatus.ClosedUnknown => NotificationType.TaskIndeterminate,
        _ => null,
    };

    /// <summary>核对确认成功、可安全重试或取消都不补发邮件。</summary>
    public static NotificationType? ForReconciliation(BackupReconciliationOutcome outcome) => outcome switch
    {
        BackupReconciliationOutcome.Failed => NotificationType.TaskFailed,
        BackupReconciliationOutcome.Inconclusive => NotificationType.TaskIndeterminate,
        _ => null,
    };

    /// <summary>任务已经结束、当前阶段为空时，远端缺口通知记在 Cleanup。</summary>
    public static BackupTaskStage RemoteNoticeStage(BackupTaskStage? currentStage) =>
        currentStage ?? BackupTaskStage.Cleanup;

    /// <summary>
    /// 一个远端位置上当前应当通知的缺口。每个错误码单独去重；没有具体原因时用通用不可恢复码。
    /// 位置未知时不新开也不关闭。只有位置已不是未知、不是远端暂时无法恢复、且结论可恢复时才关闭已打开缺口。
    /// </summary>
    public static RemoteGapProjection RemoteGapCodes(BackupLocationRecovery location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.Location != BackupFileLocation.Remote
            || location.StorageTargetId is not Guid target
            || target == Guid.Empty
            || location.Unknown)
        {
            return RemoteGapProjection.Hold;
        }

        if (!location.RemoteUnrecoverable)
        {
            return location.Conclusion is LocationRecoveryConclusion.RestorableWindowIncomplete
                or LocationRecoveryConclusion.RestorableWindowSatisfied
                ? RemoteGapProjection.Recovered
                : RemoteGapProjection.Hold;
        }

        return RemoteGapProjection.Open(location.GapReasonCodes.Count > 0
            ? location.GapReasonCodes
            : [LocationNotRestorable]);
    }

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
