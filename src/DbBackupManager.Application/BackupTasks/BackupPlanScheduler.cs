using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.BackupTasks;

public enum BackupPlanSchedulingCode
{
    Created = 1,
    NoWork = 2,
    Paused = 3,
    NotFound = 4,
    InvalidTimeZone = 5,
    ConfigurationUnavailable = 6
}

public sealed record BackupPlanSchedulingResult(Guid PlanId, BackupPlanSchedulingCode Code,
    IReadOnlyList<CreatedBackupPlanTask> Tasks);

/// <summary>每个计划在短事务内重新读取并选择；不接受调用方预先算好的决定。</summary>
public interface IBackupPlanSchedulingStore
{
    Task<IReadOnlyList<Guid>> ReadPlanIdsAsync(CancellationToken cancellationToken = default);
    Task<BackupPlanSchedulingResult> ScheduleAsync(Guid planId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>显式调用的单轮调度，不注册后台循环或执行入口。</summary>
public sealed class BackupPlanScheduler(IBackupPlanSchedulingStore store, TimeProvider clock)
{
    public async Task<IReadOnlyList<BackupPlanSchedulingResult>> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().ToUniversalTime();
        var ids = await store.ReadPlanIdsAsync(cancellationToken);
        var results = new List<BackupPlanSchedulingResult>();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await store.ScheduleAsync(id, now, cancellationToken));
        }
        return results;
    }
}

public sealed record BackupPlanScheduleSelection(BackupScheduleCalculationStatus Status,
    IReadOnlyList<BackupPlanDueWork> Work);

public static class BackupPlanScheduleSelectionRules
{
    public static BackupPlanScheduleSelection Select(BackupPlan plan, DateTimeOffset nowUtc,
        IReadOnlyDictionary<BackupScheduleSlotKey, BackupSlotDisposition> existing)
    {
        var candidates = new List<BackupPlanSlotCandidate>();
        foreach (var version in plan.Versions)
        {
            // 旧版本以其结束边界为观察时刻，停机跨版本也不会漏掉最后一个合法时隙。
            var upper = plan.SupersededAtUtc(version.Id);
            var asOf = upper is { } end && end < nowUtc ? end : nowUtc;
            if (asOf <= version.EffectiveFromUtc) continue;
            if (!Add(version.FullSchedule, BackupType.Full)) return new(BackupScheduleCalculationStatus.InvalidTimeZone, []);
            if (version.DifferentialSchedule is { } diff && !Add(diff, BackupType.Differential))
                return new(BackupScheduleCalculationStatus.InvalidTimeZone, []);

            bool Add(RecurringBackupSchedule schedule, BackupType type)
            {
                var result = BackupScheduleCalculator.Calculate(new(schedule.ScheduleType, schedule.LocalTime,
                    schedule.DaysOfWeek, version.TimeZoneId, version.EffectiveFromUtc, asOf));
                if (result.Status != BackupScheduleCalculationStatus.Succeeded) return false;
                foreach (var slot in result.WindowSlots.Where(x => x <= asOf))
                {
                    var utc = slot.ToUniversalTime();
                    if (BackupPlanSchedule.VersionOwnsSlot(plan, version.Id, utc)) candidates.Add(new(version.Id, type, utc));
                }
                return true;
            }
        }
        return new(BackupScheduleCalculationStatus.Succeeded, BackupPlanSchedule.SelectDue(plan, nowUtc, candidates, existing));
    }
}

public sealed record BackupPlanAttemptFacts(Guid AttemptId, BackupInvocationStatus SqlStatus,
    bool MetadataPassed, bool LocalVerified, bool ContradictoryEvidence = false);

public static class BackupPlanSlotDispositionRules
{
    public static BackupSlotDisposition Evaluate(BackupTaskStatus status, Guid? currentAttemptId,
        IReadOnlyList<BackupPlanAttemptFacts> attempts, bool confirmedBeforeBackupFailure = false, BackupTaskStage? currentStage = null)
    {
        if (status == BackupTaskStatus.NeedsAttention
            && currentStage is not (BackupTaskStage.Transfer or BackupTaskStage.ValidateCopy or BackupTaskStage.Cleanup))
            return BackupSlotDisposition.Uncertain;
        if (currentAttemptId is null)
        {
            if (status == BackupTaskStatus.Failed && attempts.Count == 0 && confirmedBeforeBackupFailure)
                return BackupSlotDisposition.Failed;
            return status == BackupTaskStatus.Pending && attempts.Count == 0
                ? BackupSlotDisposition.Pending : BackupSlotDisposition.Uncertain;
        }
        var matching = attempts.Where(x => x.AttemptId == currentAttemptId).ToArray();
        if (matching.Length != 1) return BackupSlotDisposition.Uncertain;
        var current = matching[0];
        if (current.ContradictoryEvidence || current.SqlStatus != BackupInvocationStatus.Succeeded
                && (current.MetadataPassed || current.LocalVerified)) return BackupSlotDisposition.Uncertain;
        // 不能把不同 Attempt 的 SQL、核对和本地校验拼成一次成功。
        if (current.SqlStatus == BackupInvocationStatus.Succeeded && current.MetadataPassed && current.LocalVerified)
            return BackupSlotDisposition.Succeeded;
        if (status == BackupTaskStatus.NeedsAttention) return BackupSlotDisposition.Uncertain;
        if (status == BackupTaskStatus.Pending) return BackupSlotDisposition.Pending;
        // 后续失败不能抹掉历史成功或矛盾证据；Prepared 也不证明该 Attempt 明确失败。
        if (status == BackupTaskStatus.Failed && attempts.All(x => x.SqlStatus == BackupInvocationStatus.ConfirmedFailed
                && !x.ContradictoryEvidence && !x.MetadataPassed && !x.LocalVerified))
            return BackupSlotDisposition.Failed;
        return status is BackupTaskStatus.Pending or BackupTaskStatus.Running
            && current.SqlStatus is BackupInvocationStatus.Prepared or BackupInvocationStatus.Running
            ? BackupSlotDisposition.Pending : BackupSlotDisposition.Uncertain;
    }
}
