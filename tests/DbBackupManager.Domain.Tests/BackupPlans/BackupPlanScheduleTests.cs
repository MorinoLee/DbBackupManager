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
    public void SucceededFullCoverageSurvivesALaterFullSlot()
    {
        var plan = ChainPlan();
        var friday = new DateTimeOffset(2026, 10, 2, 2, 0, 0, TimeSpan.Zero);
        var saturday = new DateTimeOffset(2026, 10, 3, 2, 0, 0, TimeSpan.Zero);
        var fridayCandidates = new[]
        {
            Candidate(BackupType.Full, friday),
            Candidate(BackupType.Differential, friday),
        };

        var merged = BackupPlanSchedule.SelectDue(plan, friday.AddHours(1), fridayCandidates, Empty());
        var afterSuccess = BackupPlanSchedule.SelectDue(
            plan,
            friday.AddHours(1),
            fridayCandidates,
            Outcome(merged[0].Key, BackupSlotDisposition.Succeeded));
        var nextDay = BackupPlanSchedule.SelectDue(
            plan,
            saturday.AddHours(1),
            [
                Candidate(BackupType.Full, friday),
                Candidate(BackupType.Differential, friday),
                Candidate(BackupType.Full, saturday),
            ],
            Outcome(merged[0].Key, BackupSlotDisposition.Succeeded));
        var rememberedWithoutOldCandidate = BackupPlanSchedule.SelectDue(
            plan,
            saturday.AddHours(1),
            [
                Candidate(BackupType.Differential, friday),
                Candidate(BackupType.Full, saturday),
            ],
            Outcome(merged[0].Key, BackupSlotDisposition.Succeeded));

        Assert.Empty(afterSuccess);
        Assert.Single(nextDay);
        Assert.Equal(BackupType.Full, nextDay[0].Key.BackupType);
        Assert.Equal(saturday, nextDay[0].Key.SlotUtc);
        var covered = Assert.NotNull(nextDay[0].DifferentialCoveredWhenFullSucceeds);
        Assert.Equal(friday, covered.SlotUtc);
        Assert.Equal(BackupType.Differential, covered.BackupType);
        Assert.Equal(nextDay, rememberedWithoutOldCandidate);
    }

    [Fact]
    public void LaterUncreatedFullSupersedesDifferentialAfterEarlierFullFailed()
    {
        var plan = ChainPlan();
        var friday = new DateTimeOffset(2026, 10, 2, 2, 0, 0, TimeSpan.Zero);
        var saturday = new DateTimeOffset(2026, 10, 3, 2, 0, 0, TimeSpan.Zero);
        var merged = BackupPlanSchedule.SelectDue(
            plan,
            friday.AddHours(1),
            [Candidate(BackupType.Full, friday), Candidate(BackupType.Differential, friday)],
            Empty());
        var nextDay = BackupPlanSchedule.SelectDue(
            plan,
            saturday.AddHours(1),
            [
                Candidate(BackupType.Full, friday),
                Candidate(BackupType.Differential, friday),
                Candidate(BackupType.Full, saturday),
            ],
            Outcome(merged[0].Key, BackupSlotDisposition.Failed));

        Assert.Single(nextDay);
        Assert.Equal(saturday, nextDay[0].Key.SlotUtc);
        Assert.Equal(BackupType.Full, nextDay[0].Key.BackupType);
        var covered = Assert.NotNull(nextDay[0].DifferentialCoveredWhenFullSucceeds);
        Assert.Equal(friday, covered.SlotUtc);
        Assert.Equal(BackupType.Differential, covered.BackupType);
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
        plan.Revise(SecondVersionId, ChainDefinition(localRecoveryWindowDays: 21), revisedAt);

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
                logInterval: null,
                "UTC",
                BackupStorageMode.LocalOnly,
                storageTargetId: null,
                localRecoveryWindowDays: 14,
                remoteRecoveryWindowDays: null,
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

    [Fact]
    public void FutureSlotsAreIgnoredAndTheCurrentInstantRemainsDue()
    {
        var plan = ChainPlan();
        var now = Slot(2);
        var work = BackupPlanSchedule.SelectDue(
            plan,
            now,
            [
                Candidate(BackupType.Full, now.AddMinutes(-1)),
                Candidate(BackupType.Full, now),
                Candidate(BackupType.Full, now.AddMinutes(1)),
            ],
            Empty());

        Assert.Single(work);
        Assert.Equal(now, work[0].Key.SlotUtc);
    }

    [Fact]
    public void UnknownPlanVersionIsRejected()
    {
        var plan = ChainPlan();
        var unknown = new BackupPlanSlotCandidate(Guid.NewGuid(), BackupType.Full, Slot(1));

        Assert.Throws<ArgumentException>(() => BackupPlanSchedule.SelectDue(plan, Slot(2), [unknown], Empty()));
    }

    [Fact]
    public void CrossVersionCandidatesKeepTheLatestDueSlot()
    {
        var plan = ChainPlan();
        var revisedAt = Slot(2);
        plan.Revise(SecondVersionId, ChainDefinition(localRecoveryWindowDays: 21), revisedAt);
        var earlier = revisedAt.AddMinutes(-30);
        var later = revisedAt.AddHours(2);
        var work = BackupPlanSchedule.SelectDue(
            plan,
            later.AddHours(1),
            [
                new BackupPlanSlotCandidate(FirstVersionId, BackupType.Full, earlier),
                new BackupPlanSlotCandidate(SecondVersionId, BackupType.Full, later),
            ],
            Empty());

        Assert.Single(work);
        Assert.Equal(SecondVersionId, work[0].Key.PlanVersionId);
        Assert.Equal(later, work[0].Key.SlotUtc);
    }

    [Theory]
    [InlineData(BackupSlotDisposition.Pending)]
    [InlineData(BackupSlotDisposition.Uncertain)]
    [InlineData(BackupSlotDisposition.Succeeded)]
    public void NewerFullSupersedesAnOlderDifferential(BackupSlotDisposition disposition)
    {
        var plan = ChainPlan();
        var differential = Slot(2);
        var full = Slot(3);
        var fullKey = new BackupScheduleSlotKey(plan.Id, FirstVersionId, BackupType.Full, full);
        var work = BackupPlanSchedule.SelectDue(
            plan,
            Slot(4),
            [
                Candidate(BackupType.Differential, differential),
                Candidate(BackupType.Full, full),
            ],
            Outcome(fullKey, disposition));

        Assert.Empty(work);
    }

    [Fact]
    public void FailedNewerFullLeavesTheOlderDifferentialDue()
    {
        var plan = ChainPlan();
        var differential = Slot(2);
        var full = Slot(3);
        var fullKey = new BackupScheduleSlotKey(plan.Id, FirstVersionId, BackupType.Full, full);
        var work = BackupPlanSchedule.SelectDue(
            plan,
            Slot(4),
            [Candidate(BackupType.Differential, differential), Candidate(BackupType.Full, full)],
            Outcome(fullKey, BackupSlotDisposition.Failed));

        Assert.Single(work);
        Assert.Equal(BackupType.Differential, work[0].Key.BackupType);
        Assert.Equal(differential, work[0].Key.SlotUtc);
    }

    [Fact]
    public void SucceededLaterFullDoesNotRebuildAnEarlierDifferential()
    {
        var plan = ChainPlan();
        var work = BackupPlanSchedule.SelectDue(
            plan,
            Slot(4),
            [Candidate(BackupType.Differential, Slot(2)), Candidate(BackupType.Full, Slot(3))],
            Outcome(
                new BackupScheduleSlotKey(plan.Id, FirstVersionId, BackupType.Full, Slot(3)),
                BackupSlotDisposition.Succeeded));

        Assert.Empty(work);
    }

    [Fact]
    public void AbandonedEarlierFullDoesNotSwallowDifferentialWhenLatestFullFailed()
    {
        var plan = ChainPlan();
        var differential = Slot(2);
        var work = BackupPlanSchedule.SelectDue(
            plan,
            Slot(5),
            [
                Candidate(BackupType.Full, Slot(3)),
                Candidate(BackupType.Full, Slot(4)),
                Candidate(BackupType.Differential, differential),
            ],
            Outcome(
                new BackupScheduleSlotKey(plan.Id, FirstVersionId, BackupType.Full, Slot(4)),
                BackupSlotDisposition.Failed));

        Assert.Single(work);
        Assert.Equal(BackupType.Differential, work[0].Key.BackupType);
        Assert.Equal(differential, work[0].Key.SlotUtc);
    }

    [Fact]
    public void CoverageIsNotAttachedToADifferentialThatAlreadyHasARecord()
    {
        var plan = ChainPlan();
        var differential = Slot(2);
        var full = Slot(3);
        var work = BackupPlanSchedule.SelectDue(
            plan,
            Slot(4),
            [
                Candidate(BackupType.Differential, differential),
                Candidate(BackupType.Full, full),
            ],
            Outcome(
                new BackupScheduleSlotKey(plan.Id, FirstVersionId, BackupType.Differential, differential),
                BackupSlotDisposition.Succeeded));

        Assert.Single(work);
        Assert.Equal(BackupType.Full, work[0].Key.BackupType);
        Assert.Equal(full, work[0].Key.SlotUtc);
        Assert.Null(work[0].DifferentialCoveredWhenFullSucceeds);
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

    private static BackupPlanDefinition ChainDefinition(int localRecoveryWindowDays = 14)
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
            logInterval: null,
            "UTC",
            BackupStorageMode.LocalOnly,
            storageTargetId: null,
            localRecoveryWindowDays,
            remoteRecoveryWindowDays: null,
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
