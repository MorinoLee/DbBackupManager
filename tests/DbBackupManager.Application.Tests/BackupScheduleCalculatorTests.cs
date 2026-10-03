using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.Tests;

public sealed class BackupScheduleCalculatorTests
{
    private static readonly TimeOnly TwoAm = new(2, 0);

    [Fact]
    public void InvalidTimeZoneFailsClosed()
    {
        var result = BackupScheduleCalculator.Calculate(Request(
            "Synthetic/Unknown-Zone",
            new DateTimeOffset(2026, 9, 18, 4, 0, 0, TimeSpan.Zero)));

        Assert.Equal(BackupScheduleCalculationStatus.InvalidTimeZone, result.Status);
        Assert.Empty(result.WindowSlots);
        Assert.Null(result.LatestDueSlotUtc);
        Assert.Null(result.NextFutureSlotUtc);
    }

    [Fact]
    public void DailyWindowCoversTwoLocalDaysAndIgnoresSlotsAtOrBeforeBaseline()
    {
        var now = Taipei(2026, 9, 18, 10, 0);
        var effective = Taipei(2026, 9, 17, 8, 0);
        var result = BackupScheduleCalculator.Calculate(Request("Taipei Standard Time", now, effective));

        Assert.Equal(BackupScheduleCalculationStatus.Succeeded, result.Status);
        Assert.Equal(
            [Taipei(2026, 9, 18, 2, 0)],
            result.WindowSlots);
        Assert.Equal(Taipei(2026, 9, 18, 2, 0), result.LatestDueSlotUtc);
        Assert.Equal(Taipei(2026, 9, 19, 2, 0), result.NextFutureSlotUtc);
    }

    [Fact]
    public void DailyDoesNotBackfillBeforeEffectiveBaseline()
    {
        var now = Taipei(2026, 9, 18, 10, 0);
        var result = BackupScheduleCalculator.Calculate(Request("Taipei Standard Time", now, now));

        Assert.Empty(result.WindowSlots);
        Assert.Null(result.LatestDueSlotUtc);
        Assert.Equal(Taipei(2026, 9, 19, 2, 0), result.NextFutureSlotUtc);
    }

    [Theory]
    [InlineData(BackupWeekdays.Monday, 2026, 9, 14)]
    [InlineData(BackupWeekdays.Tuesday, 2026, 9, 15)]
    [InlineData(BackupWeekdays.Wednesday, 2026, 9, 16)]
    [InlineData(BackupWeekdays.Thursday, 2026, 9, 17)]
    [InlineData(BackupWeekdays.Friday, 2026, 9, 18)]
    [InlineData(BackupWeekdays.Saturday, 2026, 9, 19)]
    [InlineData(BackupWeekdays.Sunday, 2026, 9, 20)]
    public void WeeklyUsesSelectedLocalWeekdays(BackupWeekdays days, int year, int month, int day)
    {
        var now = Taipei(2026, 9, 20, 10, 0);
        var result = BackupScheduleCalculator.Calculate(Request(
            "Taipei Standard Time",
            now,
            Taipei(2026, 1, 1, 0, 0),
            BackupScheduleType.Weekly,
            days));

        Assert.Contains(Taipei(year, month, day, 2, 0), result.WindowSlots);
        Assert.All(
            result.WindowSlots,
            slot => Assert.Equal(days, ToFlag(TimeZoneInfo.ConvertTimeFromUtc(
                slot.UtcDateTime,
                TimeZoneInfo.FindSystemTimeZoneById("Taipei Standard Time")).DayOfWeek)));
    }

    [Fact]
    public void WeeklyLookbackIsEightLocalDays()
    {
        var now = Taipei(2026, 9, 18, 10, 0);
        var result = BackupScheduleCalculator.Calculate(Request(
            "Taipei Standard Time",
            now,
            Taipei(2026, 1, 1, 0, 0),
            BackupScheduleType.Weekly,
            BackupWeekdays.Friday));

        Assert.Equal(
            [Taipei(2026, 9, 11, 2, 0), Taipei(2026, 9, 18, 2, 0)],
            result.WindowSlots);
        Assert.Equal(Taipei(2026, 9, 18, 2, 0), result.LatestDueSlotUtc);
        Assert.Equal(Taipei(2026, 9, 25, 2, 0), result.NextFutureSlotUtc);
    }

    [Fact]
    public void InvalidLocalTimeMovesToFirstValidTimeOnSameDay()
    {
        var now = Pacific(2026, 3, 8, 10, 0);
        var result = BackupScheduleCalculator.Calculate(Request(
            "Pacific Standard Time",
            now,
            Pacific(2026, 3, 1, 0, 0),
            localTime: new TimeOnly(2, 30)));

        var expected = FirstValidPacific(2026, 3, 8, new TimeOnly(2, 30));
        Assert.Equal(expected, result.LatestDueSlotUtc);
        Assert.Equal(new TimeOnly(3, 0), TimeOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(
                expected.UtcDateTime,
                TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"))));
    }

    [Fact]
    public void AmbiguousLocalTimeUsesEarlierUtc()
    {
        var now = Pacific(2026, 11, 1, 10, 0);
        var result = BackupScheduleCalculator.Calculate(Request(
            "Pacific Standard Time",
            now,
            Pacific(2026, 10, 1, 0, 0),
            localTime: new TimeOnly(1, 30)));

        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var local = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified);
        var offsets = zone.GetAmbiguousTimeOffsets(local);
        var earlier = new DateTimeOffset(local, offsets[0]) <= new DateTimeOffset(local, offsets[1])
            ? new DateTimeOffset(local, offsets[0])
            : new DateTimeOffset(local, offsets[1]);
        var later = earlier == new DateTimeOffset(local, offsets[0])
            ? new DateTimeOffset(local, offsets[1])
            : new DateTimeOffset(local, offsets[0]);

        Assert.Equal(earlier, result.LatestDueSlotUtc);
        Assert.True(earlier < later);
        Assert.DoesNotContain(later, result.WindowSlots);
    }

    [Fact]
    public void UtcBoundaryUsesPolicyTimeZoneNotServerClock()
    {
        var now = new DateTimeOffset(2026, 9, 17, 16, 30, 0, TimeSpan.Zero);
        var result = BackupScheduleCalculator.Calculate(Request(
            "Taipei Standard Time",
            now,
            new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(Taipei(2026, 9, 17, 2, 0), result.LatestDueSlotUtc);
        Assert.Equal(Taipei(2026, 9, 18, 2, 0), result.NextFutureSlotUtc);
    }

    private static BackupScheduleRequest Request(
        string timeZoneId,
        DateTimeOffset nowUtc,
        DateTimeOffset? effectiveFromUtc = null,
        BackupScheduleType scheduleType = BackupScheduleType.Daily,
        BackupWeekdays daysOfWeek = BackupWeekdays.None,
        TimeOnly? localTime = null) =>
        new(
            scheduleType,
            localTime ?? TwoAm,
            daysOfWeek,
            timeZoneId,
            effectiveFromUtc ?? nowUtc.AddDays(-30),
            nowUtc);

    private static DateTimeOffset Taipei(int year, int month, int day, int hour, int minute) =>
        new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(8));

    private static DateTimeOffset Pacific(int year, int month, int day, int hour, int minute)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static DateTimeOffset FirstValidPacific(int year, int month, int day, TimeOnly localTime)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var local = new DateTime(year, month, day, localTime.Hour, localTime.Minute, 0, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
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
}
