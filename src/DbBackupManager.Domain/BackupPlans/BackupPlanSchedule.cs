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
            var hasFull = existing.TryGetValue(fullKey, out var fullDisposition);
            if (!hasFull)
            {
                work.Add(new BackupPlanDueWork(
                    fullKey,
                    sameInstant ? Key(plan, latestDifferential!.Value) : null));
            }
            else if (sameInstant
                && fullDisposition == BackupSlotDisposition.Failed
                && !existing.ContainsKey(Key(plan, latestDifferential!.Value)))
            {
                work.Add(new BackupPlanDueWork(Key(plan, latestDifferential!.Value), null));
            }
        }

        if (!sameInstant && latestDifferential is { } selectedDifferential)
        {
            var differentialKey = Key(plan, selectedDifferential);
            if (!existing.ContainsKey(differentialKey))
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
}
