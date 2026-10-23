using DbBackupManager.Domain.BackupLogs;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupLogs;

public sealed class LogReadinessAndPriorCallTests
{
    [Fact]
    public void A05LastLogBackupLsnMissingIsNotReadyEvenWithHistoricalManagedFull()
    {
        var facts = LogTestData.ReadyFacts() with
        {
            LastLogBackupLsn = null,
            HasHistoricalManagedFull = true,
        };

        var decision = LogTechnicalReadinessRules.Evaluate(facts);

        Assert.Equal(LogTechnicalReadiness.NotReady, decision.Readiness);
        Assert.Equal(LogReason.ChainNotReady, decision.ReasonCode);
        Assert.False(decision.IsReady);
    }

    [Theory]
    [InlineData(DatabaseRecoveryModel.Simple, LogTechnicalReadiness.NotReady)]
    [InlineData(DatabaseRecoveryModel.BulkLogged, LogTechnicalReadiness.NotReady)]
    [InlineData(DatabaseRecoveryModel.Unknown, LogTechnicalReadiness.Unknown)]
    public void A05RecoveryModelNotFullKeepsLogNotReady(
        DatabaseRecoveryModel recoveryModel,
        LogTechnicalReadiness expected)
    {
        var decision = LogTechnicalReadinessRules.Evaluate(
            LogTestData.ReadyFacts() with { RecoveryModel = recoveryModel });

        Assert.Equal(expected, decision.Readiness);
        Assert.False(decision.IsReady);
    }

    [Fact]
    public void A05ReadinessIgnoresHistoricalManagedFullAsAnExemption()
    {
        var withoutBaseline = LogTechnicalReadinessRules.Evaluate(
            LogTestData.ReadyFacts() with { LastLogBackupLsn = null, HasHistoricalManagedFull = false });
        var withBaseline = LogTechnicalReadinessRules.Evaluate(
            LogTestData.ReadyFacts() with { LastLogBackupLsn = null, HasHistoricalManagedFull = true });

        Assert.Equal(withoutBaseline.Readiness, withBaseline.Readiness);
        Assert.Equal(withoutBaseline.ReasonCode, withBaseline.ReasonCode);
    }

    [Fact]
    public void A05LegacyBaselineAndValidSequenceBooleansCannotAllowPlanLog()
    {
        var request = new BackupAdmissionRequest(
            BackupPlanMode.FullAndDifferentialAndLog,
            BackupRunPurpose.PlanLog,
            BackupTaskTriggerType.Scheduled,
            planIsPaused: false,
            DatabaseRecoveryModel.Full,
            hasManagedBaseline: true,
            hasValidLogSequence: true,
            BackupPlanProtectionOptions.Default,
            BackupSlotOccupancy.None,
            allowDatabaseBackupAndLogOverlap: true);

        var decision = BackupPlanRules.Evaluate(request);

        Assert.Equal(BackupAdmissionStatus.LogTechnicalFactsRequired, decision.Status);
        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void A05UnknownIdentityOrOfflineTargetIsNotReady()
    {
        var mismatched = LogTechnicalReadinessRules.Evaluate(LogTestData.ReadyFacts() with
        {
            SourceDatabase = new DbBackupManager.Domain.BackupSets.BackupDatabaseIdentity(
                LogTestData.Id(90),
                LogTestData.Id(91)),
        });
        var offline = LogTechnicalReadinessRules.Evaluate(LogTestData.ReadyFacts() with { IsOnline = false });
        var unknown = LogTechnicalReadinessRules.Evaluate(LogTestData.ReadyFacts() with
        {
            ObservationStatus = LogEvidenceStatus.PermissionDenied,
        });

        Assert.Equal(LogTechnicalReadiness.NotReady, mismatched.Readiness);
        Assert.Equal(LogReason.IdentityMismatch, mismatched.ReasonCode);
        Assert.Equal(LogTechnicalReadiness.NotReady, offline.Readiness);
        Assert.Equal(LogReason.DatabaseOffline, offline.ReasonCode);
        Assert.Equal(LogTechnicalReadiness.Unknown, unknown.Readiness);
        Assert.Equal(LogReason.ObservationPermissionDenied, unknown.ReasonCode);
    }

    [Fact]
    public void A05UnsupportedCapabilityIsReportedSeparately()
    {
        var decision = LogTechnicalReadinessRules.Evaluate(
            LogTestData.ReadyFacts() with { Capability = LogCapabilityState.Unsupported });

        Assert.Equal(LogTechnicalReadiness.Unsupported, decision.Readiness);
        Assert.Equal(LogReason.CapabilityUnsupported, decision.ReasonCode);
    }

    [Fact]
    public void A07ActiveOrTerminationUnknownBlocksNewSlotsAndManualRequests()
    {
        var active = LogPriorCallRules.Evaluate(
            new LogPriorCallFacts { State = LogPriorCallState.Active },
            LogRequestIntent.FutureScheduledSlot);
        var activeAttempt = LogPriorCallRules.Evaluate(
            new LogPriorCallFacts
            {
                State = LogPriorCallState.TerminatedResultUnknown,
                HasActiveAttempt = true,
            },
            LogRequestIntent.Manual);
        var unknown = LogPriorCallRules.Evaluate(
            new LogPriorCallFacts { State = LogPriorCallState.TerminationUnknown },
            LogRequestIntent.Manual);
        var outstandingPermit = LogPriorCallRules.Evaluate(
            new LogPriorCallFacts
            {
                State = LogPriorCallState.TerminatedResultUnknown,
                HasOutstandingPermit = true,
            },
            LogRequestIntent.FutureScheduledSlot);

        foreach (var decision in new[] { active, activeAttempt, unknown, outstandingPermit })
        {
            Assert.False(decision.MayEnterAdmission);
            Assert.False(decision.ReusesOriginalInvocation);
            Assert.False(decision.GrantsSqlInvocation);
        }

        Assert.Equal(LogReason.PriorCallActive, active.ReasonCode);
        Assert.Equal(LogReason.PriorCallActive, activeAttempt.ReasonCode);
        Assert.Equal(LogReason.PriorCallTerminationUnknown, unknown.ReasonCode);
        Assert.Equal(LogReason.PriorPermitOutstanding, outstandingPermit.ReasonCode);
    }

    [Fact]
    public void A07PermissionInvisibleTerminationEvidenceStaysBlocked()
    {
        var decision = LogPriorCallRules.Evaluate(
            new LogPriorCallFacts
            {
                State = LogPriorCallState.TerminatedResultUnknown,
                TerminationEvidence = LogEvidenceStatus.PermissionDenied,
            },
            LogRequestIntent.FutureScheduledSlot);

        Assert.False(decision.MayEnterAdmission);
        Assert.Equal(LogReason.PriorCallTerminationUnknown, decision.ReasonCode);
    }

    [Fact]
    public void A07OriginalSlotReplayNeverReusesTheOriginalPermit()
    {
        var decision = LogPriorCallRules.Evaluate(
            new LogPriorCallFacts { State = LogPriorCallState.TerminatedResultUnknown },
            LogRequestIntent.OriginalSlotReplay);

        Assert.False(decision.MayEnterAdmission);
        Assert.False(decision.ReusesOriginalInvocation);
        Assert.False(decision.GrantsSqlInvocation);
        Assert.Equal(LogReason.OriginalSlotNotReplayable, decision.ReasonCode);
    }

    [Theory]
    [InlineData(LogRequestIntent.FutureScheduledSlot)]
    [InlineData(LogRequestIntent.Manual)]
    public void A07TerminatedUnknownResultOnlyOpensIndependentFutureAdmission(LogRequestIntent intent)
    {
        var decision = LogPriorCallRules.Evaluate(
            new LogPriorCallFacts { State = LogPriorCallState.TerminatedResultUnknown },
            intent);

        Assert.True(decision.MayEnterAdmission);
        Assert.False(decision.ReusesOriginalInvocation);
        Assert.True(decision.PreservesPriorUnknown);
        Assert.False(decision.GrantsSqlInvocation);
        Assert.Equal(LogReason.FutureRequestIndependent, decision.ReasonCode);
    }

    [Theory]
    [InlineData(LogPriorCallState.TerminatedSucceeded)]
    [InlineData(LogPriorCallState.TerminatedFailed)]
    public void A07TerminatedKnownResultMayOnlyEnterFutureAdmission(LogPriorCallState state)
    {
        var decision = LogPriorCallRules.Evaluate(
            new LogPriorCallFacts { State = state },
            LogRequestIntent.FutureScheduledSlot);

        Assert.True(decision.MayEnterAdmission);
        Assert.False(decision.ReusesOriginalInvocation);
        Assert.False(decision.PreservesPriorUnknown);
        Assert.False(decision.GrantsSqlInvocation);
        Assert.Equal(LogReason.PriorCallTerminated, decision.ReasonCode);
    }

    [Fact]
    public void A07NoPriorCallDoesNotAuthorizeInvocation()
    {
        var decision = LogPriorCallRules.Evaluate(
            new LogPriorCallFacts { State = LogPriorCallState.None },
            LogRequestIntent.Manual);

        Assert.True(decision.MayEnterAdmission);
        Assert.False(decision.GrantsSqlInvocation);
        Assert.Equal(LogReason.NoPriorCall, decision.ReasonCode);
    }
}
