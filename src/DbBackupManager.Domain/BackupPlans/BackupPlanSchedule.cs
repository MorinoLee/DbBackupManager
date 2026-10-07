using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.BackupPlans;

public enum BackupSlotDisposition
{
    Pending = 1,
    Succeeded = 2,
    Failed = 3,
    Uncertain = 4
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
        if (latestFull is { } selectedFull && !existing.ContainsKey(Key(plan, selectedFull)))
        {
            BackupScheduleSlotKey? coveredDifferential = null;
            if (latestDifferential is { } selectedDifferential
                && selectedFull.SlotUtc >= selectedDifferential.SlotUtc)
            {
                var differentialKey = Key(plan, selectedDifferential);
                if (!existing.ContainsKey(differentialKey))
                {
                    coveredDifferential = differentialKey;
                }
            }

            work.Add(new BackupPlanDueWork(Key(plan, selectedFull), coveredDifferential));
        }

        if (latestDifferential is { } differential
            && !existing.ContainsKey(Key(plan, differential))
            && !IsDifferentialSuperseded(plan, differential.SlotUtc, latestFull, dueFulls, existing, now))
        {
            work.Add(new BackupPlanDueWork(Key(plan, differential), null));
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

    private static bool IsDifferentialSuperseded(
        BackupPlan plan,
        DateTimeOffset differentialSlot,
        BackupPlanSlotCandidate? latestFull,
        IReadOnlyList<BackupPlanSlotCandidate> dueFulls,
        IReadOnlyDictionary<BackupScheduleSlotKey, BackupSlotDisposition> existing,
        DateTimeOffset nowUtc)
    {
        foreach (var state in FullStatesNotEarlierThan(
            plan,
            differentialSlot,
            latestFull,
            dueFulls,
            existing,
            nowUtc))
        {
            if (state is not FullSlotState.Failed)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<FullSlotState> FullStatesNotEarlierThan(
        BackupPlan plan,
        DateTimeOffset differentialSlot,
        BackupPlanSlotCandidate? latestFull,
        IReadOnlyList<BackupPlanSlotCandidate> dueFulls,
        IReadOnlyDictionary<BackupScheduleSlotKey, BackupSlotDisposition> existing,
        DateTimeOffset nowUtc)
    {
        var latestFullKey = latestFull is { } selected ? Key(plan, selected) : (BackupScheduleSlotKey?)null;
        var seen = new HashSet<BackupScheduleSlotKey>();
        foreach (var full in dueFulls)
        {
            if (full.SlotUtc < differentialSlot)
            {
                continue;
            }

            var key = Key(plan, full);
            if (!seen.Add(key))
            {
                continue;
            }

            if (existing.TryGetValue(key, out var disposition))
            {
                yield return ToState(disposition);
            }
            else if (latestFullKey == key)
            {
                yield return FullSlotState.NotCreated;
            }
        }

        foreach (var entry in existing)
        {
            if (entry.Key.PlanId != plan.Id
                || entry.Key.BackupType != BackupType.Full
                || entry.Key.SlotUtc < differentialSlot
                || entry.Key.SlotUtc > nowUtc
                || !seen.Add(entry.Key))
            {
                continue;
            }

            yield return ToState(entry.Value);
        }
    }

    private static FullSlotState ToState(BackupSlotDisposition disposition)
    {
        return disposition switch
        {
            BackupSlotDisposition.Pending => FullSlotState.Pending,
            BackupSlotDisposition.Uncertain => FullSlotState.Uncertain,
            BackupSlotDisposition.Succeeded => FullSlotState.Succeeded,
            BackupSlotDisposition.Failed => FullSlotState.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "枚举值无效。"),
        };
    }

    private enum FullSlotState
    {
        NotCreated = 0,
        Pending = 1,
        Uncertain = 2,
        Succeeded = 3,
        Failed = 4
    }
}
