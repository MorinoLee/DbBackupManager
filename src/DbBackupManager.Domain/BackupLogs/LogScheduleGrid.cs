namespace DbBackupManager.Domain.BackupLogs;

public sealed record LogScheduleVersion(
    Guid VersionId,
    DateTimeOffset EffectiveFromUtc,
    int? IntervalMinutes);

public sealed record LogScheduleWindow(
    Guid VersionId,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? SupersededAtUtc,
    int IntervalMinutes,
    DateTimeOffset AnchorUtc);

public sealed record LogGridObservation
{
    public DateTimeOffset NowUtc { get; init; }

    public DateTimeOffset? LastObservedNowUtc { get; init; }

    public DateTimeOffset? LastSelectedSlotUtc { get; init; }

    public bool IsPaused { get; init; }

    public DateTimeOffset? PausedAtUtc { get; init; }

    public DateTimeOffset? ResumedAtUtc { get; init; }
}

public enum LogGridStatus
{
    Selected = 1,
    Waiting = 2,
    Paused = 3,
    NoLogSchedule = 4,
    ClockRollback = 5,
    Overflow = 6
}

public sealed record LogGridDecision(
    LogGridStatus Status,
    string ReasonCode,
    Guid? VersionId = null,
    DateTimeOffset? SlotUtc = null,
    DateTimeOffset? LatestLegalSlotUtc = null,
    long MissedSlotCount = 0,
    DateTimeOffset? FirstMissedSlotUtc = null,
    DateTimeOffset? LastMissedSlotUtc = null);

/// <summary>
/// LOG 的独立 UTC 网格：A+k*Delta，k>=1；不改变既有 FULL/DIFF 时区/DST 规则。
/// </summary>
public static class LogScheduleGrid
{
    public static IReadOnlyList<LogScheduleWindow> ResolveWindows(IReadOnlyList<LogScheduleVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (versions.Count == 0)
        {
            return [];
        }

        var ordered = versions.OrderBy(version => version.EffectiveFromUtc).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var version = ordered[index];
            ArgumentNullException.ThrowIfNull(version);
            if (version.VersionId == Guid.Empty)
            {
                throw new ArgumentException("计划版本标识不能为空。", nameof(versions));
            }

            RequireUtc(version.EffectiveFromUtc, nameof(versions));
            if (version.IntervalMinutes is { } interval)
            {
                RequireInterval(interval);
            }

            if (index > 0 && ordered[index - 1].EffectiveFromUtc >= version.EffectiveFromUtc)
            {
                throw new ArgumentException("计划版本生效时间必须严格递增。", nameof(versions));
            }
        }

        var windows = new List<LogScheduleWindow>(ordered.Length);
        int? previousInterval = null;
        DateTimeOffset? previousAnchor = null;
        for (var index = 0; index < ordered.Length; index++)
        {
            var version = ordered[index];
            var supersededAt = index + 1 < ordered.Length
                ? ordered[index + 1].EffectiveFromUtc
                : (DateTimeOffset?)null;
            if (version.IntervalMinutes is not { } interval)
            {
                previousInterval = null;
                previousAnchor = null;
                continue;
            }

            DateTimeOffset anchor;
            if (previousInterval == interval && previousAnchor is { } inherited)
            {
                anchor = inherited;
            }
            else
            {
                anchor = version.EffectiveFromUtc;
            }

            windows.Add(new LogScheduleWindow(
                version.VersionId,
                version.EffectiveFromUtc,
                supersededAt,
                interval,
                anchor));
            previousInterval = interval;
            previousAnchor = anchor;
        }

        return Array.AsReadOnly(windows.ToArray());
    }

    public static bool OwnsSlot(LogScheduleWindow window, DateTimeOffset slotUtc)
    {
        ArgumentNullException.ThrowIfNull(window);
        RequireInterval(window.IntervalMinutes);
        RequireUtc(slotUtc, nameof(slotUtc));
        if (slotUtc <= window.EffectiveFromUtc)
        {
            return false;
        }

        if (window.SupersededAtUtc is { } supersededAt && slotUtc > supersededAt)
        {
            return false;
        }

        var intervalTicks = IntervalTicks(window.IntervalMinutes);
        var delta = slotUtc.UtcTicks - window.AnchorUtc.UtcTicks;
        return delta >= intervalTicks && delta % intervalTicks == 0;
    }

    public static DateTimeOffset? FirstSlotAfter(LogScheduleWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        RequireInterval(window.IntervalMinutes);
        var intervalTicks = IntervalTicks(window.IntervalMinutes);
        var delta = window.EffectiveFromUtc.UtcTicks - window.AnchorUtc.UtcTicks;
        var k = delta < 0 ? 1 : delta / intervalTicks + 1;
        return SlotAt(window, k);
    }

    public static LogGridDecision SelectDue(
        IReadOnlyList<LogScheduleVersion> versions,
        LogGridObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        RequireUtc(observation.NowUtc, nameof(observation.NowUtc));
        RequireOptionalUtc(observation.LastObservedNowUtc, nameof(observation.LastObservedNowUtc));
        RequireOptionalUtc(observation.LastSelectedSlotUtc, nameof(observation.LastSelectedSlotUtc));
        RequireOptionalUtc(observation.PausedAtUtc, nameof(observation.PausedAtUtc));
        RequireOptionalUtc(observation.ResumedAtUtc, nameof(observation.ResumedAtUtc));
        if (observation.PausedAtUtc is { } pausedAt
            && observation.ResumedAtUtc is { } resumedAt
            && resumedAt <= pausedAt)
        {
            throw new ArgumentException("恢复时间必须晚于暂停时间。", nameof(observation));
        }

        if (observation.LastObservedNowUtc is { } lastObserved && observation.NowUtc <= lastObserved)
        {
            return new LogGridDecision(LogGridStatus.ClockRollback, LogReason.ScheduleClockRollback);
        }

        if (observation.LastSelectedSlotUtc is { } lastSelected && observation.NowUtc <= lastSelected)
        {
            return new LogGridDecision(LogGridStatus.ClockRollback, LogReason.ScheduleClockRollback);
        }

        var windows = ResolveWindows(versions);
        var currentVersion = versions
            .OrderBy(version => version.EffectiveFromUtc)
            .LastOrDefault(version => version.EffectiveFromUtc <= observation.NowUtc);
        if (currentVersion is null || currentVersion.IntervalMinutes is null)
        {
            return new LogGridDecision(LogGridStatus.NoLogSchedule, LogReason.ScheduleNoLogSchedule);
        }

        var window = windows.Single(candidate => candidate.VersionId == currentVersion.VersionId);
        if (observation.IsPaused)
        {
            return new LogGridDecision(LogGridStatus.Paused, LogReason.SchedulePaused, window.VersionId);
        }

        if (observation.ResumedAtUtc is { } resumed && resumed > observation.NowUtc)
        {
            return new LogGridDecision(LogGridStatus.Paused, LogReason.ScheduleResumeInFuture, window.VersionId);
        }

        // 当前版本只选择严格晚于 EffectiveFrom 的槽；边界时刻由旧版本拥有。
        var latestLegal = LatestSlotAtOrBefore(window, observation.NowUtc);
        if (latestLegal is null)
        {
            return FirstSlotAfter(window) is null
                ? new LogGridDecision(LogGridStatus.Overflow, LogReason.ScheduleOverflow, window.VersionId)
                : new LogGridDecision(
                    LogGridStatus.Waiting,
                    LogReason.ScheduleWaiting,
                    window.VersionId);
        }

        var eligibleFrom = window.EffectiveFromUtc.AddTicks(1);
        if (observation.ResumedAtUtc is { } resumeLower && resumeLower > eligibleFrom)
        {
            eligibleFrom = resumeLower;
        }

        if (latestLegal < eligibleFrom)
        {
            return new LogGridDecision(
                LogGridStatus.Waiting,
                LogReason.ScheduleWaiting,
                window.VersionId,
                LatestLegalSlotUtc: latestLegal);
        }

        var selected = latestLegal.Value;
        var missedFrom = eligibleFrom;
        if (observation.LastSelectedSlotUtc is { } last
            && last >= missedFrom
            && last < selected)
        {
            missedFrom = last.AddTicks(1);
        }

        var (count, firstMissed, lastMissed) = CountSlots(window, missedFrom, selected);
        return new LogGridDecision(
            LogGridStatus.Selected,
            LogReason.ScheduleSelected,
            window.VersionId,
            selected,
            latestLegal,
            count,
            firstMissed,
            lastMissed);
    }

    private static DateTimeOffset? LatestSlotAtOrBefore(
        LogScheduleWindow window,
        DateTimeOffset upper)
    {
        var intervalTicks = IntervalTicks(window.IntervalMinutes);
        var delta = upper.UtcTicks - window.AnchorUtc.UtcTicks;
        if (delta < intervalTicks)
        {
            return null;
        }

        var k = delta / intervalTicks;
        while (k >= 1)
        {
            var slot = SlotAt(window, k);
            if (slot is { } value
                && value > window.EffectiveFromUtc
                && value <= upper)
            {
                return value;
            }

            k--;
        }

        return null;
    }

    private static (long Count, DateTimeOffset? First, DateTimeOffset? Last) CountSlots(
        LogScheduleWindow window,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive)
    {
        if (toExclusive <= fromInclusive)
        {
            return (0, null, null);
        }

        var intervalTicks = IntervalTicks(window.IntervalMinutes);
        var anchorTicks = window.AnchorUtc.UtcTicks;
        var fromDelta = fromInclusive.UtcTicks - anchorTicks;
        var kFirst = fromDelta <= 0
            ? 1
            : (fromDelta + intervalTicks - 1) / intervalTicks;
        var minimumTicks = window.EffectiveFromUtc.UtcTicks + 1;
        while (anchorTicks + kFirst * intervalTicks < minimumTicks)
        {
            kFirst++;
        }

        var lastDelta = toExclusive.UtcTicks - anchorTicks - 1;
        if (lastDelta < 0)
        {
            return (0, null, null);
        }

        var kLast = lastDelta / intervalTicks;
        if (kLast < kFirst)
        {
            return (0, null, null);
        }

        return (
            kLast - kFirst + 1,
            SlotAt(window, kFirst),
            SlotAt(window, kLast));
    }

    private static DateTimeOffset? SlotAt(LogScheduleWindow window, long k)
    {
        if (k < 1)
        {
            return null;
        }

        var intervalTicks = IntervalTicks(window.IntervalMinutes);
        var ticks = window.AnchorUtc.UtcTicks + k * intervalTicks;
        if (ticks < window.AnchorUtc.UtcTicks
            || ticks < DateTimeOffset.MinValue.UtcTicks
            || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return null;
        }

        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static long IntervalTicks(int intervalMinutes) =>
        intervalMinutes * TimeSpan.TicksPerMinute;

    private static void RequireInterval(int intervalMinutes)
    {
        if (intervalMinutes is < 1 or > 1_440)
        {
            throw new ArgumentOutOfRangeException(
                nameof(intervalMinutes),
                "LOG 间隔必须介于 1 与 1440 分钟之间。");
        }
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("时间必须使用 UTC。", parameterName);
        }
    }

    private static void RequireOptionalUtc(DateTimeOffset? value, string parameterName)
    {
        if (value is { } present)
        {
            RequireUtc(present, parameterName);
        }
    }
}
