using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.BackupTasks;

public enum BackupScheduleCalculationStatus
{
    Succeeded = 1,
    InvalidTimeZone = 2
}

public sealed record BackupScheduleRequest(
    BackupScheduleType ScheduleType,
    TimeOnly LocalTime,
    BackupWeekdays DaysOfWeek,
    string TimeZoneId,
    DateTimeOffset ScheduleEffectiveFromUtc,
    DateTimeOffset NowUtc);

public sealed record BackupScheduleCalculation(
    BackupScheduleCalculationStatus Status,
    IReadOnlyList<DateTimeOffset> WindowSlots,
    DateTimeOffset? LatestDueSlotUtc,
    DateTimeOffset? NextFutureSlotUtc)
{
    public static BackupScheduleCalculation InvalidTimeZone { get; } =
        new(BackupScheduleCalculationStatus.InvalidTimeZone, [], null, null);
}

public static class BackupScheduleCalculator
{
    public const int DailyLookbackLocalDays = 2;
    public const int WeeklyLookbackLocalDays = 8;

    public static BackupScheduleCalculation Calculate(BackupScheduleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return BackupScheduleCalculation.InvalidTimeZone;
        }

        var nowUtc = request.NowUtc.ToUniversalTime();
        var effectiveFromUtc = request.ScheduleEffectiveFromUtc.ToUniversalTime();
        var lookbackDays = request.ScheduleType == BackupScheduleType.Weekly
            ? WeeklyLookbackLocalDays
            : DailyLookbackLocalDays;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc.UtcDateTime, timeZone);
        var today = DateOnly.FromDateTime(nowLocal);
        var windowStart = today.AddDays(-lookbackDays);
        var searchEnd = today.AddDays(lookbackDays);
        var slots = new List<DateTimeOffset>();
        for (var date = windowStart; date <= searchEnd; date = date.AddDays(1))
        {
            if (!IsScheduledDay(request.ScheduleType, request.DaysOfWeek, date))
            {
                continue;
            }

            if (!TryCreateSlot(timeZone, date, request.LocalTime, out var slot)
                || slot <= effectiveFromUtc)
            {
                continue;
            }

            slots.Add(slot);
        }

        var windowSlots = slots
            .Where(slot =>
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(slot.UtcDateTime, timeZone);
                return DateOnly.FromDateTime(local) >= windowStart
                    && DateOnly.FromDateTime(local) <= today;
            })
            .ToArray();
        return new BackupScheduleCalculation(
            BackupScheduleCalculationStatus.Succeeded,
            windowSlots,
            windowSlots.Where(slot => slot <= nowUtc).Select(slot => (DateTimeOffset?)slot).Max(),
            slots.Where(slot => slot > nowUtc).Select(slot => (DateTimeOffset?)slot).Min());
    }

    private static bool IsScheduledDay(
        BackupScheduleType scheduleType,
        BackupWeekdays daysOfWeek,
        DateOnly date)
    {
        if (scheduleType == BackupScheduleType.Daily)
        {
            return true;
        }

        return (daysOfWeek & ToFlag(date.DayOfWeek)) != 0;
    }

    private static BackupWeekdays ToFlag(DayOfWeek day) =>
        day switch
        {
            DayOfWeek.Monday => BackupWeekdays.Monday,
            DayOfWeek.Tuesday => BackupWeekdays.Tuesday,
            DayOfWeek.Wednesday => BackupWeekdays.Wednesday,
            DayOfWeek.Thursday => BackupWeekdays.Thursday,
            DayOfWeek.Friday => BackupWeekdays.Friday,
            DayOfWeek.Saturday => BackupWeekdays.Saturday,
            DayOfWeek.Sunday => BackupWeekdays.Sunday,
            _ => BackupWeekdays.None
        };

    private static bool TryCreateSlot(
        TimeZoneInfo timeZone,
        DateOnly date,
        TimeOnly localTime,
        out DateTimeOffset slot)
    {
        var local = date.ToDateTime(localTime, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local)
            && !TryFindFirstValidLocal(timeZone, local, date, out local))
        {
            slot = default;
            return false;
        }

        if (timeZone.IsAmbiguousTime(local))
        {
            var offsets = timeZone.GetAmbiguousTimeOffsets(local);
            var first = new DateTimeOffset(local, offsets[0]);
            var second = new DateTimeOffset(local, offsets[1]);
            slot = first <= second ? first : second;
            return true;
        }

        slot = new DateTimeOffset(local, timeZone.GetUtcOffset(local));
        return true;
    }

    private static bool TryFindFirstValidLocal(
        TimeZoneInfo timeZone,
        DateTime invalidLocal,
        DateOnly date,
        out DateTime valid)
    {
        var end = date.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Unspecified);
        var probe = invalidLocal;
        while (probe <= end && timeZone.IsInvalidTime(probe))
        {
            probe = probe.AddMinutes(1);
        }

        if (probe > end
            || DateOnly.FromDateTime(probe) != date
            || timeZone.IsInvalidTime(probe))
        {
            valid = default;
            return false;
        }

        while (true)
        {
            var previous = probe.AddSeconds(-1);
            if (previous < invalidLocal
                || DateOnly.FromDateTime(previous) != date
                || timeZone.IsInvalidTime(previous))
            {
                break;
            }

            probe = previous;
        }

        valid = probe;
        return true;
    }
}
