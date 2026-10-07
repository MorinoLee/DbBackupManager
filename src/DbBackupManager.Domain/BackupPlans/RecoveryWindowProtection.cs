using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.BackupPlans;

public enum RecoveryWindowVerdict
{
    Protected = 1,
    Deletable = 2
}

public enum LocationRecoveryConclusion
{
    NotRestorable = 1,
    RestorableWindowIncomplete = 2,
    RestorableWindowSatisfied = 3
}

public enum BackupCopyPresence
{
    Available = 1,
    Missing = 2,
    Unknown = 3
}

public enum BackupCertainty
{
    Determined = 1,
    Uncertain = 2
}

public static class RecoveryProtectionReason
{
    public const string WindowUnknown = "window_unknown";

    public const string LogRetentionDeferred = "log_retention_deferred";

    public const string Uncertain = "uncertain";

    public const string ActiveTaskDependency = "active_task_dependency";

    public const string CompletionUnknown = "completion_unknown";

    public const string PresenceNotDeletable = "presence_not_deletable";

    public const string BaselineUnresolved = "baseline_unresolved";

    public const string ActiveDifferentialBaseline = "active_differential_baseline";

    public const string DifferentialBase = "differential_base";

    public const string LatestAvailableFull = "latest_available_full";

    public const string LatestRestorePoint = "latest_restore_point";

    public const string WindowStartAnchor = "window_start_anchor";

    public const string InsideWindow = "inside_window";

    public const string BaseUnavailableAtLocation = "base_unavailable_at_location";

    public const string OutsideWindowUnneeded = "outside_window_unneeded";
}

public readonly record struct ActiveBaseline
{
    private ActiveBaseline(bool isUnresolved, Guid? fullId)
    {
        IsUnresolved = isUnresolved;
        FullId = fullId;
    }

    public static ActiveBaseline None { get; } = new(isUnresolved: false, fullId: null);

    public static ActiveBaseline Unresolved { get; } = new(isUnresolved: true, fullId: null);

    public bool IsUnresolved { get; }

    public Guid? FullId { get; }

    public static ActiveBaseline Known(Guid fullId)
    {
        if (fullId == Guid.Empty)
        {
            throw new ArgumentException("活动基线标识不能为空。", nameof(fullId));
        }

        return new ActiveBaseline(isUnresolved: false, fullId);
    }
}

public readonly record struct RecoveryBackupSet(
    Guid Id,
    BackupType Type,
    DateTimeOffset? CompletedAtUtc,
    Guid? DifferentialBaseId,
    bool IsCopyOnly,
    BackupCopyPresence Presence,
    BackupCertainty Certainty,
    bool ReferencedByActiveTask)
{
    public RecoveryBackupSet(
        Guid id,
        BackupType type,
        DateTimeOffset? completedAtUtc,
        Guid? differentialBaseId,
        BackupCopyPresence presence,
        BackupCertainty certainty,
        bool referencedByActiveTask)
        : this(id, type, completedAtUtc, differentialBaseId, IsCopyOnly: false, presence, certainty, referencedByActiveTask)
    {
    }
}

public readonly record struct RecoveryProtectionDecision(
    Guid BackupSetId,
    RecoveryWindowVerdict Verdict,
    string Reason);

public readonly record struct RecoveryWindowAssessment(
    IReadOnlyList<RecoveryProtectionDecision> Decisions,
    LocationRecoveryConclusion Conclusion);

public static class RecoveryWindowProtection
{
    public static RecoveryWindowAssessment Evaluate(
        int? windowDays,
        DateTimeOffset nowUtc,
        IReadOnlyList<RecoveryBackupSet> backupSets,
        ActiveBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(backupSets);
        var now = ConfigurationValues.RequireUtc(nowUtc, nameof(nowUtc));
        EnsureUnique(backupSets);
        foreach (var set in backupSets)
        {
            if (set.CompletedAtUtc is { } completedAt)
            {
                ConfigurationValues.RequireUtc(completedAt, nameof(backupSets));
            }

            ConfigurationValues.RequireDefined(set.Type, nameof(backupSets));
            ConfigurationValues.RequireDefined(set.Presence, nameof(backupSets));
            ConfigurationValues.RequireDefined(set.Certainty, nameof(backupSets));
        }

        if (!TryGetWindow(windowDays, now, out var windowStart))
        {
            return new RecoveryWindowAssessment(
                backupSets.Select(set => Protect(set.Id, RecoveryProtectionReason.WindowUnknown)).ToArray(),
                LocationRecoveryConclusion.NotRestorable);
        }

        var decisions = new Dictionary<Guid, RecoveryProtectionDecision>(backupSets.Count);
        var calculationSets = new List<RecoveryBackupSet>();
        var setsById = backupSets.ToDictionary(set => set.Id);
        foreach (var set in backupSets)
        {
            if (Gate(set) is { } reason)
            {
                decisions[set.Id] = Protect(set.Id, reason);
            }
            else
            {
                calculationSets.Add(set);
            }
        }

        var calculationFulls = calculationSets.Where(set => set.Type == BackupType.Full).ToArray();
        var calculationById = calculationSets.ToDictionary(set => set.Id);
        var restorePoints = calculationSets.Where(set => IsRestorePoint(set, calculationById)).ToArray();
        var newestFullAt = Newest(calculationFulls);
        var newestRestoreAt = Newest(restorePoints);
        var anchorAt = Newest(restorePoints.Where(set => set.CompletedAtUtc!.Value < windowStart).ToArray());
        var protectedDifferentials = new HashSet<Guid>();
        foreach (var set in calculationSets)
        {
            if (set.Type != BackupType.Differential)
            {
                continue;
            }

            if (IsInside(set, windowStart)
                || IsAt(set, newestRestoreAt)
                || IsAt(set, anchorAt))
            {
                protectedDifferentials.Add(set.Id);
            }
        }

        var differentialBases = new HashSet<Guid>();
        foreach (var id in protectedDifferentials)
        {
            AddReferencedFull(calculationById[id], setsById, differentialBases);
        }

        foreach (var set in backupSets)
        {
            if (ProtectsBaseDespiteGate(set))
            {
                AddReferencedFull(set, setsById, differentialBases);
            }
        }

        foreach (var set in calculationSets)
        {
            decisions[set.Id] = Decide(
                set,
                baseline,
                windowStart,
                newestFullAt,
                newestRestoreAt,
                anchorAt,
                differentialBases,
                calculationById);
        }

        if (baseline.IsUnresolved)
        {
            foreach (var set in calculationSets)
            {
                if (set.Type == BackupType.Full)
                {
                    decisions[set.Id] = Protect(set.Id, RecoveryProtectionReason.BaselineUnresolved);
                }
            }
        }

        var ordered = backupSets.Select(set => decisions[set.Id]).ToArray();
        return new RecoveryWindowAssessment(
            ordered,
            Conclude(calculationFulls, calculationSets, windowStart, baseline, calculationById));
    }

    private static RecoveryProtectionDecision Decide(
        RecoveryBackupSet set,
        ActiveBaseline baseline,
        DateTimeOffset windowStart,
        DateTimeOffset? newestFullAt,
        DateTimeOffset? newestRestoreAt,
        DateTimeOffset? anchorAt,
        HashSet<Guid> differentialBases,
        Dictionary<Guid, RecoveryBackupSet> eligibleById)
    {
        if (baseline.FullId == set.Id && set.Type == BackupType.Full)
        {
            return Protect(set.Id, RecoveryProtectionReason.ActiveDifferentialBaseline);
        }

        if (differentialBases.Contains(set.Id))
        {
            return Protect(set.Id, RecoveryProtectionReason.DifferentialBase);
        }

        if (set.Type == BackupType.Full && IsAt(set, newestFullAt))
        {
            return Protect(set.Id, RecoveryProtectionReason.LatestAvailableFull);
        }

        if (IsAt(set, newestRestoreAt) && IsRestorePoint(set, eligibleById))
        {
            return Protect(set.Id, RecoveryProtectionReason.LatestRestorePoint);
        }

        if (IsAt(set, anchorAt) && IsRestorePoint(set, eligibleById))
        {
            return Protect(set.Id, RecoveryProtectionReason.WindowStartAnchor);
        }

        if (IsInside(set, windowStart))
        {
            return Protect(
                set.Id,
                set.Type == BackupType.Differential && !HasEligibleBase(set, eligibleById)
                    ? RecoveryProtectionReason.BaseUnavailableAtLocation
                    : RecoveryProtectionReason.InsideWindow);
        }

        return new RecoveryProtectionDecision(
            set.Id,
            RecoveryWindowVerdict.Deletable,
            RecoveryProtectionReason.OutsideWindowUnneeded);
    }

    private static LocationRecoveryConclusion Conclude(
        RecoveryBackupSet[] eligibleFulls,
        IReadOnlyList<RecoveryBackupSet> eligible,
        DateTimeOffset windowStart,
        ActiveBaseline baseline,
        Dictionary<Guid, RecoveryBackupSet> eligibleById)
    {
        if (eligibleFulls.Length == 0
            || eligible.Any(set => set.Type == BackupType.Differential
                && IsInside(set, windowStart)
                && !HasEligibleBase(set, eligibleById)))
        {
            return LocationRecoveryConclusion.NotRestorable;
        }

        var holdsBaseline = baseline.FullId is null
            || eligibleFulls.Any(set => set.Id == baseline.FullId);
        var coversWindowStart = eligibleFulls.Any(set => set.CompletedAtUtc!.Value <= windowStart);
        if (!holdsBaseline || baseline.IsUnresolved || !coversWindowStart)
        {
            return LocationRecoveryConclusion.RestorableWindowIncomplete;
        }

        return LocationRecoveryConclusion.RestorableWindowSatisfied;
    }

    private static string? Gate(RecoveryBackupSet set)
    {
        ConfigurationValues.RequireDefined(set.Type, nameof(set));
        if (set.Type == BackupType.Log)
        {
            return RecoveryProtectionReason.LogRetentionDeferred;
        }

        if (set.Certainty == BackupCertainty.Uncertain)
        {
            return RecoveryProtectionReason.Uncertain;
        }

        if (set.ReferencedByActiveTask)
        {
            return RecoveryProtectionReason.ActiveTaskDependency;
        }

        if (set.CompletedAtUtc is null)
        {
            return RecoveryProtectionReason.CompletionUnknown;
        }

        if (set.Presence != BackupCopyPresence.Available)
        {
            return RecoveryProtectionReason.PresenceNotDeletable;
        }

        if (set.Type is not (BackupType.Full or BackupType.Differential))
        {
            return RecoveryProtectionReason.PresenceNotDeletable;
        }

        return null;
    }

    private static bool ProtectsBaseDespiteGate(RecoveryBackupSet set)
    {
        if (set.Type != BackupType.Differential)
        {
            return false;
        }

        if (set.Certainty == BackupCertainty.Uncertain
            || set.ReferencedByActiveTask
            || set.CompletedAtUtc is null)
        {
            return true;
        }

        return set.Presence == BackupCopyPresence.Unknown;
    }

    private static void AddReferencedFull(
        RecoveryBackupSet differential,
        Dictionary<Guid, RecoveryBackupSet> setsById,
        HashSet<Guid> differentialBases)
    {
        if (differential.DifferentialBaseId is { } baseId
            && setsById.TryGetValue(baseId, out var baseSet)
            && baseSet.Type == BackupType.Full)
        {
            differentialBases.Add(baseId);
        }
    }

    private static bool IsRestorePoint(RecoveryBackupSet set, Dictionary<Guid, RecoveryBackupSet> eligibleById)
    {
        return set.Type == BackupType.Full || HasEligibleBase(set, eligibleById);
    }

    private static bool HasEligibleBase(RecoveryBackupSet set, Dictionary<Guid, RecoveryBackupSet> eligibleById)
    {
        return set.DifferentialBaseId is { } baseId
            && eligibleById.TryGetValue(baseId, out var baseSet)
            && baseSet.Type == BackupType.Full
            && !baseSet.IsCopyOnly;
    }

    private static bool IsInside(RecoveryBackupSet set, DateTimeOffset windowStart)
    {
        return set.CompletedAtUtc!.Value >= windowStart;
    }

    private static bool IsAt(RecoveryBackupSet set, DateTimeOffset? time)
    {
        return time is { } value && set.CompletedAtUtc!.Value == value;
    }

    private static DateTimeOffset? Newest(RecoveryBackupSet[] sets)
    {
        return sets.Length == 0 ? null : sets.Max(set => set.CompletedAtUtc!.Value);
    }

    private static bool TryGetWindow(int? windowDays, DateTimeOffset nowUtc, out DateTimeOffset windowStart)
    {
        if (windowDays is null or < 1 or > 36_500)
        {
            windowStart = default;
            return false;
        }

        windowStart = nowUtc - TimeSpan.FromDays(windowDays.Value);
        return true;
    }

    private static void EnsureUnique(IReadOnlyList<RecoveryBackupSet> backupSets)
    {
        var seen = new HashSet<Guid>();
        foreach (var set in backupSets)
        {
            if (set.Id == Guid.Empty || !seen.Add(set.Id))
            {
                throw new ArgumentException("备份集标识不能为空且不能重复。", nameof(backupSets));
            }
        }
    }

    private static RecoveryProtectionDecision Protect(Guid id, string reason)
    {
        return new RecoveryProtectionDecision(id, RecoveryWindowVerdict.Protected, reason);
    }
}
