using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.BackupPlans;

public enum BackupSlotDisposition
{
    Pending = 1,
    Succeeded = 2,
    Failed = 3
}

public readonly record struct BackupScheduleSlotKey(
    Guid PlanId,
    Guid PlanVersionId,
    BackupType BackupType,
    DateTimeOffset SlotUtc);

public readonly record struct BackupPlanSlotCandidate(
    Guid PlanVersionId,
    BackupType BackupType,
    DateTimeOffset SlotUtc);

public readonly record struct BackupPlanDueWork(
    BackupScheduleSlotKey Key,
    BackupScheduleSlotKey? DifferentialCoveredWhenFullSucceeds);

public static class BackupPlanSchedule
{
    public static bool VersionOwnsSlot(BackupPlan plan, Guid versionId, DateTimeOffset slotUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var slot = ConfigurationValues.RequireUtc(slotUtc, nameof(slotUtc));
        var version = Version(plan, versionId);
        if (slot <= version.EffectiveFromUtc)
        {
            return false;
        }

        var supersededAt = plan.SupersededAtUtc(versionId);
        return supersededAt is null || slot <= supersededAt.Value;
    }

    public static IReadOnlyList<BackupPlanDueWork> SelectDue(
        BackupPlan plan,
        DateTimeOffset nowUtc,
        IReadOnlyList<BackupPlanSlotCandidate> candidates,
        IReadOnlyDictionary<BackupScheduleSlotKey, BackupSlotDisposition> existing)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(existing);
        var now = ConfigurationValues.RequireUtc(nowUtc, nameof(nowUtc));
        if (plan.IsPaused)
        {
            return [];
        }

        BackupPlanSlotCandidate? latestFull = null;
        BackupPlanSlotCandidate? latestDifferential = null;
        var dueFulls = new List<BackupPlanSlotCandidate>();
        foreach (var candidate in candidates)
        {
            var version = Version(plan, candidate.PlanVersionId);
            ConfigurationValues.RequireDefined(candidate.BackupType, nameof(candidate.BackupType));
            var slot = ConfigurationValues.RequireUtc(candidate.SlotUtc, nameof(candidate.SlotUtc));
            if (candidate.BackupType is not (BackupType.Full or BackupType.Differential)
                || !BackupPlanRules.Includes(version.Mode, candidate.BackupType)
                || !VersionOwnsSlot(plan, version.Id, slot)
                || slot > now)
            {
                continue;
            }

            var normalized = candidate with { SlotUtc = slot };
            if (candidate.BackupType == BackupType.Full)
            {
                dueFulls.Add(normalized);
                latestFull = Later(latestFull, normalized);
            }
            else
            {
                latestDifferential = Later(latestDifferential, normalized);
            }
        }

        var work = new List<BackupPlanDueWork>(2);
        var sameInstant = latestFull is { } full
            && latestDifferential is { } differential
            && full.SlotUtc == differential.SlotUtc;
        if (latestFull is { } selectedFull)
        {
            var fullKey = Key(plan, selectedFull);
            if (!existing.ContainsKey(fullKey))
            {
                work.Add(new BackupPlanDueWork(
                    fullKey,
                    sameInstant ? Key(plan, latestDifferential!.Value) : null));
            }
        }

        if (latestDifferential is { } selectedDifferential)
        {
            var differentialKey = Key(plan, selectedDifferential);
            var mergedIntoNewFull = sameInstant
                && latestFull is { } mergedFull
                && !existing.ContainsKey(Key(plan, mergedFull));
            if (!existing.ContainsKey(differentialKey)
                && !mergedIntoNewFull
                && CoveringFullState(plan, selectedDifferential.SlotUtc, dueFulls, existing)
                    is not (CoveringFull.Covered or CoveringFull.Waiting))
            {
                work.Add(new BackupPlanDueWork(differentialKey, null));
            }
        }

        return work;
    }

    private static BackupPlanVersion Version(BackupPlan plan, Guid versionId)
    {
        return plan.Versions.SingleOrDefault(version => version.Id == versionId)
            ?? throw new ArgumentException("计划版本不存在。", nameof(versionId));
    }

    private static BackupPlanSlotCandidate Later(
        BackupPlanSlotCandidate? current,
        BackupPlanSlotCandidate candidate)
    {
        return current is null || candidate.SlotUtc > current.Value.SlotUtc
            ? candidate
            : current.Value;
    }

    private static BackupScheduleSlotKey Key(BackupPlan plan, BackupPlanSlotCandidate candidate)
    {
        return new BackupScheduleSlotKey(plan.Id, candidate.PlanVersionId, candidate.BackupType, candidate.SlotUtc);
    }

    private static CoveringFull CoveringFullState(
        BackupPlan plan,
        DateTimeOffset slotUtc,
        IReadOnlyList<BackupPlanSlotCandidate> dueFulls,
        IReadOnlyDictionary<BackupScheduleSlotKey, BackupSlotDisposition> existing)
    {
        BackupScheduleSlotKey? key = null;
        foreach (var full in dueFulls)
        {
            if (full.SlotUtc == slotUtc)
            {
                key = Key(plan, full);
                break;
            }
        }

        if (key is null)
        {
            foreach (var entry in existing)
            {
                if (entry.Key.PlanId == plan.Id
                    && entry.Key.BackupType == BackupType.Full
                    && entry.Key.SlotUtc == slotUtc)
                {
                    key = entry.Key;
                    break;
                }
            }
        }

        if (key is null || !existing.TryGetValue(key.Value, out var disposition))
        {
            return CoveringFull.None;
        }

        return disposition switch
        {
            BackupSlotDisposition.Succeeded => CoveringFull.Covered,
            BackupSlotDisposition.Pending => CoveringFull.Waiting,
            BackupSlotDisposition.Failed => CoveringFull.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(existing), disposition, "枚举值无效。"),
        };
    }

    private enum CoveringFull
    {
        None = 0,
        Waiting = 1,
        Covered = 2,
        Failed = 3
    }
}
