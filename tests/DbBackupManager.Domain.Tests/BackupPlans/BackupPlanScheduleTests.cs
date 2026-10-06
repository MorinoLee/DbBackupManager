using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupPlans;

public sealed class BackupPlanScheduleTests
{
    private static readonly DateTimeOffset EffectiveFrom = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid PlanId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FirstVersionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SecondVersionId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void DowntimeKeepsOnlyTheLatestDueSlotOfEachType()
    {
        var plan = ChainPlan();
        var now = Slot(5);
        var work = BackupPlanSchedule.SelectDue(
            plan,
            now,
            [
                Candidate(BackupType.Full, Slot(1)),
                Candidate(BackupType.Full, Slot(3)),
                Candidate(BackupType.Differential, Slot(2)),
                Candidate(BackupType.Differential, Slot(4)),
            ],
            Empty());

        Assert.Equal(2, work.Count);
        Assert.Equal(Slot(3), work[0].Key.SlotUtc);
        Assert.Equal(BackupType.Full, work[0].Key.BackupType);
        Assert.Null(work[0].DifferentialCoveredWhenFullSucceeds);
        Assert.Equal(Slot(4), work[1].Key.SlotUtc);
        Assert.Equal(BackupType.Differential, work[1].Key.BackupType);
    }

    [Fact]
    public void SameInstantFullAndDifferentialMergeUntilTheFullSucceeds()
    {
        var plan = ChainPlan();
        var instant = Slot(2);
        var candidates = new[]
        {
            Candidate(BackupType.Full, instant),
            Candidate(BackupType.Differential, instant),
            Candidate(BackupType.Full, Slot(1)),
        };

        var merged = BackupPlanSchedule.SelectDue(plan, Slot(3), candidates, Empty());
        var pending = BackupPlanSchedule.SelectDue(
            plan,
            Slot(3),
            candidates,
            Outcome(merged[0].Key, BackupSlotDisposition.Pending));
        var succeeded = BackupPlanSchedule.SelectDue(
            plan,
            Slot(3),
            candidates,
            Outcome(merged[0].Key, BackupSlotDisposition.Succeeded));
        var failed = BackupPlanSchedule.SelectDue(
            plan,
            Slot(3),
            candidates,
            Outcome(merged[0].Key, BackupSlotDisposition.Failed));

        Assert.Single(merged);
        Assert.Equal(BackupType.Full, merged[0].Key.BackupType);
        var covered = Assert.NotNull(merged[0].DifferentialCoveredWhenFullSucceeds);
        Assert.Equal(instant, covered.SlotUtc);
        Assert.Equal(BackupType.Differential, covered.BackupType);
        Assert.Empty(pending);
        Assert.Empty(succeeded);
        Assert.Single(failed);
        Assert.Equal(BackupType.Differential, failed[0].Key.BackupType);
        Assert.Null(failed[0].DifferentialCoveredWhenFullSucceeds);
    }

    [Fact]
    public void PausedPlanSelectsNothing()
    {
        var plan = ChainPlan();
        plan.Pause();

        var work = BackupPlanSchedule.SelectDue(
            plan,
            Slot(2),
            [Candidate(BackupType.Full, Slot(1))],
            Empty());

        Assert.Empty(work);
    }

    [Fact]
    public void SlotAtTheVersionBoundaryStaysWithTheEarlierVersion()
    {
        var plan = ChainPlan();
        var revisedAt = Slot(2);
        plan.Revise(SecondVersionId, ChainDefinition(recoveryWindowDays: 21), revisedAt);

        Assert.True(BackupPlanSchedule.VersionOwnsSlot(plan, FirstVersionId, revisedAt));
        Assert.False(BackupPlanSchedule.VersionOwnsSlot(plan, SecondVersionId, revisedAt));
        Assert.True(BackupPlanSchedule.VersionOwnsSlot(plan, SecondVersionId, revisedAt.AddMinutes(1)));

        var work = BackupPlanSchedule.SelectDue(
            plan,
            revisedAt.AddHours(1),
            [
                new BackupPlanSlotCandidate(FirstVersionId, BackupType.Full, revisedAt),
                new BackupPlanSlotCandidate(SecondVersionId, BackupType.Full, revisedAt),
                new BackupPlanSlotCandidate(SecondVersionId, BackupType.Full, revisedAt.AddMinutes(30)),
            ],
            Empty());

        Assert.Single(work);
        Assert.Equal(SecondVersionId, work[0].Key.PlanVersionId);
        Assert.Equal(revisedAt.AddMinutes(30), work[0].Key.SlotUtc);
    }

    [Fact]
    public void FullOnlyPlanIgnoresDifferentialCandidates()
    {
        var plan = BackupPlan.Create(
            PlanId,
            Guid.NewGuid(),
            "全库",
            FirstVersionId,
            new BackupPlanDefinition(
                BackupPlanMode.Full,
                new RecurringBackupSchedule(BackupScheduleType.Daily, new TimeOnly(2, 0), BackupWeekdays.None),
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
                transferTimeoutMinutes: 60),
            EffectiveFrom);
        var work = BackupPlanSchedule.SelectDue(
            plan,
            Slot(2),
            [
                Candidate(BackupType.Full, Slot(1)),
                Candidate(BackupType.Differential, Slot(1)),
                Candidate(BackupType.Log, Slot(1)),
            ],
            Empty());

        Assert.Single(work);
        Assert.Equal(BackupType.Full, work[0].Key.BackupType);
    }

    private static BackupPlan ChainPlan()
    {
        return BackupPlan.Create(
            PlanId,
            Guid.NewGuid(),
            "链式",
            FirstVersionId,
            ChainDefinition(),
            EffectiveFrom);
    }

    private static BackupPlanDefinition ChainDefinition(int recoveryWindowDays = 14)
    {
        return new BackupPlanDefinition(
            BackupPlanMode.FullAndDifferential,
            new RecurringBackupSchedule(BackupScheduleType.Weekly, new TimeOnly(2, 0), BackupWeekdays.Sunday),
            new RecurringBackupSchedule(
                BackupScheduleType.Weekly,
                new TimeOnly(2, 0),
                BackupWeekdays.Monday
                    | BackupWeekdays.Tuesday
                    | BackupWeekdays.Wednesday
                    | BackupWeekdays.Thursday
                    | BackupWeekdays.Friday
                    | BackupWeekdays.Saturday),
            logSchedule: null,
            "UTC",
            BackupStorageMode.LocalOnly,
            storageTargetId: null,
            recoveryWindowDays,
            useChecksum: true,
            useCompression: false,
            backupTimeoutMinutes: 120,
            verifyTimeoutMinutes: 60,
            transferTimeoutMinutes: 60);
    }

    private static BackupPlanSlotCandidate Candidate(BackupType backupType, DateTimeOffset slot)
    {
        return new BackupPlanSlotCandidate(FirstVersionId, backupType, slot);
    }

    private static DateTimeOffset Slot(int day)
    {
        return EffectiveFrom.AddDays(day);
    }

    private static Dictionary<BackupScheduleSlotKey, BackupSlotDisposition> Empty()
    {
        return [];
    }

    private static Dictionary<BackupScheduleSlotKey, BackupSlotDisposition> Outcome(
        BackupScheduleSlotKey key,
        BackupSlotDisposition disposition)
    {
        return new Dictionary<BackupScheduleSlotKey, BackupSlotDisposition> { [key] = disposition };
    }
}
