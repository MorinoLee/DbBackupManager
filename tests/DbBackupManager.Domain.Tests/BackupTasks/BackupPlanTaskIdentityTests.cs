using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupTasks;

public sealed class BackupPlanTaskIdentityTests
{
    private static readonly DateTimeOffset Slot = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(BackupRunPurpose.PlanFull, BackupType.Full, false)]
    [InlineData(BackupRunPurpose.PlanDifferential, BackupType.Differential, false)]
    [InlineData(BackupRunPurpose.PlanLog, BackupType.Log, false)]
    [InlineData(BackupRunPurpose.AdHocCopyOnlyFull, BackupType.Full, true)]
    public void PlanPurposeDeterminesImmutableTypeAndCopyOnly(BackupRunPurpose purpose, BackupType type, bool copyOnly)
    {
        var plan = Plan();
        var task = BackupTask.ForPlan(Guid.NewGuid(), plan.Id, plan.CurrentVersionId, purpose,
            BackupTaskTriggerType.Manual, null);
        var snapshot = Snapshot(task, plan, purpose);
        Assert.Null(task.PolicyId);
        Assert.Equal(plan.Id, task.PlanId);
        Assert.Equal(plan.CurrentVersionId, task.PlanVersionId);
        Assert.Equal(type, task.BackupType);
        Assert.Equal(type, snapshot.BackupType);
        Assert.Equal(purpose, snapshot.Purpose);
        Assert.Equal(copyOnly, snapshot.UseCopyOnly);
        Assert.Equal(14, snapshot.LocalRetentionDays);
        Assert.Equal("v3", snapshot.FileNameRuleVersion);
        plan.Revise(Guid.NewGuid(), Definition(28), Slot.AddHours(1));
        Assert.Equal(14, snapshot.LocalRetentionDays);
        Assert.NotEqual(plan.CurrentVersionId, task.PlanVersionId);
    }

    [Fact]
    public void LegacySnapshotDoesNotInferPlanPurposeFromCopyOnly()
    {
        var task = new BackupTask(Guid.NewGuid(), Guid.NewGuid(), BackupTaskTriggerType.Manual, null);
        var snapshot = new BackupTaskSnapshot(task.Id, "旧策略", Identity(), Sql(), Source("v2"),
            new(BackupStorageMode.LocalOnly, null, 7, null, true, false, true, 120, 60, 60, "UTC"));
        Assert.NotNull(task.PolicyId);
        Assert.Null(task.PlanId);
        Assert.Null(task.PlanVersionId);
        Assert.Equal(BackupType.Full, task.BackupType);
        Assert.Null(snapshot.Purpose);
        Assert.True(snapshot.UseCopyOnly);
        Assert.Equal("v2", snapshot.FileNameRuleVersion);
    }

    [Theory]
    [InlineData(BackupRunPurpose.PlanDifferential)]
    [InlineData(BackupRunPurpose.PlanLog)]
    [InlineData(BackupRunPurpose.AdHocCopyOnlyFull)]
    public void OnlyOrdinaryPlanFullMayCoverDifferentialSlot(BackupRunPurpose purpose) =>
        Assert.Throws<ArgumentException>(() => BackupTask.ForPlan(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            purpose, BackupTaskTriggerType.Manual, null, Slot));

    [Fact]
    public void CoveredSlotIsPreservedAndRequiresUtc()
    {
        var task = BackupTask.ForPlan(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), BackupRunPurpose.PlanFull,
            BackupTaskTriggerType.Scheduled, Slot, Slot.AddMinutes(-30));
        Assert.Equal(Slot.AddMinutes(-30), task.CoveredDifferentialSlotUtc);
        Assert.Throws<ArgumentException>(() => BackupTask.ForPlan(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            BackupRunPurpose.PlanFull, BackupTaskTriggerType.Manual, null, Slot.ToOffset(TimeSpan.FromHours(8))));
    }

    [Fact]
    public void InvalidIdentitiesTriggersPurposesAndSnapshotVersionAreRejected()
    {
        var plan = Plan();
        Assert.Throws<ArgumentException>(() => BackupTask.ForPlan(Guid.Empty, plan.Id, plan.CurrentVersionId,
            BackupRunPurpose.PlanFull, BackupTaskTriggerType.Manual, null));
        Assert.Throws<ArgumentException>(() => BackupTask.ForPlan(Guid.NewGuid(), Guid.Empty, plan.CurrentVersionId,
            BackupRunPurpose.PlanFull, BackupTaskTriggerType.Manual, null));
        Assert.Throws<ArgumentException>(() => BackupTask.ForPlan(Guid.NewGuid(), plan.Id, Guid.Empty,
            BackupRunPurpose.PlanFull, BackupTaskTriggerType.Manual, null));
        Assert.Throws<ArgumentException>(() => BackupTask.ForPlan(Guid.NewGuid(), plan.Id, plan.CurrentVersionId,
            BackupRunPurpose.AdHocCopyOnlyFull, BackupTaskTriggerType.Scheduled, Slot));
        Assert.Throws<ArgumentException>(() => BackupTask.ForPlan(Guid.NewGuid(), plan.Id, plan.CurrentVersionId,
            BackupRunPurpose.PlanFull, BackupTaskTriggerType.Scheduled, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => BackupTask.ForPlan(Guid.NewGuid(), plan.Id, plan.CurrentVersionId,
            (BackupRunPurpose)99, BackupTaskTriggerType.Manual, null));
        var task = BackupTask.ForPlan(Guid.NewGuid(), plan.Id, plan.CurrentVersionId, BackupRunPurpose.PlanFull,
            BackupTaskTriggerType.Manual, null);
        Assert.Throws<ArgumentException>(() => Snapshot(task, Plan(), BackupRunPurpose.PlanFull));
        Assert.Throws<ArgumentException>(() => Snapshot(task, plan, BackupRunPurpose.PlanDifferential));
        Assert.Throws<ArgumentException>(() => BackupTaskSnapshot.ForPlan(task, plan.CurrentVersion, plan.Name,
            BackupRunPurpose.PlanFull, Identity(), Sql(), Source("v2")));
    }

    private static BackupTaskSnapshot Snapshot(BackupTask task, BackupPlan plan, BackupRunPurpose purpose) =>
        BackupTaskSnapshot.ForPlan(task, plan.CurrentVersion, plan.Name, purpose, Identity(), Sql(), Source("v3"));
    private static BackupTaskIdentitySnapshot Identity() => new(Guid.NewGuid(), "合成服务器", Guid.NewGuid(),
        "合成实例", Guid.NewGuid(), "SyntheticDatabase");
    private static BackupSqlTargetSnapshot Sql() => new("synthetic-sql", Guid.NewGuid(), true, false, null, 30);
    private static BackupSourceSnapshot Source(string version) => new(@"D:\Synthetic\Backup", version,
        new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", Guid.NewGuid(), null));
    private static BackupPlan Plan() => BackupPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "合成计划", Guid.NewGuid(), Definition(14), Slot);
    private static BackupPlanDefinition Definition(int days) => new(BackupPlanMode.FullAndDifferentialAndLog,
        new(BackupScheduleType.Daily, new(2, 0), BackupWeekdays.None),
        new(BackupScheduleType.Daily, new(3, 0), BackupWeekdays.None), new(15), "UTC", BackupStorageMode.LocalOnly,
        null, days, null, true, false, 120, 60, 60);
}
