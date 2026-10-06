using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupPlans;

public sealed class BackupPlanTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 6, 2, 0, 0, TimeSpan.Zero);
    private static readonly Guid DatabaseId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid PlanId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid FirstVersionId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid SecondVersionId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid ThirdVersionId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    [Fact]
    public void VersionHistoryStaysReadOnlyWhileRevisionsRemainVisible()
    {
        var plan = CreatePlan();
        var history = plan.Versions;

        Assert.IsNotType<List<BackupPlanVersion>>(history);
        Assert.Throws<InvalidCastException>(() => (List<BackupPlanVersion>)history);

        plan.Revise(SecondVersionId, FullDefinition(recoveryWindowDays: 30), CreatedAt.AddHours(1));

        Assert.Same(history, plan.Versions);
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void CreateBindsOneDatabaseToItsFirstVersion()
    {
        var plan = CreatePlan();

        Assert.Equal(DatabaseId, plan.DatabaseId);
        Assert.Equal("每晚全库", plan.Name);
        Assert.Equal("每晚全库".ToUpperInvariant(), plan.NormalizedName);
        Assert.False(plan.IsPaused);
        Assert.Equal(FirstVersionId, plan.CurrentVersionId);
        Assert.Equal(1, plan.CurrentVersion.Number);
        Assert.Equal(BackupPlanMode.Full, plan.CurrentVersion.Mode);
        Assert.Equal(CreatedAt, plan.CurrentVersion.EffectiveFromUtc);
        Assert.Null(plan.SupersededAtUtc(FirstVersionId));
    }

    [Fact]
    public void PauseKeepsTheDatabaseOccupied()
    {
        var index = new CurrentBackupPlanIndex();
        var plan = CreatePlan();
        index.Add(plan);

        plan.Pause();
        plan.Pause();

        Assert.True(plan.IsPaused);
        Assert.Equal(DatabaseId, plan.DatabaseId);
        Assert.Equal(FirstVersionId, plan.CurrentVersionId);
        Assert.True(index.Occupies(DatabaseId));
        Assert.Throws<InvalidOperationException>(() => index.Add(CreatePlan(planId: Guid.NewGuid(), versionId: Guid.NewGuid())));
    }

    [Fact]
    public void DifferentDatabasesCanEachHaveACurrentPlan()
    {
        var index = new CurrentBackupPlanIndex();
        index.Add(CreatePlan());
        index.Add(CreatePlan(databaseId: Guid.NewGuid(), planId: Guid.NewGuid(), versionId: Guid.NewGuid()));

        Assert.True(index.Occupies(DatabaseId));
    }

    [Fact]
    public void ResumeDoesNotCreateAVersion()
    {
        var plan = CreatePlan();
        plan.Pause();

        plan.Resume();
        plan.Resume();

        Assert.False(plan.IsPaused);
        Assert.Single(plan.Versions);
        Assert.Equal(FirstVersionId, plan.CurrentVersionId);
    }

    [Fact]
    public void RenameDoesNotCreateAVersion()
    {
        var plan = CreatePlan();

        plan.Rename("  周末全库  ");

        Assert.Equal("周末全库", plan.Name);
        Assert.Single(plan.Versions);
        Assert.Equal(CreatedAt, plan.CurrentVersion.EffectiveFromUtc);
    }

    [Fact]
    public void RevisionLeavesThePreviousVersionUnchanged()
    {
        var plan = CreatePlan();
        var original = plan.CurrentVersion;
        var revisedAt = CreatedAt.AddHours(2);

        var created = plan.Revise(SecondVersionId, FullDefinition(recoveryWindowDays: 30), revisedAt);

        Assert.True(created);
        Assert.Equal(BackupPlanMode.Full, original.Mode);
        Assert.Equal(14, original.RecoveryWindowDays);
        Assert.Equal(CreatedAt, original.EffectiveFromUtc);
        Assert.Equal(1, original.Number);
        Assert.Equal(SecondVersionId, plan.CurrentVersionId);
        Assert.Equal(2, plan.CurrentVersion.Number);
        Assert.Equal(30, plan.CurrentVersion.RecoveryWindowDays);
        Assert.Equal(revisedAt, plan.SupersededAtUtc(FirstVersionId));
        Assert.Null(plan.SupersededAtUtc(SecondVersionId));
    }

    [Fact]
    public void UnchangedRevisionDoesNotMoveTheEffectiveBoundary()
    {
        var plan = CreatePlan();

        var created = plan.Revise(SecondVersionId, FullDefinition(), CreatedAt.AddMinutes(1));

        Assert.False(created);
        Assert.Equal(FirstVersionId, plan.CurrentVersionId);
        Assert.Single(plan.Versions);
    }

    [Fact]
    public void RevisionAtTheCurrentEffectiveTimeIsRejected()
    {
        var plan = CreatePlan();

        Assert.Throws<ArgumentException>(() => plan.Revise(SecondVersionId, FullDefinition(recoveryWindowDays: 30), CreatedAt));
        Assert.Equal(FirstVersionId, plan.CurrentVersionId);
    }

    [Fact]
    public void EarlierVersionCutoffStaysOnItsSuccessor()
    {
        var plan = CreatePlan();
        var secondAt = CreatedAt.AddHours(1);
        var thirdAt = CreatedAt.AddHours(3);
        plan.Revise(SecondVersionId, FullDefinition(recoveryWindowDays: 21), secondAt);
        plan.Revise(ThirdVersionId, ChainDefinition(), thirdAt);

        Assert.Equal(secondAt, plan.SupersededAtUtc(FirstVersionId));
        Assert.Equal(thirdAt, plan.SupersededAtUtc(SecondVersionId));
        Assert.Equal(BackupPlanMode.Full, plan.Versions.Single(version => version.Id == FirstVersionId).Mode);
        Assert.Equal(BackupPlanMode.FullAndDifferentialAndLog, plan.CurrentVersion.Mode);
        Assert.Equal(15, plan.CurrentVersion.LogSchedule!.Value.IntervalMinutes);
    }

    [Fact]
    public void FullPlanRejectsDifferentialAndLogSchedules()
    {
        Assert.Throws<ArgumentException>(() => FullDefinition(differential: WeekdayDifferential()));
        Assert.Throws<ArgumentException>(() => FullDefinition(log: new LogBackupSchedule(15)));
    }

    [Fact]
    public void ChainPlanRequiresTheSchedulesForItsMode()
    {
        Assert.Throws<ArgumentException>(() => new BackupPlanDefinition(
            BackupPlanMode.FullAndDifferential,
            NightlyFull(),
            differentialSchedule: null,
            logSchedule: null,
            "UTC",
            BackupStorageMode.LocalOnly,
            storageTargetId: null,
            recoveryWindowDays: 14,
            useChecksum: true,
            useCompression: false,
            backupTimeoutMinutes: 120,
            verifyTimeoutMinutes: 60,
            transferTimeoutMinutes: 60));
    }

    [Fact]
    public void LocalPlanRejectsARemoteTarget()
    {
        Assert.Throws<ArgumentException>(() => FullDefinition(storageTargetId: Guid.NewGuid()));
    }

    private static BackupPlan CreatePlan(
        Guid? databaseId = null,
        Guid? planId = null,
        Guid? versionId = null)
    {
        return BackupPlan.Create(
            planId ?? PlanId,
            databaseId ?? DatabaseId,
            "每晚全库",
            versionId ?? FirstVersionId,
            FullDefinition(),
            CreatedAt);
    }

    private static BackupPlanDefinition FullDefinition(
        int recoveryWindowDays = 14,
        RecurringBackupSchedule? differential = null,
        LogBackupSchedule? log = null,
        Guid? storageTargetId = null)
    {
        return new BackupPlanDefinition(
            BackupPlanMode.Full,
            NightlyFull(),
            differential,
            log,
            "UTC",
            BackupStorageMode.LocalOnly,
            storageTargetId,
            recoveryWindowDays,
            useChecksum: true,
            useCompression: false,
            backupTimeoutMinutes: 120,
            verifyTimeoutMinutes: 60,
            transferTimeoutMinutes: 60);
    }

    private static BackupPlanDefinition ChainDefinition()
    {
        return new BackupPlanDefinition(
            BackupPlanMode.FullAndDifferentialAndLog,
            new RecurringBackupSchedule(BackupScheduleType.Weekly, new TimeOnly(2, 0), BackupWeekdays.Sunday),
            WeekdayDifferential(),
            new LogBackupSchedule(15),
            "UTC",
            BackupStorageMode.LocalOnly,
            storageTargetId: null,
            recoveryWindowDays: 14,
            useChecksum: true,
            useCompression: true,
            backupTimeoutMinutes: 120,
            verifyTimeoutMinutes: 60,
            transferTimeoutMinutes: 60);
    }

    private static RecurringBackupSchedule NightlyFull()
    {
        return new RecurringBackupSchedule(BackupScheduleType.Daily, new TimeOnly(2, 0), BackupWeekdays.None);
    }

    private static RecurringBackupSchedule WeekdayDifferential()
    {
        return new RecurringBackupSchedule(
            BackupScheduleType.Weekly,
            new TimeOnly(2, 0),
            BackupWeekdays.Monday
                | BackupWeekdays.Tuesday
                | BackupWeekdays.Wednesday
                | BackupWeekdays.Thursday
                | BackupWeekdays.Friday
                | BackupWeekdays.Saturday);
    }
}
