using DbBackupManager.Domain.BackupLogs;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupLogs;

public sealed class LogCoverageRulesTests
{
    [Theory]
    [InlineData(200L, 300L, true)]
    [InlineData(150L, 300L, true)]
    [InlineData(100L, 200L, false)]
    public void A01IntervalMustCoverFrontierToAdvance(long first, long last, bool advances)
    {
        var result = Evaluate(LogCandidate(LogTestData.Id(100), first, last));

        if (advances)
        {
            Assert.Equal(300m, result.FrontierLsn!.Value.Value);
            Assert.Single(result.UsedBackupSetIds);
            Assert.Equal(LogTestData.Id(100), result.UsedBackupSetIds[0]);
            Assert.Equal(LogCoverageConclusion.Continuous, result.Conclusion);
        }
        else
        {
            Assert.Equal(200m, result.FrontierLsn!.Value.Value);
            Assert.Empty(result.UsedBackupSetIds);
        }
    }

    [Theory]
    [InlineData(300L, 200L)]
    [InlineData(200L, 200L)]
    public void A01ReverseOrZeroLengthIntervalNeverParticipates(long first, long last)
    {
        var id = LogTestData.Id(101);
        var result = Evaluate(LogCandidate(id, first, last));

        Assert.Equal(200m, result.FrontierLsn!.Value.Value);
        Assert.DoesNotContain(id, result.UsedBackupSetIds);
        Assert.NotEqual(LogCoverageConclusion.Continuous, result.Conclusion);
    }

    [Fact]
    public void A02InputOrderCannotSkipBridgeableCandidates()
    {
        var first = LogCandidate(LogTestData.Id(110), 200, 250);
        var second = LogCandidate(LogTestData.Id(111), 250, 300);
        var unrelated = LogCandidate(LogTestData.Id(112), 100, 150);

        var forward = Evaluate(first, second, unrelated);
        var reversed = Evaluate(unrelated, second, first);

        Assert.Equal(300m, forward.FrontierLsn!.Value.Value);
        Assert.Equal(forward.FrontierLsn, reversed.FrontierLsn);
        Assert.Equal(
            forward.UsedBackupSetIds.OrderBy(id => id).ToArray(),
            reversed.UsedBackupSetIds.OrderBy(id => id).ToArray());
        Assert.DoesNotContain(LogTestData.Id(112), forward.UsedBackupSetIds);
    }

    [Fact]
    public void A02LaterIntervalEnumeratedFirstDoesNotCreateGap()
    {
        var later = LogCandidate(LogTestData.Id(120), 240, 300);
        var bridge = LogCandidate(LogTestData.Id(121), 200, 240);

        var result = Evaluate(later, bridge);

        Assert.Equal(300m, result.FrontierLsn!.Value.Value);
        Assert.Equal(2, result.UsedBackupSetIds.Count);
        Assert.Equal(LogCoverageConclusion.Continuous, result.Conclusion);
        Assert.Empty(result.KnownGaps);
    }

    [Fact]
    public void A02OverlappingIntervalsAdvanceWithoutRequiringAnExactPredecessor()
    {
        var result = Evaluate(
            LogCandidate(LogTestData.Id(122), 250, 320),
            LogCandidate(LogTestData.Id(123), 200, 300));

        Assert.Equal(320m, result.FrontierLsn!.Value.Value);
        Assert.Equal(2, result.UsedBackupSetIds.Count);
        Assert.Equal(LogCoverageConclusion.Continuous, result.Conclusion);
    }

    [Fact]
    public void A03MissingCopyAloneDoesNotProvePathGap()
    {
        var result = LogCoverageRules.Evaluate(new LogCoverageRequest(
            LogTestData.AvailableStart(200),
            enumerationComplete: true,
            [
                LogTestData.LogCandidate(
                    LogTestData.Id(133),
                    200,
                    300,
                    copy: LogLocationCopyState.Missing),
            ]));

        Assert.Equal(LogCoverageConclusion.Unknown, result.Conclusion);
        Assert.Empty(result.UsedBackupSetIds);
        Assert.Empty(result.KnownGaps);
        Assert.NotEmpty(result.UnknownRanges);
        Assert.Contains(LogReason.CopyMissing, result.ReasonCodes);
        Assert.Equal(200m, result.FrontierLsn!.Value.Value);
    }

    [Fact]
    public void A03UnresolvedCandidateAfterFrontierStaysUnknown()
    {
        var result = Evaluate(
            LogCandidate(
                LogTestData.Id(134),
                210,
                280,
                LogTestData.Metadata(
                    first: 210,
                    last: 280,
                    status: LogEvidenceStatus.MissingFields)),
            LogCandidate(LogTestData.Id(135), 300, 400));

        Assert.Equal(LogCoverageConclusion.Unknown, result.Conclusion);
        Assert.Empty(result.KnownGaps);
        Assert.NotEmpty(result.UnknownRanges);
    }

    [Fact]
    public void A03MissingCopyAndOtherCandidateDoNotInventGap()
    {
        var result = LogCoverageRules.Evaluate(new LogCoverageRequest(
            LogTestData.AvailableStart(200),
            enumerationComplete: true,
            [
                LogTestData.LogCandidate(
                    LogTestData.Id(136),
                    200,
                    400,
                    copy: LogLocationCopyState.Missing),
                LogCandidate(LogTestData.Id(137), 300, 320),
            ]));

        Assert.Equal(LogCoverageConclusion.Unknown, result.Conclusion);
        Assert.Empty(result.UsedBackupSetIds);
        Assert.Empty(result.KnownGaps);
        Assert.Contains(LogReason.CopyMissing, result.ReasonCodes);
    }

    [Fact]
    public void A03ExternalLogIsNotAutomaticallyImportedAsCoverage()
    {
        var external = LogCandidate(
            LogTestData.Id(1400),
            200,
            300,
            managed: false);
        var result = Evaluate(external);

        Assert.Equal(LogCoverageConclusion.Unknown, result.Conclusion);
        Assert.Empty(result.UsedBackupSetIds);
        Assert.Contains(LogReason.ExternalNotManaged, result.ReasonCodes);
        Assert.Equal(200m, result.FrontierLsn!.Value.Value);
    }

    [Fact]
    public void A03MissingCopyAndUnresolvedEvidenceDoNotInventGap()
    {
        var result = LogCoverageRules.Evaluate(new LogCoverageRequest(
            LogTestData.AvailableStart(200),
            enumerationComplete: true,
            [
                LogTestData.LogCandidate(
                    LogTestData.Id(138),
                    200,
                    300,
                    copy: LogLocationCopyState.Missing),
                LogCandidate(
                    LogTestData.Id(139),
                    null,
                    null,
                    LogTestData.Metadata(
                        first: null,
                        last: null,
                        status: LogEvidenceStatus.MissingFields)),
            ]));

        Assert.Equal(LogCoverageConclusion.Unknown, result.Conclusion);
        Assert.Empty(result.KnownGaps);
        Assert.NotEmpty(result.UnknownRanges);
        Assert.Contains(LogReason.CopyMissing, result.ReasonCodes);
        Assert.Contains(LogReason.CoverageUnknown, result.ReasonCodes);
    }

    [Fact]
    public void A03CompleteQueryWithoutBridgeCanReportGap()
    {
        var result = Evaluate(LogCandidate(LogTestData.Id(130), 201, 300));

        Assert.Equal(LogCoverageConclusion.Gap, result.Conclusion);
        Assert.Equal(200m, result.FrontierLsn!.Value.Value);
        var gap = Assert.Single(result.KnownGaps);
        Assert.Equal(200m, gap.FromLsn!.Value.Value);
        Assert.Equal(201m, gap.ToLsn!.Value.Value);
        Assert.Empty(result.UsedBackupSetIds);
    }

    [Fact]
    public void A03CompleteEnumerationDoesNotRemoveUnresolvedBridgeableCandidate()
    {
        var unresolved = LogCandidate(
            LogTestData.Id(131),
            null,
            null,
            LogTestData.Metadata(
                first: null,
                last: null,
                status: LogEvidenceStatus.MissingFields));

        var result = Evaluate(
            LogCandidate(LogTestData.Id(132), 201, 300),
            unresolved);

        Assert.Equal(LogCoverageConclusion.Unknown, result.Conclusion);
        Assert.Empty(result.KnownGaps);
        Assert.NotEmpty(result.UnknownRanges);
        Assert.Contains(LogReason.ActualEvidenceUnknown, result.ReasonCodes);
        Assert.Equal(200m, result.FrontierLsn!.Value.Value);
    }

    [Fact]
    public void A04KnownIdentityGenerationOrBranchMismatchIsUnsupportedBranch()
    {
        var identity = Evaluate(LogCandidate(
            LogTestData.Id(140),
            200,
            300,
            LogTestData.Metadata(database: new BackupDatabaseIdentity(LogTestData.Id(90), LogTestData.Id(91)))));
        var generation = Evaluate(LogCandidate(
            LogTestData.Id(141),
            200,
            300,
            LogTestData.Metadata(generation: new LogChainGeneration(LogTestData.Id(92)))));
        var branch = Evaluate(LogCandidate(
            LogTestData.Id(142),
            200,
            300,
            LogTestData.Metadata(branch: new BackupRecoveryBranch(LogTestData.Id(93), LogTestData.Id(93)))));
        var spanningFork = Evaluate(LogCandidate(
            LogTestData.Id(143),
            200,
            300,
            LogTestData.Metadata(branch: new BackupRecoveryBranch(LogTestData.Id(4), LogTestData.Id(94)))));

        foreach (var result in new[] { identity, generation, branch, spanningFork })
        {
            Assert.Equal(LogCoverageConclusion.UnsupportedBranch, result.Conclusion);
            Assert.Empty(result.UsedBackupSetIds);
            Assert.NotEmpty(result.UnsupportedRanges);
            Assert.Equal(200m, result.FrontierLsn!.Value.Value);
        }

        Assert.Contains(LogReason.ActualIdentityUnsupported, identity.ReasonCodes);
        Assert.Contains(LogReason.ActualGenerationUnsupported, generation.ReasonCodes);
        Assert.Contains(LogReason.ActualBranchUnsupported, branch.ReasonCodes);
        Assert.Contains(LogReason.ActualBranchUnsupported, spanningFork.ReasonCodes);
    }

    [Fact]
    public void A04MissingForkIsUnknownNotUnsupported()
    {
        var result = Evaluate(LogCandidate(
            LogTestData.Id(150),
            200,
            300,
            LogTestData.Metadata(branch: new BackupRecoveryBranch(null, null))));

        Assert.Equal(LogCoverageConclusion.Unknown, result.Conclusion);
        Assert.Empty(result.UsedBackupSetIds);
        Assert.NotEmpty(result.UnknownRanges);
        Assert.Contains(LogReason.ActualBranchUnknown, result.ReasonCodes);
    }

    [Fact]
    public void A04MissingGenerationIsUnknownNotSilentlyConnected()
    {
        var result = Evaluate(LogCandidate(
            LogTestData.Id(153),
            200,
            300,
            LogTestData.Metadata(generation: new LogChainGeneration(null))));

        Assert.Equal(LogCoverageConclusion.Unknown, result.Conclusion);
        Assert.Empty(result.UsedBackupSetIds);
        Assert.Contains(LogReason.ActualGenerationUnknown, result.ReasonCodes);
    }

    [Fact]
    public void A04SameBackupWithOneCompleteObservationKeepsTheCompleteFacts()
    {
        var id = LogTestData.Id(154);
        var result = Evaluate(
            LogCandidate(id, 200, 300, LogTestData.Metadata(status: LogEvidenceStatus.MissingFields)),
            LogCandidate(id, 200, 300));

        Assert.Equal(LogCoverageConclusion.Continuous, result.Conclusion);
        Assert.Single(result.UsedBackupSetIds);
        Assert.Equal(id, result.UsedBackupSetIds[0]);
    }

    [Fact]
    public void A04SameBackupIdentityWithDifferentFactsIsNotSilentlyDeduplicated()
    {
        var id = LogTestData.Id(151);
        var result = Evaluate(
            LogCandidate(id, 200, 300),
            LogCandidate(id, 200, 310));

        Assert.Equal(LogCoverageConclusion.Unknown, result.Conclusion);
        Assert.Empty(result.UsedBackupSetIds);
        Assert.Contains(LogReason.DuplicateIdentityConflict, result.ReasonCodes);
        Assert.Contains(id, result.RejectedBackupSetIds);
    }

    [Fact]
    public void A04SameBackupIdentityWithSameFactsIsRegisteredOnce()
    {
        var id = LogTestData.Id(152);
        var result = Evaluate(
            LogCandidate(id, 200, 300),
            LogCandidate(id, 200, 300));

        Assert.Equal(LogCoverageConclusion.Continuous, result.Conclusion);
        Assert.Single(result.UsedBackupSetIds);
        Assert.Equal(id, result.UsedBackupSetIds[0]);
        Assert.Equal(300m, result.FrontierLsn!.Value.Value);
    }

    [Fact]
    public void A06FirstManagedLogBeginsLogChainFalseAloneDoesNotRejectCoverage()
    {
        var result = Evaluate(LogCandidate(
            LogTestData.Id(160),
            200,
            300,
            LogTestData.Metadata(beginsLogChain: false)));

        Assert.Equal(LogCoverageConclusion.Continuous, result.Conclusion);
        Assert.Single(result.UsedBackupSetIds);
        Assert.Equal(LogTestData.Id(160), result.UsedBackupSetIds[0]);
    }

    [Theory]
    [InlineData("bulk")]
    [InlineData("bulk-missing")]
    [InlineData("recovery-unknown")]
    [InlineData("recovery-simple")]
    [InlineData("copy-only")]
    [InlineData("force-offline")]
    [InlineData("force-offline-missing")]
    [InlineData("damaged")]
    [InlineData("snapshot")]
    [InlineData("incomplete")]
    [InlineData("damaged-missing")]
    [InlineData("source-conflict")]
    [InlineData("checksum-mismatch")]
    [InlineData("compression-mismatch")]
    [InlineData("protection-missing")]
    public void A06ActualInvalidOrConflictingMetadataNeverAdvancesCoverage(string scenario)
    {
        var metadata = scenario switch
        {
            "bulk" => LogTestData.Metadata(bulk: true),
            "bulk-missing" => LogTestData.Metadata(bulk: null),
            "recovery-unknown" => LogTestData.Metadata(recoveryModel: DatabaseRecoveryModel.Unknown),
            "recovery-simple" => LogTestData.Metadata(recoveryModel: DatabaseRecoveryModel.Simple),
            "copy-only" => LogTestData.Metadata(copyOnly: true),
            "force-offline" => LogTestData.Metadata(forceOffline: true),
            "force-offline-missing" => LogTestData.Metadata(forceOffline: null),
            "damaged" => LogTestData.Metadata(damaged: true),
            "snapshot" => LogTestData.Metadata(snapshot: true),
            "incomplete" => LogTestData.Metadata(incomplete: true),
            "damaged-missing" => LogTestData.Metadata(damaged: null),
            "source-conflict" => LogTestData.Metadata(status: LogEvidenceStatus.SourceConflict),
            "checksum-mismatch" => LogTestData.Metadata(checksum: false),
            "compression-mismatch" => LogTestData.Metadata(compression: true),
            "protection-missing" => LogTestData.Metadata(checksum: null),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "未知场景。"),
        };
        var result = Evaluate(LogCandidate(LogTestData.Id(161), 200, 300, metadata));

        Assert.NotEqual(LogCoverageConclusion.Continuous, result.Conclusion);
        Assert.Empty(result.UsedBackupSetIds);
        Assert.Equal(200m, result.FrontierLsn!.Value.Value);
    }

    [Fact]
    public void A08DataStartUsesFullLastLsnNotFirstDatabaseBackupLsnOrCompletionTime()
    {
        var full = LogTestData.FullCandidate(last: 250, first: 100, databaseBackupLsn: 150);
        full = full with
        {
            Actual = full.Actual with
            {
                CompletedAtUtc = LogTestData.Utc(2026, 10, 23, 9, 55),
            },
        };
        var start = LogDataStartRules.Select(new LogDataStartRequest(
            LogLocationScope.Local,
            [full]));

        Assert.Equal(LogDataStartAvailability.Available, start.Availability);
        Assert.Equal(250m, start.RestorePointLsn!.Value.Value);
        Assert.NotEqual(100m, start.RestorePointLsn.Value.Value);
        Assert.NotEqual(150m, start.RestorePointLsn.Value.Value);

        var coverage = LogCoverageRules.Evaluate(new LogCoverageRequest(
            start,
            enumerationComplete: true,
            [LogCandidate(LogTestData.Id(170), 200, 300)]));

        Assert.Equal(LogCoverageConclusion.Continuous, coverage.Conclusion);
        Assert.Equal(300m, coverage.FrontierLsn!.Value.Value);
    }

    [Fact]
    public void A08ActualMatchingDifferentialCanAdvanceTheRestorePoint()
    {
        var differentialId = LogTestData.Id(171);
        var start = LogDataStartRules.Select(new LogDataStartRequest(
            LogLocationScope.Local,
            [
                LogTestData.FullCandidate(last: 250),
                LogTestData.DifferentialCandidate(differentialId, LogTestData.FullId, last: 280),
            ]));

        Assert.Equal(LogDataStartAvailability.Available, start.Availability);
        Assert.Equal(280m, start.RestorePointLsn!.Value.Value);
        Assert.Equal(differentialId, start.DifferentialBackupSetId);

        var coverage = LogCoverageRules.Evaluate(new LogCoverageRequest(
            start,
            enumerationComplete: true,
            [LogCandidate(LogTestData.Id(172), 200, 300)]));

        Assert.Equal(LogCoverageConclusion.Continuous, coverage.Conclusion);
        Assert.Equal(300m, coverage.FrontierLsn!.Value.Value);
    }

    [Fact]
    public void A08DamagedOrIncompleteFullIsNotAValidStart()
    {
        var damaged = LogTestData.FullCandidate(last: 250) with
        {
            Actual = LogTestData.Metadata(
                first: 100,
                last: 250,
                type: BackupType.Full,
                damaged: true),
        };
        var unknown = LogTestData.FullCandidate(last: 250) with
        {
            Actual = LogTestData.Metadata(
                first: 100,
                last: 250,
                type: BackupType.Full,
                damaged: null),
        };

        var damagedStart = LogDataStartRules.Select(new LogDataStartRequest(
            LogLocationScope.Local,
            [damaged]));
        var unknownStart = LogDataStartRules.Select(new LogDataStartRequest(
            LogLocationScope.Local,
            [unknown]));

        Assert.Equal(LogDataStartAvailability.Missing, damagedStart.Availability);
        Assert.Equal(LogReason.StartBackupInvalid, damagedStart.ReasonCode);
        Assert.Equal(LogDataStartAvailability.Unknown, unknownStart.Availability);
        Assert.Equal(LogReason.StartEvidenceUnknown, unknownStart.ReasonCode);
    }

    [Fact]
    public void A08MissingCopyOnlyEvidenceIsUnknownNotMissing()
    {
        var full = LogTestData.FullCandidate(last: 250) with
        {
            Actual = LogTestData.Metadata(
                first: 100,
                last: 250,
                type: BackupType.Full,
                copyOnly: null),
        };
        var start = LogDataStartRules.Select(new LogDataStartRequest(
            LogLocationScope.Local,
            [full],
            preferredFullBackupSetId: LogTestData.FullId));

        Assert.Equal(LogDataStartAvailability.Unknown, start.Availability);
        Assert.Equal(LogReason.StartEvidenceUnknown, start.ReasonCode);
    }

    [Fact]
    public void A08CopyOnlyOrExternalFullIsNotAnAutomaticStart()
    {
        var copyOnly = LogDataStartRules.Select(new LogDataStartRequest(
            LogLocationScope.Local,
            [LogTestData.FullCandidate(copyOnly: true)]));
        var external = LogDataStartRules.Select(new LogDataStartRequest(
            LogLocationScope.Local,
            [LogTestData.FullCandidate(managed: false)]));

        Assert.Equal(LogDataStartAvailability.Missing, copyOnly.Availability);
        Assert.Equal(LogReason.StartCopyOnlyNotEligible, copyOnly.ReasonCode);
        Assert.Equal(LogDataStartAvailability.Missing, external.Availability);
        Assert.Equal(LogReason.StartExternalNotManaged, external.ReasonCode);
    }

    [Fact]
    public void A08RemoteLogCannotFillLocalCoverage()
    {
        var localStart = LogTestData.AvailableStart(200);
        var remoteLog = LogTestData.LogCandidate(
            LogTestData.Id(173),
            200,
            300,
            scope: LogLocationScope.Remote(LogTestData.Id(900)));
        var result = LogCoverageRules.Evaluate(new LogCoverageRequest(
            localStart,
            enumerationComplete: true,
            [remoteLog]));

        Assert.Equal(LogCoverageConclusion.Gap, result.Conclusion);
        Assert.Equal(200m, result.FrontierLsn!.Value.Value);
        Assert.Empty(result.UsedBackupSetIds);
    }

    [Fact]
    public void A08RemoteLocationCannotBorrowLocalFullForARestorableConclusion()
    {
        var localStart = LogTestData.AvailableStart(200);
        var remoteStart = LogDataStartRules.Select(new LogDataStartRequest(
            LogLocationScope.Remote(LogTestData.Id(900)),
            [
                new LogDataStartCandidate
                {
                    BackupSetId = LogTestData.Id(180),
                    Actual = LogTestData.Metadata(200, 300),
                    Scope = LogLocationScope.Remote(LogTestData.Id(900)),
                },
            ]));

        Assert.Equal(LogDataStartAvailability.Available, localStart.Availability);
        Assert.Equal(LogDataStartAvailability.Missing, remoteStart.Availability);
        Assert.Null(remoteStart.RestorePointLsn);

        var coverage = LogCoverageRules.Evaluate(new LogCoverageRequest(
            remoteStart,
            enumerationComplete: true,
            [
                LogTestData.LogCandidate(
                    LogTestData.Id(181),
                    200,
                    300,
                    scope: LogLocationScope.Remote(LogTestData.Id(900))),
            ]));

        Assert.Equal(LogCoverageConclusion.Unknown, coverage.Conclusion);
        Assert.Null(coverage.FrontierLsn);
        Assert.Empty(coverage.UsedBackupSetIds);
        Assert.Contains(LogReason.StartNotAvailable, coverage.ReasonCodes);
    }

    [Fact]
    public void A09AdjacentMaximumAndZeroLsnsRemainExact()
    {
        var maximum = new BackupLsn(BackupLsn.MaximumValue);
        var neighbor = new BackupLsn(BackupLsn.MaximumValue - 1m);
        var zero = new BackupLsn(0m);

        Assert.Equal(BackupLsn.MaximumValue, maximum.Value);
        Assert.Equal(BackupLsn.MaximumValue - 1m, neighbor.Value);
        Assert.Equal(0m, zero.Value);
        Assert.NotEqual(maximum, neighbor);

        var maximumStart = LogDataStartRules.Select(new LogDataStartRequest(
            LogLocationScope.Local,
            [LogTestData.FullCandidate(last: BackupLsn.MaximumValue - 1m)]));
        var maximumResult = LogCoverageRules.Evaluate(new LogCoverageRequest(
            maximumStart,
            enumerationComplete: true,
            [
                LogCandidate(
                    LogTestData.Id(190),
                    BackupLsn.MaximumValue - 1m,
                    BackupLsn.MaximumValue),
            ]));

        Assert.Equal(BackupLsn.MaximumValue, maximumResult.FrontierLsn!.Value.Value);

        var zeroStart = LogDataStartRules.Select(new LogDataStartRequest(
            LogLocationScope.Local,
            [LogTestData.FullCandidate(last: 0, first: 0)]));
        var zeroResult = LogCoverageRules.Evaluate(new LogCoverageRequest(
            zeroStart,
            enumerationComplete: true,
            [LogCandidate(LogTestData.Id(191), 0, 1)]));

        Assert.Equal(1m, zeroResult.FrontierLsn!.Value.Value);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0.5)]
    public void A09FractionalOrNegativeLsnIsRejected(decimal value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackupLsn(value));
    }

    [Fact]
    public void A09OutOfRangeLsnIsRejectedWithoutNarrowing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BackupLsn(BackupLsn.MaximumValue + 1m));
        Assert.Equal(
            9999999999999999999999998m,
            new BackupLsn(9999999999999999999999998m).Value);
    }

    private static LogCoverageResult Evaluate(params LogCoverageCandidate[] candidates)
    {
        return LogCoverageRules.Evaluate(LogTestData.CoverageRequest(
            LogTestData.AvailableStart(200),
            enumerationComplete: true,
            candidates));
    }

    private static LogCoverageCandidate LogCandidate(
        Guid id,
        decimal? first,
        decimal? last,
        LogBackupActualMetadata? actual = null,
        LogLocationScope? scope = null,
        bool managed = true)
    {
        return LogTestData.LogCandidate(id, first, last, actual, scope, managed: managed);
    }
}
