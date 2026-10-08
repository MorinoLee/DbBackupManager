using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.BackupTasks;

public static class BackupPlanTaskCreationRules
{
    public static BackupPlanTaskCreationCode? Validate(CreateBackupPlanTaskCommand command, BackupPlan plan)
    {
        var request = command.Request;
        if (request.Purpose == BackupRunPurpose.PlanLog) return BackupPlanTaskCreationCode.StageNotOpen;
        var version = plan.Versions.SingleOrDefault(x => x.Id == request.PlanVersionId);
        if (plan.Id != request.PlanId || version is null) return BackupPlanTaskCreationCode.VersionConflict;
        if (command.TriggerType == BackupTaskTriggerType.Manual && plan.CurrentVersionId != version.Id)
            return BackupPlanTaskCreationCode.VersionConflict;
        if (request.Purpose != BackupRunPurpose.AdHocCopyOnlyFull
            && !BackupPlanRules.Includes(version.Mode, BackupPlanRules.ToBackupType(request.Purpose)))
            return BackupPlanTaskCreationCode.PurposeNotInMode;
        if (request.CoveredDifferentialSlotUtc is { } covered
            && (request.Purpose != BackupRunPurpose.PlanFull || covered.Offset != TimeSpan.Zero))
            return BackupPlanTaskCreationCode.InvalidSlot;
        if (command.TriggerType == BackupTaskTriggerType.Manual)
            return request.ScheduledSlotAtUtc is null ? null : BackupPlanTaskCreationCode.InvalidSlot;
        if (request.Purpose == BackupRunPurpose.AdHocCopyOnlyFull) return BackupPlanTaskCreationCode.InvalidRequest;
        if (plan.IsPaused) return BackupPlanTaskCreationCode.PlanPaused;
        if (request.ScheduledSlotAtUtc is not { } slot || slot.Offset != TimeSpan.Zero || slot > command.NowUtc
            || request.CoveredDifferentialSlotUtc > command.NowUtc
            || !BackupPlanSchedule.VersionOwnsSlot(plan, version.Id, slot))
            return BackupPlanTaskCreationCode.InvalidSlot;
        var schedule = request.Purpose == BackupRunPurpose.PlanFull ? version.FullSchedule : version.DifferentialSchedule!.Value;
        var calculation = BackupScheduleCalculator.Calculate(new(schedule.ScheduleType, schedule.LocalTime,
            schedule.DaysOfWeek, version.TimeZoneId, version.EffectiveFromUtc, slot));
        return calculation.Status == BackupScheduleCalculationStatus.Succeeded && calculation.WindowSlots.Contains(slot)
            ? null : BackupPlanTaskCreationCode.InvalidSlot;
    }
}
