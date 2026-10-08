using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

public sealed partial class PlatformDbContext
{
    private void PrepareTaskIdentityEntries()
    {
        var tasks = ChangeTracker.Entries<BackupTask>().ToArray();
        var snapshots = ChangeTracker.Entries<BackupTaskSnapshot>()
            .Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToArray();
        foreach (var entry in tasks)
        {
            if (entry.State == EntityState.Modified)
                foreach (var name in new[] { nameof(BackupTask.PolicyId), nameof(BackupTask.PlanId),
                    nameof(BackupTask.PlanVersionId), nameof(BackupTask.BackupType), nameof(BackupTask.TriggerType),
                    nameof(BackupTask.ScheduledSlotAtUtc), nameof(BackupTask.CoveredDifferentialSlotUtc) })
                    if (entry.Property(name).IsModified
                        && !Equals(entry.Property(name).OriginalValue, entry.Property(name).CurrentValue))
                        throw new InvalidOperationException("任务身份、类型与时隙创建后不能修改。");
            if (entry.State == EntityState.Added && entry.Entity.PlanId is not null
                && !snapshots.Any(x => x.TaskId == entry.Entity.Id))
                throw new InvalidOperationException("新增计划任务必须在同一次提交中保存用途快照。");
        }
        foreach (var snapshot in snapshots)
        {
            var task = tasks.Select(x => x.Entity).SingleOrDefault(x => x.Id == snapshot.TaskId)
                ?? BackupTasks.AsNoTracking().SingleOrDefault(x => x.Id == snapshot.TaskId);
            if (task is null) continue; // 不存在的任务由 Restrict 外键拒绝。
            if (task.BackupType != snapshot.BackupType
                || (task.PlanId is not null) != (snapshot.Purpose is not null))
                throw new InvalidOperationException("任务与快照的身份来源、类型或用途不一致。");
            if (snapshot.Purpose is not { } purpose) continue;
            if (snapshot.BackupType != BackupPlanRules.ToBackupType(purpose)
                || snapshot.UseCopyOnly != BackupPlanRules.UseCopyOnly(purpose)
                || snapshot.FileNameRuleVersion != BackupTaskPathFactory.PlanVersion
                || purpose == BackupRunPurpose.AdHocCopyOnlyFull && task.TriggerType != BackupTaskTriggerType.Manual
                || task.CoveredDifferentialSlotUtc is not null && purpose != BackupRunPurpose.PlanFull)
                throw new InvalidOperationException("计划用途与快照选项、触发方式或取代时隙不一致。");
            var plan = BackupPlans.Local.SingleOrDefault(x => x.Id == task.PlanId)
                ?? BackupPlans.AsNoTracking().SingleOrDefault(x => x.Id == task.PlanId);
            if (plan is not null && plan.DatabaseId != snapshot.DatabaseId)
                throw new InvalidOperationException("任务数据库身份与计划不一致。");
        }
    }
}
