using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.Tests;

public sealed class BackupPlanSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 4, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(2, 3, 2)]
    [InlineData(3, 3, 1)]
    [InlineData(4, 3, 1)]
    public void DueTypesAreCombinedAndCoveredOnlyWhenAbsent(int fullHour, int diffHour, int count)
    {
        var plan = Plan(fullHour, diffHour);
        var selection = Select(plan);
        Assert.Equal(count, selection.Count);
        var full = Assert.Single(selection, x => x.Key.BackupType == BackupType.Full);
        Assert.Equal(Now.UtcDateTime.Date.AddHours(fullHour), full.Key.SlotUtc.UtcDateTime);
        Assert.Equal(fullHour >= diffHour, full.DifferentialCoveredWhenFullSucceeds is not null);
        if (fullHour >= diffHour)
        {
            var diff = Key(plan, BackupType.Differential, Now.UtcDateTime.Date.AddHours(diffHour));
            Assert.Null(Assert.Single(Select(plan, new Dictionary<BackupScheduleSlotKey, BackupSlotDisposition>
            { [diff] = BackupSlotDisposition.Failed })).DifferentialCoveredWhenFullSucceeds);
        }
    }

    [Theory]
    [InlineData(BackupSlotDisposition.Pending, 0)]
    [InlineData(BackupSlotDisposition.Uncertain, 0)]
    [InlineData(BackupSlotDisposition.Succeeded, 0)]
    [InlineData(BackupSlotDisposition.Failed, 1)]
    public void FullDispositionDeterminesWhetherOlderDiffMayBeQueued(BackupSlotDisposition disposition, int expected)
    {
        var plan = Plan(4, 3);
        var existing = new Dictionary<BackupScheduleSlotKey, BackupSlotDisposition> { [Key(plan, BackupType.Full, Now)] = disposition };
        var work = Select(plan, existing);
        Assert.Equal(expected, work.Count);
        if (expected == 1) Assert.Equal(BackupType.Differential, Assert.Single(work).Key.BackupType);
    }

    [Fact]
    public void HistoricalSuccessOutsideCalculatorWindowStillSuppressesDiffAfterNewestFailure()
    {
        var definition = Definition(4, 3, days: BackupWeekdays.Monday);
        var plan = BackupPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "合成", Guid.NewGuid(), definition, Now.AddMonths(-2));
        var now = Now.AddDays(3);
        var existing = new Dictionary<BackupScheduleSlotKey, BackupSlotDisposition>
        {
            [Key(plan, BackupType.Full, now)] = BackupSlotDisposition.Failed,
            [Key(plan, BackupType.Full, Now.AddDays(-3))] = BackupSlotDisposition.Succeeded
        };
        Assert.Empty(BackupPlanScheduleSelectionRules.Select(plan, now, existing).Work);
    }

    [Fact]
    public void AbandonedOlderFullCandidatesDoNotSuppressWeeklyDiffAfterLatestFullFailed()
    {
        var plan = BackupPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "合成", Guid.NewGuid(), Definition(4, 3, days: BackupWeekdays.Monday), Now.AddMonths(-2));
        var work = Select(plan, new Dictionary<BackupScheduleSlotKey, BackupSlotDisposition>
        { [Key(plan, BackupType.Full, Now)] = BackupSlotDisposition.Failed });
        var diff = Assert.Single(work);
        Assert.Equal(BackupType.Differential, diff.Key.BackupType);
        Assert.Equal(Now.AddDays(-3).AddHours(-1), diff.Key.SlotUtc);
    }

    [Fact]
    public void DowntimeAcrossVersionsUsesEachVersionEndAndExclusiveStart()
    {
        var plan = Plan(2, 3);
        var old = plan.CurrentVersionId;
        var boundary = Now.AddMonths(1).UtcDateTime.Date.AddHours(3);
        plan.Revise(Guid.NewGuid(), Definition(2, 3, days: BackupWeekdays.Friday), boundary);
        var work = BackupPlanScheduleSelectionRules.Select(plan, boundary.AddMinutes(1), new Dictionary<BackupScheduleSlotKey, BackupSlotDisposition>()).Work;
        Assert.Equal(2, work.Count);
        Assert.All(work, x => Assert.Equal(old, x.Key.PlanVersionId));
        Assert.Equal(boundary, Assert.Single(work, x => x.Key.BackupType == BackupType.Differential).Key.SlotUtc);
        var later = BackupPlanScheduleSelectionRules.Select(plan, boundary.AddMonths(5), new Dictionary<BackupScheduleSlotKey, BackupSlotDisposition>()).Work;
        Assert.InRange(later.Count(x => x.Key.BackupType == BackupType.Full), 0, 1);
        Assert.InRange(later.Count(x => x.Key.BackupType == BackupType.Differential), 0, 1);
        Assert.All(later, x => Assert.Equal(plan.CurrentVersionId, x.Key.PlanVersionId));
    }

    [Theory]
    [InlineData(3, 8, 2, 7)]
    [InlineData(11, 1, 1, 5)]
    public void DaylightSavingUsesExistingGapAndFoldRules(int month, int day, int localHour, int utcHour)
    {
        var now = new DateTimeOffset(2026, month, day, 12, 0, 0, TimeSpan.Zero);
        var plan = BackupPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "合成", Guid.NewGuid(),
            Definition(localHour, localHour, zone: "Eastern Standard Time"), now.AddDays(-30));
        var full = Assert.Single(BackupPlanScheduleSelectionRules.Select(plan, now, new Dictionary<BackupScheduleSlotKey, BackupSlotDisposition>()).Work);
        Assert.Equal(now.Date.AddHours(utcHour), full.Key.SlotUtc.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, full.Key.SlotUtc.Offset);
    }

    [Fact]
    public void PausedAndLogModesDoNotGenerateLogOrAdHocWork()
    {
        var plan = Plan(2, 3);
        plan.Pause();
        Assert.Empty(Select(plan));
        var log = BackupPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "合成", Guid.NewGuid(),
            new(BackupPlanMode.FullAndDifferentialAndLog, new(BackupScheduleType.Daily, new(2, 0), BackupWeekdays.None),
                new(BackupScheduleType.Daily, new(2, 0), BackupWeekdays.None), new(30), "UTC", BackupStorageMode.LocalOnly, null, 14, null, true, false, 120, 60, 90), Now.AddDays(-2));
        Assert.Equal(BackupType.Full, Assert.Single(Select(log)).Key.BackupType);
    }

    [Theory]
    [InlineData(BackupTaskStatus.Failed, BackupInvocationStatus.Succeeded, true, true, BackupSlotDisposition.Succeeded)]
    [InlineData(BackupTaskStatus.Succeeded, BackupInvocationStatus.Succeeded, false, true, BackupSlotDisposition.Uncertain)]
    [InlineData(BackupTaskStatus.Failed, BackupInvocationStatus.Succeeded, true, false, BackupSlotDisposition.Uncertain)]
    [InlineData(BackupTaskStatus.Failed, BackupInvocationStatus.ConfirmedFailed, false, false, BackupSlotDisposition.Failed)]
    [InlineData(BackupTaskStatus.Failed, BackupInvocationStatus.ConfirmedFailed, true, false, BackupSlotDisposition.Uncertain)]
    [InlineData(BackupTaskStatus.Pending, BackupInvocationStatus.ConfirmedFailed, false, false, BackupSlotDisposition.Pending)]
    [InlineData(BackupTaskStatus.NeedsAttention, BackupInvocationStatus.ConfirmedFailed, false, false, BackupSlotDisposition.Uncertain)]
    [InlineData(BackupTaskStatus.NeedsAttention, BackupInvocationStatus.Succeeded, true, true, BackupSlotDisposition.Uncertain)]
    [InlineData(BackupTaskStatus.Running, BackupInvocationStatus.Running, false, false, BackupSlotDisposition.Pending)]
    [InlineData(BackupTaskStatus.Failed, BackupInvocationStatus.Indeterminate, false, false, BackupSlotDisposition.Uncertain)]
    public void DispositionUsesFactsRatherThanFinalStatus(BackupTaskStatus status, BackupInvocationStatus sql,
        bool metadata, bool local, BackupSlotDisposition expected)
    {
        var id = Guid.NewGuid();
        Assert.Equal(expected, BackupPlanSlotDispositionRules.Evaluate(status, id, [new(id, sql, metadata, local)]));
    }

    [Fact]
    public void MissingAndContradictoryAttemptsCannotBeCombinedOrAssumedFailed()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.Equal(BackupSlotDisposition.Uncertain, BackupPlanSlotDispositionRules.Evaluate(BackupTaskStatus.Failed, null, []));
        Assert.Equal(BackupSlotDisposition.Pending, BackupPlanSlotDispositionRules.Evaluate(BackupTaskStatus.Pending, null, []));
        Assert.Equal(BackupSlotDisposition.Failed, BackupPlanSlotDispositionRules.Evaluate(BackupTaskStatus.Failed, null, [], true));
        Assert.Equal(BackupSlotDisposition.Uncertain, BackupPlanSlotDispositionRules.Evaluate(BackupTaskStatus.NeedsAttention, null, [], true));
        Assert.Equal(BackupSlotDisposition.Uncertain, BackupPlanSlotDispositionRules.Evaluate(BackupTaskStatus.Succeeded, second,
            [new(first, BackupInvocationStatus.Succeeded, false, true), new(second, BackupInvocationStatus.Succeeded, true, false)]));
        Assert.Equal(BackupSlotDisposition.Uncertain, BackupPlanSlotDispositionRules.Evaluate(BackupTaskStatus.Failed, second,
            [new(first, BackupInvocationStatus.Succeeded, true, true), new(second, BackupInvocationStatus.ConfirmedFailed, false, false)]));
        Assert.Equal(BackupSlotDisposition.Uncertain, BackupPlanSlotDispositionRules.Evaluate(BackupTaskStatus.Failed, first,
            [new(first, BackupInvocationStatus.ConfirmedFailed, false, false, true)]));
    }

    [Theory]
    [InlineData(BackupTaskStage.Transfer)]
    [InlineData(BackupTaskStage.ValidateCopy)]
    [InlineData(BackupTaskStage.Cleanup)]
    public void RemoteUncertaintyDoesNotEraseConfirmedBackupSuccess(BackupTaskStage stage)
    {
        var id = Guid.NewGuid();
        Assert.Equal(BackupSlotDisposition.Succeeded, BackupPlanSlotDispositionRules.Evaluate(BackupTaskStatus.NeedsAttention, id,
            [new(id, BackupInvocationStatus.Succeeded, true, true)], currentStage: stage));
        Assert.Equal(BackupSlotDisposition.Uncertain, BackupPlanSlotDispositionRules.Evaluate(BackupTaskStatus.NeedsAttention, id,
            [new(id, BackupInvocationStatus.Succeeded, false, true)], currentStage: stage));
    }

    [Fact]
    public async Task OnePlanConfigurationErrorDoesNotStopOthersAndAllUseSameClock()
    {
        var store = new RecordingStore();
        var result = await new BackupPlanScheduler(store, new FrozenTime()).RunOnceAsync();
        Assert.Equal(2, result.Count);
        Assert.Equal(BackupPlanSchedulingCode.ConfigurationUnavailable, result[0].Code);
        Assert.All(store.Times, x => Assert.Equal(Now, x));
    }

    [Fact]
    public async Task CancellationAndPlatformFailurePropagate()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BackupPlanScheduler(new RecordingStore(), new FrozenTime()).RunOnceAsync(cts.Token));
        await Assert.ThrowsAsync<IOException>(() => new BackupPlanScheduler(new RecordingStore { Fail = true }, new FrozenTime()).RunOnceAsync());
    }

    internal static BackupPlanDefinition Definition(int full = 2, int diff = 3, string zone = "UTC", BackupWeekdays days = BackupWeekdays.None) =>
        new(BackupPlanMode.FullAndDifferential, new(BackupScheduleType.Daily, new(full, 0), BackupWeekdays.None),
            new(days == BackupWeekdays.None ? BackupScheduleType.Daily : BackupScheduleType.Weekly, new(diff, 0), days), null, zone, BackupStorageMode.LocalOnly, null, 14, null, true, false, 120, 60, 90);
    private static BackupPlan Plan(int full, int diff) => BackupPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "合成", Guid.NewGuid(), Definition(full, diff), Now.AddMonths(-2));
    private static BackupScheduleSlotKey Key(BackupPlan plan, BackupType type, DateTimeOffset slot) => new(plan.Id, plan.CurrentVersionId, type, slot);
    private static IReadOnlyList<BackupPlanDueWork> Select(BackupPlan plan, IReadOnlyDictionary<BackupScheduleSlotKey, BackupSlotDisposition>? existing = null) =>
        BackupPlanScheduleSelectionRules.Select(plan, Now, existing ?? new Dictionary<BackupScheduleSlotKey, BackupSlotDisposition>()).Work;
    private sealed class FrozenTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class RecordingStore : IBackupPlanSchedulingStore
    {
        private readonly Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];
        internal List<DateTimeOffset> Times { get; } = [];
        internal bool Fail { get; init; }
        public Task<IReadOnlyList<Guid>> ReadPlanIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Guid>>(ids);
        public Task<BackupPlanSchedulingResult> ScheduleAsync(Guid planId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("合成平台库故障。");
            Times.Add(nowUtc);
            return Task.FromResult(new BackupPlanSchedulingResult(planId,
                planId == ids[0] ? BackupPlanSchedulingCode.ConfigurationUnavailable : BackupPlanSchedulingCode.NoWork, []));
        }
    }
}
