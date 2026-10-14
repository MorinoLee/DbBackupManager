using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Domain.BackupPlans;

public enum BackupPlanBaselineEnablementDecision
{
    Queued = 1,
    SkippedManagedBaseline = 2,
    SkippedUncertainBaseline = 3,
    SkippedOutstandingFull = 4
}

public static class BackupPlanBaselineRules
{
    public static bool IsUncertain(DifferentialBaselineConclusion? conclusion) =>
        conclusion is DifferentialBaselineConclusion.Unknown
            or DifferentialBaselineConclusion.Unmanaged
            or DifferentialBaselineConclusion.Mismatch;

    /// <summary>
    /// 当前版本含 DIFF 且计划未暂停时，首次启用、版本刚改为含 DIFF，或从暂停恢复，都是启用事件。
    /// 仅 FULL、仍暂停，或未恢复时上一版本已经含 DIFF 的档位扩展，都不是启用事件。
    /// </summary>
    public static bool IsEnablement(
        bool isPaused,
        bool currentIncludesDifferential,
        bool previousIncludesDifferential,
        bool resumedFromPause = false)
    {
        if (isPaused || !currentIncludesDifferential)
        {
            return false;
        }

        return resumedFromPause || !previousIncludesDifferential;
    }

    public static BackupPlanBaselineEnablementDecision? Decide(
        bool isPaused,
        bool currentIncludesDifferential,
        bool previousIncludesDifferential,
        bool hasVerifiedManagedOrdinaryFull,
        DifferentialBaselineConclusion? activeBaselineConclusion,
        bool hasOutstandingOrdinaryPlanFull,
        bool resumedFromPause = false)
    {
        if (!IsEnablement(isPaused, currentIncludesDifferential, previousIncludesDifferential, resumedFromPause))
        {
            return null;
        }

        if (IsUncertain(activeBaselineConclusion))
        {
            return BackupPlanBaselineEnablementDecision.SkippedUncertainBaseline;
        }

        if (hasOutstandingOrdinaryPlanFull)
        {
            return BackupPlanBaselineEnablementDecision.SkippedOutstandingFull;
        }

        return hasVerifiedManagedOrdinaryFull
            ? BackupPlanBaselineEnablementDecision.SkippedManagedBaseline
            : BackupPlanBaselineEnablementDecision.Queued;
    }

    public static bool AllowsDifferentialExecution(
        bool hasVerifiedManagedOrdinaryFull,
        DifferentialBaselineConclusion? activeBaselineConclusion)
    {
        return hasVerifiedManagedOrdinaryFull && !IsUncertain(activeBaselineConclusion);
    }
}
