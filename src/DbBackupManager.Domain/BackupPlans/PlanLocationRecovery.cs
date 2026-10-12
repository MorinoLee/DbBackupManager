using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.BackupPlans;

/// <summary>
/// 一个已登记备份集的逻辑事实。在场情况由各位置的副本决定，不在这里填写。
/// </summary>
public readonly record struct PlanRecoverySetInput(
    Guid Id,
    BackupType? Type,
    DateTimeOffset? CompletedAtUtc,
    Guid? DifferentialBaseId,
    bool? IsCopyOnly,
    bool Proven,
    bool ReferencedByActiveTask);

/// <summary>某个存储位置上的已登记副本。RemoteOnly 的本地暂存不参与该位置的恢复链。</summary>
public readonly record struct PlanRecoveryCopyInput(
    Guid FileId,
    Guid? BackupSetId,
    BackupFileLocation Location,
    Guid? StorageTargetId,
    BackupFileStatus Status,
    bool RemoteOnlyStaging,
    bool OwnershipProven);

public readonly record struct PlanLocationWindow(
    BackupFileLocation Location,
    Guid? StorageTargetId,
    int? WindowDays);

public readonly record struct PlanCopyRetentionDecision(
    Guid FileId,
    Guid? BackupSetId,
    RecoveryWindowVerdict Verdict,
    string Reason);

public readonly record struct PlanLocationRecoveryResult(
    BackupFileLocation Location,
    Guid? StorageTargetId,
    int? WindowDays,
    LocationRecoveryConclusion Conclusion,
    IReadOnlyList<PlanCopyRetentionDecision> Copies);

/// <summary>
/// 把已登记事实投影成 §5.3 的单位置判定。投影只加严：未知类型、未证明的依赖、
/// 归属不清和 RemoteOnly 暂存都不会变成可删除。
/// </summary>
public static class PlanLocationRecovery
{
    public const string DependencyUnproven = "dependency_unproven";

    public const string RemoteStagingDeferred = "remote_staging_deferred";

    public const string OwnershipUnproven = "artifact.owner_unknown";

    public static IReadOnlyList<PlanLocationRecoveryResult> Evaluate(
        DateTimeOffset nowUtc,
        ActiveBaseline baseline,
        bool protectAllFullsBecauseDependencyUnproven,
        bool databaseActivityBlocksDeletion,
        IReadOnlyList<PlanLocationWindow> windows,
        IReadOnlyList<PlanRecoverySetInput> sets,
        IReadOnlyList<PlanRecoveryCopyInput> copies)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(copies);
        var now = ConfigurationValues.RequireUtc(nowUtc, nameof(nowUtc));
        var setsById = new Dictionary<Guid, PlanRecoverySetInput>();
        foreach (var set in sets)
        {
            if (set.Id == Guid.Empty || !setsById.TryAdd(set.Id, set))
            {
                throw new ArgumentException("备份集标识不能为空且不能重复。", nameof(sets));
            }

            if (set.CompletedAtUtc is { } completedAt)
            {
                ConfigurationValues.RequireUtc(completedAt, nameof(sets));
            }

            if (set.Type is { } type)
            {
                ConfigurationValues.RequireDefined(type, nameof(sets));
            }
        }

        var seenFiles = new HashSet<Guid>();
        foreach (var copy in copies)
        {
            ConfigurationValues.RequireDefined(copy.Location, nameof(copies));
            ConfigurationValues.RequireDefined(copy.Status, nameof(copies));
            if (copy.FileId == Guid.Empty || !seenFiles.Add(copy.FileId))
            {
                throw new ArgumentException("副本标识不能为空且不能重复。", nameof(copies));
            }

            if (copy.Location == BackupFileLocation.Local)
            {
                if (copy.StorageTargetId is not null)
                {
                    throw new ArgumentException("本地副本不能带存储目标。", nameof(copies));
                }
            }
            else if (copy.StorageTargetId is null || copy.StorageTargetId == Guid.Empty)
            {
                throw new ArgumentException("远程副本必须带存储目标。", nameof(copies));
            }
        }

        var locations = windows.Select(window => (window.Location, window.StorageTargetId)).ToHashSet();
        foreach (var copy in copies)
        {
            locations.Add((copy.Location, copy.StorageTargetId));
        }

        var effectiveBaseline = protectAllFullsBecauseDependencyUnproven
            ? ActiveBaseline.Unresolved
            : baseline;
        var results = new List<PlanLocationRecoveryResult>();
        foreach (var location in locations.OrderBy(item => item.Location).ThenBy(item => item.StorageTargetId))
        {
            var window = windows.FirstOrDefault(item => item.Location == location.Location && item.StorageTargetId == location.StorageTargetId);
            var windowDays = windows.Any(item => item.Location == location.Location && item.StorageTargetId == location.StorageTargetId)
                ? window.WindowDays
                : null;
            results.Add(EvaluateLocation(
                now,
                effectiveBaseline,
                databaseActivityBlocksDeletion,
                location.Location,
                location.StorageTargetId,
                windowDays,
                setsById,
                copies.Where(copy => copy.Location == location.Location && copy.StorageTargetId == location.StorageTargetId).ToArray()));
        }

        return results;
    }

    public static bool IsDeletable(PlanCopyRetentionDecision decision) =>
        decision.Verdict == RecoveryWindowVerdict.Deletable
        && decision.Reason == RecoveryProtectionReason.OutsideWindowUnneeded;

    private static PlanLocationRecoveryResult EvaluateLocation(
        DateTimeOffset now,
        ActiveBaseline baseline,
        bool databaseActivityBlocksDeletion,
        BackupFileLocation location,
        Guid? storageTargetId,
        int? windowDays,
        Dictionary<Guid, PlanRecoverySetInput> setsById,
        IReadOnlyList<PlanRecoveryCopyInput> copies)
    {
        var decisions = new Dictionary<Guid, PlanCopyRetentionDecision>();
        var present = new Dictionary<Guid, List<PlanRecoveryCopyInput>>();
        foreach (var copy in copies)
        {
            if (copy.RemoteOnlyStaging)
            {
                decisions[copy.FileId] = Protect(copy, RemoteStagingDeferred);
                continue;
            }

            if (!copy.OwnershipProven || copy.BackupSetId is null || !setsById.ContainsKey(copy.BackupSetId.Value))
            {
                decisions[copy.FileId] = Protect(copy, copy.OwnershipProven ? DependencyUnproven : OwnershipUnproven);
                continue;
            }

            if (copy.Status == BackupFileStatus.Deleted)
            {
                continue;
            }

            if (!present.TryGetValue(copy.BackupSetId.Value, out var group))
            {
                group = [];
                present.Add(copy.BackupSetId.Value, group);
            }

            group.Add(copy);
        }

        var recoverySets = new List<RecoveryBackupSet>();
        var presenceBySet = new Dictionary<Guid, BackupCopyPresence>();
        foreach (var (setId, group) in present)
        {
            var presence = group.Count == 1 ? Presence(group[0].Status) : BackupCopyPresence.Unknown;
            presenceBySet[setId] = presence;
            if (group.Count != 1)
            {
                foreach (var copy in group)
                {
                    decisions[copy.FileId] = Protect(copy, DependencyUnproven);
                }
            }

            recoverySets.Add(Project(setsById[setId], presence, databaseActivityBlocksDeletion));
        }

        foreach (var set in setsById.Values)
        {
            if (presenceBySet.ContainsKey(set.Id)
                || set.Type != BackupType.Differential
                || !set.Proven
                || set.DifferentialBaseId is not { } baseId
                || !presenceBySet.ContainsKey(baseId))
            {
                continue;
            }

            // 依赖已经登记、副本尚未落到本位置时，仍保护基准，避免先删基线再登记副本。
            recoverySets.Add(Project(set, BackupCopyPresence.Unknown, databaseActivityBlocksDeletion));
        }

        var assessment = RecoveryWindowProtection.Evaluate(windowDays, now, recoverySets, baseline);
        var reasonBySet = assessment.Decisions.ToDictionary(decision => decision.BackupSetId);
        foreach (var (setId, group) in present)
        {
            if (group.Count != 1 || decisions.ContainsKey(group[0].FileId))
            {
                continue;
            }

            var decision = reasonBySet[setId];
            decisions[group[0].FileId] = new PlanCopyRetentionDecision(
                group[0].FileId,
                setId,
                decision.Verdict,
                decision.Reason);
        }

        var ordered = copies
            .Where(copy => decisions.ContainsKey(copy.FileId))
            .Select(copy => decisions[copy.FileId])
            .ToArray();
        return new PlanLocationRecoveryResult(location, storageTargetId, windowDays, assessment.Conclusion, ordered);
    }

    private static RecoveryBackupSet Project(
        PlanRecoverySetInput set,
        BackupCopyPresence presence,
        bool databaseActivityBlocksDeletion)
    {
        var proven = set.Proven && set.Type is BackupType.Full or BackupType.Differential or BackupType.Log && set.IsCopyOnly is not null;
        var type = set.Type is BackupType.Full or BackupType.Differential or BackupType.Log
            ? set.Type.Value
            : BackupType.Full;
        if (set.Type is not (BackupType.Full or BackupType.Differential or BackupType.Log))
        {
            proven = false;
        }

        return new RecoveryBackupSet(
            set.Id,
            type,
            proven ? set.CompletedAtUtc : set.Type == BackupType.Log ? set.CompletedAtUtc : null,
            set.Type == BackupType.Differential ? set.DifferentialBaseId : null,
            set.IsCopyOnly == true,
            presence,
            proven ? BackupCertainty.Determined : BackupCertainty.Uncertain,
            set.ReferencedByActiveTask || databaseActivityBlocksDeletion);
    }

    private static BackupCopyPresence Presence(BackupFileStatus status) => status switch
    {
        BackupFileStatus.Available or BackupFileStatus.DeletePending or BackupFileStatus.DeleteFailed => BackupCopyPresence.Available,
        BackupFileStatus.Missing => BackupCopyPresence.Missing,
        _ => BackupCopyPresence.Unknown,
    };

    private static PlanCopyRetentionDecision Protect(PlanRecoveryCopyInput copy, string reason) =>
        new(copy.FileId, copy.BackupSetId, RecoveryWindowVerdict.Protected, reason);
}
