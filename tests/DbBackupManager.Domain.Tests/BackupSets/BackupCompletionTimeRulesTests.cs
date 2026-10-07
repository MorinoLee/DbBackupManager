using DbBackupManager.Domain.BackupSets;

namespace DbBackupManager.Domain.Tests.BackupSets;

public sealed class BackupCompletionTimeRulesTests
{
    private static readonly TimeZoneInfo FixedZone = TimeZoneInfo.CreateCustomTimeZone(
        "SyntheticFixed", TimeSpan.FromHours(8), "合成固定时区", "合成标准时间");

    private static readonly TimeZoneInfo DstZone = CreateDstZone();
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PlatformCompletionWinsDespiteOldOrUnusableSqlTime()
    {
        var observed = Now.AddDays(-10);
        var result = BackupCompletionTimeRules.Evaluate(observed, new DateTime(2020, 1, 1), Now, null, null);
        Assert.Equal(observed, result.CompletedAtUtc);
        Assert.Equal(BackupCompletionTimeSource.PlatformObserved, result.Source);
        Assert.Equal(BackupCompletionTimeReason.PlatformObserved, result.ReasonCode);
    }

    [Fact]
    public void PlatformCompletionSlightlyAfterNowIsPreservedAfterClockMovesBack()
    {
        var observed = Now.AddSeconds(1);
        var result = BackupCompletionTimeRules.Evaluate(observed, null, Now, null, null);
        Assert.Equal(observed, result.CompletedAtUtc);
        Assert.Equal(BackupCompletionTimeSource.PlatformObserved, result.Source);
        Assert.Equal(BackupCompletionTimeReason.PlatformObserved, result.ReasonCode);
    }

    [Fact]
    public void KnownOffsetConvertsRawLocalTimeWithoutChangingIt()
    {
        var local = new DateTime(2026, 10, 7, 15, 0, 0, DateTimeKind.Unspecified);
        var result = BackupCompletionTimeRules.Evaluate(null, local, Now, TimeSpan.FromHours(8), FixedZone);
        Assert.Equal(Now.AddHours(-1), result.CompletedAtUtc);
        Assert.Equal(BackupCompletionTimeSource.SqlLocalTime, result.Source);
        Assert.Equal(BackupCompletionTimeReason.SqlLocalConverted, result.ReasonCode);
        Assert.Equal(DateTimeKind.Unspecified, local.Kind);
        Assert.Equal(TimeSpan.Zero, result.CompletedAtUtc!.Value.Offset);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(24)]
    public void ZeroAndExactly24HoursAreInsideTheConversionWindow(int hours)
    {
        var expected = Now.AddHours(-hours);
        var local = expected.UtcDateTime.AddHours(8);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var result = BackupCompletionTimeRules.Evaluate(null, local, Now, TimeSpan.FromHours(8), FixedZone);
        Assert.Equal(expected, result.CompletedAtUtc);
    }

    [Fact]
    public void MoreThan24HoursRemainsUnknown()
    {
        var local = new DateTime(2026, 10, 6, 16, 0, 0).AddTicks(-1);
        AssertUnknown(BackupCompletionTimeRules.Evaluate(null, local, Now, TimeSpan.FromHours(8), FixedZone),
            BackupCompletionTimeReason.OlderThan24Hours);
    }

    [Fact]
    public void FutureSqlTimeIsUnknown()
    {
        AssertUnknown(BackupCompletionTimeRules.Evaluate(null, new DateTime(2026, 10, 7, 17, 0, 0),
            Now, TimeSpan.FromHours(8), FixedZone), BackupCompletionTimeReason.FutureSqlTime);
    }

    [Fact]
    public void MissingSqlTimeOffsetOrTimeZoneCannotProduceCompletion()
    {
        var local = new DateTime(2026, 10, 7, 15, 0, 0);
        AssertUnknown(BackupCompletionTimeRules.Evaluate(null, null, Now, TimeSpan.Zero, FixedZone),
            BackupCompletionTimeReason.SqlFinishMissing);
        AssertUnknown(BackupCompletionTimeRules.Evaluate(null, local, Now, null, FixedZone),
            BackupCompletionTimeReason.ServerOffsetUnknown);
        AssertUnknown(BackupCompletionTimeRules.Evaluate(null, local, Now, TimeSpan.FromHours(8), null),
            BackupCompletionTimeReason.ServerTimeZoneUnknown);
    }

    [Fact]
    public void DaylightSavingRepeatedTimeIsUnknown()
    {
        var now = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        var repeated = new DateTime(2026, 11, 1, 1, 30, 0);
        Assert.True(DstZone.IsAmbiguousTime(repeated));
        AssertUnknown(BackupCompletionTimeRules.Evaluate(null, repeated, now, TimeSpan.FromHours(-5), DstZone),
            BackupCompletionTimeReason.AmbiguousLocalTime);
    }

    [Fact]
    public void DaylightSavingSkippedTimeIsUnknown()
    {
        var now = new DateTimeOffset(2026, 3, 8, 9, 0, 0, TimeSpan.Zero);
        var skipped = new DateTime(2026, 3, 8, 2, 30, 0);
        Assert.True(DstZone.IsInvalidTime(skipped));
        AssertUnknown(BackupCompletionTimeRules.Evaluate(null, skipped, now, TimeSpan.FromHours(-4), DstZone),
            BackupCompletionTimeReason.InvalidLocalTime);
    }

    [Fact]
    public void CurrentOffsetCannotBeAppliedAcrossARecentDstTransition()
    {
        var now = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        var beforeTransition = new DateTime(2026, 11, 1, 0, 30, 0);
        Assert.False(DstZone.IsAmbiguousTime(beforeTransition));
        AssertUnknown(BackupCompletionTimeRules.Evaluate(null, beforeTransition, now, TimeSpan.FromHours(-5), DstZone),
            BackupCompletionTimeReason.ServerOffsetMismatch);
    }

    [Fact]
    public void NormalDaylightSavingTimeUsesTheReadableCurrentOffset()
    {
        var now = new DateTimeOffset(2026, 7, 1, 16, 0, 0, TimeSpan.Zero);
        var result = BackupCompletionTimeRules.Evaluate(null, new DateTime(2026, 7, 1, 11, 0, 0),
            now, TimeSpan.FromHours(-4), DstZone);
        Assert.Equal(now.AddHours(-1), result.CompletedAtUtc);
    }

    [Fact]
    public void OffsetDisagreeingWithServerTimeZoneIsUnknown()
    {
        AssertUnknown(BackupCompletionTimeRules.Evaluate(null, new DateTime(2026, 10, 7, 15, 0, 0),
            Now, TimeSpan.Zero, FixedZone), BackupCompletionTimeReason.ServerOffsetMismatch);
    }

    [Fact]
    public void ConversionOutsideDateTimeOffsetRangeIsUnknown()
    {
        AssertUnknown(BackupCompletionTimeRules.Evaluate(null, DateTime.MinValue, Now, TimeSpan.FromHours(8), FixedZone),
            BackupCompletionTimeReason.ConversionOutOfRange);
    }

    [Fact]
    public void PlatformUtcFactStillWorksDuringAnAmbiguousSqlLocalTime()
    {
        var now = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        var observed = now.AddHours(-1);
        var result = BackupCompletionTimeRules.Evaluate(observed, new DateTime(2026, 11, 1, 1, 30, 0),
            now, TimeSpan.FromHours(-5), DstZone);
        Assert.Equal(observed, result.CompletedAtUtc);
        Assert.Equal(BackupCompletionTimeSource.PlatformObserved, result.Source);
    }

    [Fact]
    public void InputUtcFactsAndRawSqlLocalKindAreValidated()
    {
        Assert.Throws<ArgumentException>(() => BackupCompletionTimeRules.Evaluate(
            Now.ToOffset(TimeSpan.FromHours(8)), null, Now, null, null));
        Assert.Throws<ArgumentException>(() => BackupCompletionTimeRules.Evaluate(
            null, null, Now.ToOffset(TimeSpan.FromHours(8)), null, null));
        Assert.Throws<ArgumentException>(() => BackupCompletionTimeRules.Evaluate(
            null, Now.UtcDateTime, Now, TimeSpan.FromHours(8), FixedZone));
    }

    private static void AssertUnknown(BackupCompletionTimeDecision result, string reason)
    {
        Assert.Null(result.CompletedAtUtc);
        Assert.Equal(BackupCompletionTimeSource.Unknown, result.Source);
        Assert.Equal(reason, result.ReasonCode);
    }

    private static TimeZoneInfo CreateDstZone()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("SyntheticDst", TimeSpan.FromHours(-5),
            "合成夏令时区", "合成标准时间", "合成夏令时间", [rule]);
    }
}
