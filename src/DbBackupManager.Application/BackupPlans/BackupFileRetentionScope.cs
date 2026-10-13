using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;

namespace DbBackupManager.Application.BackupPlans;

/// <summary>
/// 文件列表只为当前页的副本取保留原因。位置图不完整时，依赖全库窗口的原因改为未知，避免把没看到的历史说成可删除。
/// </summary>
public static class BackupFileRetentionScope
{
    public static IReadOnlyDictionary<Guid, string> Select(
        IReadOnlyCollection<Guid> pageFileIds,
        IReadOnlyList<PlanLocationAssessment> assessments,
        bool locationGraphComplete) =>
        Select(
            pageFileIds,
            assessments,
            locationGraphComplete
                ? pageFileIds as IReadOnlySet<Guid> ?? pageFileIds.ToHashSet()
                : Empty);

    /// <summary>
    /// <paramref name="filesWithProvenLocationGraph"/> 只放已经按本页文件证明位置依赖的副本。
    /// 不在其中的窗口类原因改为未知；仅由该副本自身就能确定的原因保持原样。
    /// </summary>
    public static IReadOnlyDictionary<Guid, string> Select(
        IReadOnlyCollection<Guid> pageFileIds,
        IReadOnlyList<PlanLocationAssessment> assessments,
        IReadOnlySet<Guid> filesWithProvenLocationGraph)
    {
        ArgumentNullException.ThrowIfNull(pageFileIds);
        ArgumentNullException.ThrowIfNull(assessments);
        ArgumentNullException.ThrowIfNull(filesWithProvenLocationGraph);
        var page = pageFileIds as IReadOnlySet<Guid> ?? pageFileIds.ToHashSet();
        var reasons = new Dictionary<Guid, string>();
        foreach (var copy in assessments.SelectMany(assessment => assessment.Copies))
        {
            if (page.Contains(copy.FileId))
            {
                reasons.TryAdd(copy.FileId, Reason(copy.Reason, filesWithProvenLocationGraph.Contains(copy.FileId)));
            }
        }

        return reasons;
    }

    public static bool DependsOnLocationGraph(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return !IsLocallyDetermined(reason);
    }

    private static readonly IReadOnlySet<Guid> Empty = new HashSet<Guid>();

    private static string Reason(string reason, bool locationGraphComplete) =>
        locationGraphComplete || IsLocallyDetermined(reason)
            ? reason
            : RecoveryProtectionReason.WindowUnknown;

    private static bool IsLocallyDetermined(string reason) =>
        reason is RecoveryProtectionReason.WindowUnknown
            or RecoveryProtectionReason.Uncertain
            or RecoveryProtectionReason.CompletionUnknown
            or RecoveryProtectionReason.ActiveTaskDependency
            or RecoveryProtectionReason.LogRetentionDeferred
            or RecoveryProtectionReason.PresenceNotDeletable
            or RecoveryProtectionReason.BaselineUnresolved
            or RecoveryProtectionReason.ActiveDifferentialBaseline
            or PlanLocationRecovery.DependencyUnproven
            or PlanLocationRecovery.OwnershipUnproven
            or PlanLocationRecovery.RemoteBaseUnconfirmed
            or PlanLocationRecovery.RemoteDependentUnconfirmed
            or PlanLocationRecovery.RemoteStagingDeferred;
}
