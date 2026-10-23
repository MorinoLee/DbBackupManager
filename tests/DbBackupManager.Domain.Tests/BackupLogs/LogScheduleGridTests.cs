using DbBackupManager.Domain.BackupLogs;

namespace DbBackupManager.Domain.Tests.BackupLogs;

public sealed class LogScheduleGridTests
{
    private static readonly DateTimeOffset TenOClock = LogTestData.Utc(2026, 10, 23, 10, 0);

    [Fact]
    public void A10FirstSlotIsStrictlyAfterEffectiveFrom()
    {
        var version = Version(1, TenOClock, 15);

        var atAnchor = Select([version], TenOClock);
        var atFirstSlot = Select([version], TenOClock.AddMinutes(15));

        Assert.Equal(LogGridStatus.Waiting, atAnchor.Status);
        Assert.Null(atAnchor.SlotUtc);
        Assert.Equal(LogGridStatus.Selected, atFirstSlot.Status);
        Assert.Equal(TenOClock.AddMinutes(15), atFirstSlot.SlotUtc);
    }

    [Fact]
    public void A10SameIntervalInheritsAnchorAndDifferentIntervalUsesNewAnchor()
    {
        var baseVersion = Version(1, TenOClock, 15);
        var sameInterval = Version(2, TenOClock.AddMinutes(22), 15);
        var changedInterval = Version(2, TenOClock.AddMinutes(22), 10);

        var sameWindows = LogScheduleGrid.ResolveWindows([baseVersion, sameInterval]);
        var changedWindows = LogScheduleGrid.ResolveWindows([baseVersion, changedInterval]);

        Assert.Equal(TenOClock, sameWindows[1].AnchorUtc);
        Assert.Equal(TenOClock.AddMinutes(22), changedWindows[1].AnchorUtc);

        var inherited = Select([baseVersion, sameInterval], TenOClock.AddMinutes(30));
        Assert.Equal(LogGridStatus.Selected, inherited.Status);
        Assert.Equal(TenOClock.AddMinutes(30), inherited.SlotUtc);
        Assert.Equal(LogTestData.Id(2), inherited.VersionId);

        var changedTooEarly = Select([baseVersion, changedInterval], TenOClock.AddMinutes(30));
        var changedDue = Select([baseVersion, changedInterval], TenOClock.AddMinutes(32));
        Assert.Equal(LogGridStatus.Waiting, changedTooEarly.Status);
        Assert.Equal(LogGridStatus.Selected, changedDue.Status);
        Assert.Equal(TenOClock.AddMinutes(32), changedDue.SlotUtc);
    }

    [Fact]
    public void A10RemovedThenReEnabledLogStartsANewAnchor()
    {
        var removed = Version(2, TenOClock.AddMinutes(15), null);
        var reEnabled = Version(3, TenOClock.AddMinutes(40), 15);

        var windows = LogScheduleGrid.ResolveWindows(
            [Version(1, TenOClock, 15), removed, reEnabled]);

        Assert.Equal(2, windows.Count);
        Assert.Equal(TenOClock.AddMinutes(40), windows[1].AnchorUtc);

        var before = Select([Version(1, TenOClock, 15), removed, reEnabled], TenOClock.AddMinutes(50));
        var due = Select([Version(1, TenOClock, 15), removed, reEnabled], TenOClock.AddMinutes(55));

        Assert.Equal(LogGridStatus.Waiting, before.Status);
        Assert.Equal(LogGridStatus.Selected, due.Status);
        Assert.Equal(TenOClock.AddMinutes(55), due.SlotUtc);
    }

    [Fact]
    public void A10VersionBoundaryBelongsToTheOldVersionOnly()
    {
        var first = Version(1, TenOClock, 15);
        var second = Version(2, TenOClock.AddMinutes(30), 15);
        var windows = LogScheduleGrid.ResolveWindows([first, second]);
        var boundary = TenOClock.AddMinutes(30);

        Assert.True(LogScheduleGrid.OwnsSlot(windows[0], boundary));
        Assert.False(LogScheduleGrid.OwnsSlot(windows[1], boundary));
        Assert.Equal(TenOClock.AddMinutes(45), LogScheduleGrid.FirstSlotAfter(windows[1]));

        var atBoundary = Select([first, second], boundary);
        var afterBoundary = Select([first, second], TenOClock.AddMinutes(45));

        Assert.Equal(LogGridStatus.Waiting, atBoundary.Status);
        Assert.Equal(LogGridStatus.Selected, afterBoundary.Status);
        Assert.Equal(TenOClock.AddMinutes(45), afterBoundary.SlotUtc);
        Assert.Equal(LogTestData.Id(2), afterBoundary.VersionId);
    }

    [Fact]
    public void A10PauseDoesNotBackfillAndResumeWaitsForALegalGrid()
    {
        var version = Version(1, TenOClock, 5);
        var afterResume = Select(
            [version],
            TenOClock.AddMinutes(46),
            lastSelected: TenOClock.AddMinutes(15),
            pausedAt: TenOClock.AddMinutes(20),
            resumedAt: TenOClock.AddMinutes(45));
        var stillPaused = Select(
            [version],
            TenOClock.AddMinutes(46),
            lastSelected: TenOClock.AddMinutes(15),
            isPaused: true,
            pausedAt: TenOClock.AddMinutes(20),
            resumedAt: TenOClock.AddMinutes(45));
        var resumeInFuture = Select(
            [version],
            TenOClock.AddMinutes(44),
            lastSelected: TenOClock.AddMinutes(15),
            pausedAt: TenOClock.AddMinutes(20),
            resumedAt: TenOClock.AddMinutes(45));
        var noDueAfterResume = Select(
            [version],
            TenOClock.AddMinutes(48),
            lastSelected: TenOClock.AddMinutes(15),
            pausedAt: TenOClock.AddMinutes(20),
            resumedAt: TenOClock.AddMinutes(47));

        Assert.Equal(LogGridStatus.Selected, afterResume.Status);
        Assert.Equal(TenOClock.AddMinutes(45), afterResume.SlotUtc);
        Assert.NotEqual(TenOClock.AddMinutes(20), afterResume.SlotUtc);
        Assert.NotEqual(TenOClock.AddMinutes(25), afterResume.SlotUtc);
        Assert.NotEqual(TenOClock.AddMinutes(40), afterResume.SlotUtc);
        Assert.Equal(LogGridStatus.Paused, stillPaused.Status);
        Assert.Null(stillPaused.SlotUtc);
        Assert.Equal(LogGridStatus.Paused, resumeInFuture.Status);
        Assert.Equal(LogReason.ScheduleResumeInFuture, resumeInFuture.ReasonCode);
        Assert.Equal(LogGridStatus.Waiting, noDueAfterResume.Status);
        Assert.Null(noDueAfterResume.SlotUtc);
    }

    [Fact]
    public void A10ClockRollbackDoesNotRebuildSlots()
    {
        var version = Version(1, TenOClock, 5);
        var observedNow = Select(
            [version],
            TenOClock.AddMinutes(40),
            lastObservedNow: TenOClock.AddMinutes(50));
        var selectedLater = Select(
            [version],
            TenOClock.AddMinutes(45),
            lastSelected: TenOClock.AddMinutes(50));

        Assert.Equal(LogGridStatus.ClockRollback, observedNow.Status);
        Assert.Null(observedNow.SlotUtc);
        Assert.Equal(LogGridStatus.ClockRollback, selectedLater.Status);
        Assert.Null(selectedLater.SlotUtc);
    }

    [Fact]
    public void A10ForwardJumpSelectsOnlyTheLatestSlotAndReportsMissedSlots()
    {
        var result = Select(
            [Version(1, TenOClock, 5)],
            TenOClock.AddHours(1),
            lastObservedNow: TenOClock.AddMinutes(5),
            lastSelected: TenOClock.AddMinutes(5));
        var noFutureSlot = Select(
            [Version(1, TenOClock, 5)],
            TenOClock.AddMinutes(7),
            lastSelected: TenOClock.AddMinutes(5));

        Assert.Equal(LogGridStatus.Selected, result.Status);
        Assert.Equal(TenOClock.AddHours(1), result.SlotUtc);
        Assert.Equal(10, result.MissedSlotCount);
        Assert.Equal(TenOClock.AddMinutes(10), result.FirstMissedSlotUtc);
        Assert.Equal(TenOClock.AddMinutes(55), result.LastMissedSlotUtc);
        Assert.Equal(TenOClock.AddMinutes(5), noFutureSlot.SlotUtc);
    }

    [Fact]
    public void A10NonWholeMinuteAnchorKeepsItsPrecision()
    {
        var anchor = TenOClock.AddSeconds(30).AddMilliseconds(500);
        var version = new LogScheduleVersion(LogTestData.Id(1), anchor, 5);

        var tooEarly = Select([version], TenOClock.AddMinutes(5).AddSeconds(29));
        var due = Select([version], TenOClock.AddMinutes(5).AddSeconds(30).AddMilliseconds(500));

        Assert.Equal(LogGridStatus.Waiting, tooEarly.Status);
        Assert.Equal(LogGridStatus.Selected, due.Status);
        Assert.Equal(TenOClock.AddMinutes(5).AddSeconds(30).AddMilliseconds(500), due.SlotUtc);
    }

    [Fact]
    public void A10RemovedLogDoesNotBackfillTheOldVersion()
    {
        var result = Select(
            [Version(1, TenOClock, 15), Version(2, TenOClock.AddMinutes(30), null)],
            TenOClock.AddMinutes(40));

        Assert.Equal(LogGridStatus.NoLogSchedule, result.Status);
        Assert.Null(result.SlotUtc);
    }

    [Fact]
    public void A10OverflowIsReportedInsteadOfThrowing()
    {
        var effective = DateTimeOffset.MaxValue.AddMinutes(-1);
        var result = Select(
            [new LogScheduleVersion(LogTestData.Id(1), effective, 5)],
            DateTimeOffset.MaxValue);

        Assert.Equal(LogGridStatus.Overflow, result.Status);
        Assert.Equal(LogReason.ScheduleOverflow, result.ReasonCode);
        Assert.Null(result.SlotUtc);
    }

    private static LogScheduleVersion Version(int id, DateTimeOffset effectiveFrom, int? intervalMinutes) =>
        new(LogTestData.Id(id), effectiveFrom, intervalMinutes);

    private static LogGridDecision Select(
        IReadOnlyList<LogScheduleVersion> versions,
        DateTimeOffset now,
        DateTimeOffset? lastObservedNow = null,
        DateTimeOffset? lastSelected = null,
        bool isPaused = false,
        DateTimeOffset? pausedAt = null,
        DateTimeOffset? resumedAt = null)
    {
        return LogScheduleGrid.SelectDue(versions, new LogGridObservation
        {
            NowUtc = now,
            LastObservedNowUtc = lastObservedNow,
            LastSelectedSlotUtc = lastSelected,
            IsPaused = isPaused,
            PausedAtUtc = pausedAt,
            ResumedAtUtc = resumedAt,
        });
    }
}
