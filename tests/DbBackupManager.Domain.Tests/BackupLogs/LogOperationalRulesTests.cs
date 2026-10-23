using DbBackupManager.Domain.BackupLogs;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupLogs;

public sealed class LogOperationalRulesTests
{
    private static readonly DateTimeOffset Now = LogTestData.Utc(2026, 10, 23, 12, 0);

    [Fact]
    public void A11AutomaticUninvokedLogIsMergeable()
    {
        var decision = LogWorkMergeRules.Evaluate(new LogPendingWorkFacts());

        Assert.Equal(LogMergeDisposition.Mergeable, decision.Disposition);
        Assert.Equal(LogReason.MergeEligible, decision.ReasonCode);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("attempt")]
    [InlineData("reservation")]
    [InlineData("permit")]
    [InlineData("side-effect")]
    [InlineData("ended")]
    public void A11ManualOrAlreadyEnteredItemCannotBeMerged(string scenario)
    {
        var work = scenario switch
        {
            "manual" => new LogPendingWorkFacts { Trigger = BackupTaskTriggerType.Manual },
            "attempt" => new LogPendingWorkFacts { HasAttempt = true },
            "reservation" => new LogPendingWorkFacts { HasSqlReservation = true },
            "permit" => new LogPendingWorkFacts { HasInvocationPermit = true },
            "side-effect" => new LogPendingWorkFacts { HasExternalSideEffect = true },
            "ended" => new LogPendingWorkFacts { IsEnded = true },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "未知场景。"),
        };

        var decision = LogWorkMergeRules.Evaluate(work);

        Assert.Equal(LogMergeDisposition.NotMergeable, decision.Disposition);
    }

    [Fact]
    public void A11EndedOldSlotIsNotRebuiltByMerging()
    {
        var decision = LogWorkMergeRules.Evaluate(new LogPendingWorkFacts { IsEnded = true });

        Assert.Equal(LogMergeDisposition.NotMergeable, decision.Disposition);
        Assert.Equal(LogReason.MergeEnded, decision.ReasonCode);
    }

    [Fact]
    public void A11FullBackupAtTheSameInstantDoesNotCoverLog()
    {
        Assert.False(LogWorkMergeRules.DatabaseBackupCoversLog);
        var decision = LogWorkMergeRules.Evaluate(new LogPendingWorkFacts { BackupType = BackupType.Log });

        Assert.Equal(LogMergeDisposition.Mergeable, decision.Disposition);
    }

    [Fact]
    public void A06ObservationSourceTimeAndConflictRemainOnTheFact()
    {
        var observedAt = LogTestData.Utc(2026, 10, 23, 11, 55);
        var metadata = LogTestData.Metadata(
            source: LogObservationSource.Msdb,
            observedAtUtc: observedAt,
            status: LogEvidenceStatus.SourceConflict);

        Assert.Equal(LogObservationSource.Msdb, metadata.Source);
        Assert.Equal(observedAt, metadata.ObservedAtUtc);
        Assert.Equal(LogEvidenceStatus.SourceConflict, metadata.Status);
        Assert.NotEqual(LogObservationSource.BackupHeader, metadata.Source);
    }

    [Fact]
    public void A12RunAgeAndLastSuccessAgeAreReportedSeparately()
    {
        var decision = LogAgeRules.Evaluate(
            Now,
            Now.AddMinutes(-4),
            Now.AddMinutes(-740),
            isPaused: false,
            intervalMinutes: 5);

        Assert.Equal(TimeSpan.FromMinutes(4), decision.RunAge);
        Assert.Equal(TimeSpan.FromMinutes(740), decision.LastSqlSuccessAge);
        Assert.True(decision.HasSqlSuccess);
        Assert.False(decision.Alert);
    }

    [Fact]
    public void A12RunAgeStartsFromTheLaterWindowOrSuccessObservation()
    {
        var decision = LogAgeRules.Evaluate(
            Now,
            Now.AddMinutes(-30),
            Now.AddMinutes(-3),
            isPaused: false,
            intervalMinutes: 5);

        Assert.Equal(TimeSpan.FromMinutes(3), decision.RunAge);
        Assert.Equal(TimeSpan.FromMinutes(3), decision.LastSqlSuccessAge);
        Assert.False(decision.Alert);
    }

    [Fact]
    public void A12PauseSuppressesAlertButKeepsRawSuccessAge()
    {
        var decision = LogAgeRules.Evaluate(
            Now,
            Now.AddMinutes(-60),
            Now.AddMinutes(-740),
            isPaused: true,
            intervalMinutes: 5);

        Assert.Null(decision.RunAge);
        Assert.Equal(TimeSpan.FromMinutes(740), decision.LastSqlSuccessAge);
        Assert.False(decision.Alert);
        Assert.Equal(LogReason.AgePaused, decision.ReasonCode);
    }

    [Fact]
    public void A12MissingSuccessIsNotZeroAndDoesNotFabricateATimePoint()
    {
        var decision = LogAgeRules.Evaluate(
            Now,
            Now.AddMinutes(-4),
            lastSqlSuccessObservedAtUtc: null,
            isPaused: false,
            intervalMinutes: 5);

        Assert.Equal(TimeSpan.FromMinutes(4), decision.RunAge);
        Assert.Null(decision.LastSqlSuccessAge);
        Assert.NotEqual(TimeSpan.Zero, decision.LastSqlSuccessAge);
        Assert.False(decision.HasSqlSuccess);
    }

    [Fact]
    public void A12RunAgeOverTwiceTheIntervalAlerts()
    {
        var decision = LogAgeRules.Evaluate(
            Now,
            Now.AddMinutes(-11),
            lastSqlSuccessObservedAtUtc: null,
            isPaused: false,
            intervalMinutes: 5);

        Assert.True(decision.Alert);
        Assert.Equal(LogReason.AgeExceeded, decision.ReasonCode);
    }

    [Fact]
    public void A13MissingOrInvalidBudgetConfigurationDeniesNewLog()
    {
        var missing = LogBudgetRules.Evaluate(
            new LogBudgetLimits(null, null, null),
            new LogBudgetObservation(0, 0, 1024));
        var invalid = LogBudgetRules.Evaluate(
            new LogBudgetLimits(0, -1, 0),
            new LogBudgetObservation(0, 0, 1024));

        Assert.False(missing.AllowsNewLog);
        Assert.Equal(LogReason.BudgetConfigMissing, missing.ReasonCode);
        Assert.False(invalid.AllowsNewLog);
        Assert.Equal(LogReason.BudgetConfigInvalid, invalid.ReasonCode);
    }

    [Fact]
    public void A13UnknownObservationOrReservationIsNotTreatedAsZero()
    {
        var unknownBytes = LogBudgetRules.Evaluate(
            ValidLimits(),
            new LogBudgetObservation(PendingFileCount: 1, PendingBytes: null, FreeBytes: 1024));
        var unknownFreeSpace = LogBudgetRules.Evaluate(
            ValidLimits(),
            new LogBudgetObservation(PendingFileCount: 1, PendingBytes: 100, FreeBytes: null));
        var unknownReservation = LogBudgetRules.Evaluate(
            ValidLimits(),
            new LogBudgetObservation(
                PendingFileCount: 9,
                PendingBytes: 900,
                FreeBytes: 1024,
                HasUnknownReservations: true));

        Assert.False(unknownBytes.AllowsNewLog);
        Assert.Equal(LogReason.BudgetObservationUnknown, unknownBytes.ReasonCode);
        Assert.False(unknownFreeSpace.AllowsNewLog);
        Assert.Equal(LogReason.BudgetObservationUnknown, unknownFreeSpace.ReasonCode);
        Assert.False(unknownReservation.AllowsNewLog);
        Assert.Equal(LogReason.BudgetReservationUnknown, unknownReservation.ReasonCode);
    }

    [Fact]
    public void A13ThresholdAndFreeSpaceChecksDenyNewLog()
    {
        var fileCount = LogBudgetRules.Evaluate(
            ValidLimits(),
            new LogBudgetObservation(PendingFileCount: 10, PendingBytes: 100, FreeBytes: 1024));
        var bytes = LogBudgetRules.Evaluate(
            ValidLimits(),
            new LogBudgetObservation(PendingFileCount: 1, PendingBytes: 1000, FreeBytes: 1024));
        var freeSpace = LogBudgetRules.Evaluate(
            ValidLimits(),
            new LogBudgetObservation(PendingFileCount: 1, PendingBytes: 100, FreeBytes: 99));

        Assert.Equal(LogReason.BudgetFileCountExceeded, fileCount.ReasonCode);
        Assert.Equal(LogReason.BudgetBytesExceeded, bytes.ReasonCode);
        Assert.Equal(LogReason.BudgetFreeSpaceLow, freeSpace.ReasonCode);
    }

    [Fact]
    public void A13AdmissibleBudgetIsStillNotAnInvocationAuthorization()
    {
        var decision = LogBudgetRules.Evaluate(
            ValidLimits(),
            new LogBudgetObservation(PendingFileCount: 1, PendingBytes: 100, FreeBytes: 1024));

        Assert.True(decision.AllowsNewLog);
        Assert.Equal(LogReason.BudgetAdmissible, decision.ReasonCode);
        Assert.True(decision.AllowsOriginalFileWork);
        Assert.False(decision.IsInvocationAuthorization);
        Assert.True(decision.RequiresTransactionalReservation);
    }

    [Fact]
    public void A13DeniedNewLogStillAllowsOriginalFileWork()
    {
        var decision = LogBudgetRules.Evaluate(
            new LogBudgetLimits(null, null, null),
            new LogBudgetObservation(0, 0, 0));

        Assert.False(decision.AllowsNewLog);
        Assert.True(decision.AllowsOriginalFileWork);
        Assert.False(decision.IsInvocationAuthorization);
    }

    [Fact]
    public void A13ManualLogHasNoSeparateBudgetBypass()
    {
        var manualOrScheduled = LogBudgetRules.Evaluate(
            new LogBudgetLimits(null, null, null),
            new LogBudgetObservation(0, 0, 1024));

        Assert.False(manualOrScheduled.AllowsNewLog);
        Assert.Equal(LogReason.BudgetConfigMissing, manualOrScheduled.ReasonCode);
    }

    private static LogBudgetLimits ValidLimits() => new(10, 1000, 100);
}
