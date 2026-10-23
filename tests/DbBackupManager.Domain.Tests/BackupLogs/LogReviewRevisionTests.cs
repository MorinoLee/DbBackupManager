using DbBackupManager.Domain.BackupLogs;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupLogs;

public sealed class LogReviewRevisionTests
{
    [Fact]
    public void F01PreferredFullIdentityConflictIsOrderIndependentAndHasNoStart()
    {
        var id = LogTestData.Id(200);
        var first = LogTestData.FullCandidate(id: id, last: 200);
        var second = LogTestData.FullCandidate(id: id, last: 250);

        var forward = Select([first, second], preferredFullBackupSetId: id);
        var reversed = Select([second, first], preferredFullBackupSetId: id);

        AssertStartConflict(forward, id, LogFactField.LastLsn);
        AssertStartConflict(reversed, id, LogFactField.LastLsn);

        var coverage = Evaluate(forward, LogTestData.LogCandidate(LogTestData.Id(201), 250, 300));
        Assert.Equal(LogCoverageConclusion.Unknown, coverage.Conclusion);
        Assert.Null(coverage.FrontierLsn);
        Assert.Empty(coverage.UsedBackupSetIds);
    }

    [Fact]
    public void F01AutomaticStartSkipsConflictingFullButKeepsTheConflict()
    {
        var conflictId = LogTestData.Id(202);
        var cleanId = LogTestData.Id(203);
        var conflicting = new[]
        {
            LogTestData.FullCandidate(id: conflictId, last: 200),
            LogTestData.FullCandidate(id: conflictId, last: 250),
        };
        var clean = LogTestData.FullCandidate(id: cleanId, last: 180);

        var forward = Select([conflicting[0], conflicting[1], clean]);
        var reversed = Select([clean, conflicting[1], conflicting[0]]);

        Assert.Equal(LogDataStartAvailability.Available, forward.Availability);
        Assert.Equal(cleanId, forward.FullBackupSetId);
        Assert.Equal(180m, forward.RestorePointLsn!.Value.Value);
        var conflict = Assert.Single(forward.Conflicts);
        Assert.Equal(conflictId, conflict.BackupSetId);
        Assert.Equal(LogFactField.LastLsn, conflict.FieldCode);
        Assert.Equal(forward.FullBackupSetId, reversed.FullBackupSetId);
        Assert.Equal(forward.RestorePointLsn, reversed.RestorePointLsn);
    }

    [Fact]
    public void F01DifferentialIdentityConflictIsNotSelectedAsTheStart()
    {
        var differentialId = LogTestData.Id(204);
        var full = LogTestData.FullCandidate(last: 200);
        var first = LogTestData.DifferentialCandidate(differentialId, LogTestData.FullId, last: 250);
        var second = LogTestData.DifferentialCandidate(differentialId, LogTestData.FullId, last: 280);

        var forward = Select([full, first, second]);
        var reversed = Select([second, full, first]);

        Assert.Equal(LogDataStartAvailability.Available, forward.Availability);
        Assert.Equal(200m, forward.RestorePointLsn!.Value.Value);
        Assert.Null(forward.DifferentialBackupSetId);
        var conflict = Assert.Single(forward.Conflicts);
        Assert.Equal(differentialId, conflict.BackupSetId);
        Assert.Equal(forward.RestorePointLsn, reversed.RestorePointLsn);
        Assert.Null(reversed.DifferentialBackupSetId);
    }

    [Fact]
    public void F01IdenticalDuplicateFullIsUsableAndOrderIndependent()
    {
        var id = LogTestData.Id(205);
        var first = LogTestData.FullCandidate(id: id, last: 250);
        var second = LogTestData.FullCandidate(id: id, last: 250);

        var forward = Select([first, second]);
        var reversed = Select([second, first]);

        Assert.Equal(LogDataStartAvailability.Available, forward.Availability);
        Assert.Equal(250m, forward.RestorePointLsn!.Value.Value);
        Assert.Empty(forward.Conflicts);
        Assert.Equal(forward.RestorePointLsn, reversed.RestorePointLsn);
        Assert.Empty(reversed.Conflicts);
    }

    [Fact]
    public void F02BeginsLogChainConflictDoesNotAdvanceCoverage()
    {
        var id = LogTestData.Id(210);
        var first = LogTestData.LogCandidate(
            id,
            200,
            300,
            LogTestData.Metadata(beginsLogChain: true));
        var second = LogTestData.LogCandidate(
            id,
            200,
            300,
            LogTestData.Metadata(beginsLogChain: false));

        var forward = Evaluate(LogTestData.AvailableStart(200), first, second);
        var reversed = Evaluate(LogTestData.AvailableStart(200), second, first);

        AssertCoverageConflict(forward, id, LogFactField.BeginsLogChain);
        AssertCoverageConflict(reversed, id, LogFactField.BeginsLogChain);
    }

    [Theory]
    [InlineData("first-lsn")]
    [InlineData("last-lsn")]
    [InlineData("database-backup-lsn")]
    [InlineData("fork-point-lsn")]
    public void F02OtherChainFactsMustAlsoAgree(string field)
    {
        var id = LogTestData.Id(211 + field.Length);
        var left = field switch
        {
            "first-lsn" => LogTestData.Metadata(first: 200, last: 300),
            "last-lsn" => LogTestData.Metadata(first: 200, last: 300),
            "database-backup-lsn" => LogTestData.Metadata(databaseBackupLsn: 150),
            _ => LogTestData.Metadata(forkPointLsn: 220),
        };
        var right = field switch
        {
            "first-lsn" => LogTestData.Metadata(first: 201, last: 300),
            "last-lsn" => LogTestData.Metadata(first: 200, last: 310),
            "database-backup-lsn" => LogTestData.Metadata(databaseBackupLsn: 151),
            _ => LogTestData.Metadata(forkPointLsn: 221),
        };
        var expectedField = field switch
        {
            "first-lsn" => LogFactField.FirstLsn,
            "last-lsn" => LogFactField.LastLsn,
            "database-backup-lsn" => LogFactField.DatabaseBackupLsn,
            _ => LogFactField.ForkPointLsn,
        };

        var result = Evaluate(
            LogTestData.AvailableStart(200),
            LogTestData.LogCandidate(id, 200, 300, left),
            LogTestData.LogCandidate(id, 200, 300, right));

        AssertCoverageConflict(result, id, expectedField);
    }

    [Fact]
    public void F02ComplementaryMissingFieldCanBeCompleted()
    {
        var id = LogTestData.Id(220);
        var complete = LogTestData.LogCandidate(
            id,
            200,
            300,
            LogTestData.Metadata(beginsLogChain: null));
        var partial = LogTestData.LogCandidate(
            id,
            200,
            300,
            LogTestData.Metadata(
                beginsLogChain: false,
                status: LogEvidenceStatus.MissingFields));

        var result = Evaluate(LogTestData.AvailableStart(200), complete, partial);

        Assert.Equal(LogCoverageConclusion.Continuous, result.Conclusion);
        Assert.Single(result.UsedBackupSetIds);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void F02IdenticalDuplicateLogIsRegisteredOnceAndOrderIndependent()
    {
        var id = LogTestData.Id(221);
        var first = LogTestData.LogCandidate(id, 200, 300);
        var second = LogTestData.LogCandidate(id, 200, 300);

        var forward = Evaluate(LogTestData.AvailableStart(200), first, second);
        var reversed = Evaluate(LogTestData.AvailableStart(200), second, first);

        Assert.Equal(LogCoverageConclusion.Continuous, forward.Conclusion);
        Assert.Single(forward.UsedBackupSetIds);
        Assert.Empty(forward.Conflicts);
        Assert.Equal(forward.UsedBackupSetIds, reversed.UsedBackupSetIds);
        Assert.Equal(forward.FrontierLsn, reversed.FrontierLsn);
    }

    [Fact]
    public void F03SourceConflictBoundaryCannotProveAGap()
    {
        var result = Evaluate(
            LogTestData.AvailableStart(200),
            LogTestData.LogCandidate(
                LogTestData.Id(230),
                210,
                300,
                LogTestData.Metadata(
                    first: 210,
                    last: 300,
                    status: LogEvidenceStatus.SourceConflict)));

        Assert.Equal(LogCoverageConclusion.Unknown, result.Conclusion);
        Assert.Empty(result.KnownGaps);
        Assert.NotEmpty(result.UnknownRanges);
        Assert.Contains(LogReason.ActualSourceConflict, result.ReasonCodes);
    }

    [Fact]
    public void F03SourceConflictAfterFrontierStillCannotExcludeBridge()
    {
        var result = Evaluate(
            LogTestData.AvailableStart(200),
            LogTestData.LogCandidate(
                LogTestData.Id(231),
                300,
                400,
                LogTestData.Metadata(
                    first: 300,
                    last: 400,
                    status: LogEvidenceStatus.SourceConflict)));

        Assert.Equal(LogCoverageConclusion.Unknown, result.Conclusion);
        Assert.Empty(result.KnownGaps);
        Assert.NotEmpty(result.UnknownRanges);
    }

    [Fact]
    public void F03SourceConflictWithInvalidIntervalStaysUnknown()
    {
        var result = Evaluate(
            LogTestData.AvailableStart(200),
            LogTestData.LogCandidate(
                LogTestData.Id(234),
                300,
                200,
                LogTestData.Metadata(
                    first: 300,
                    last: 200,
                    status: LogEvidenceStatus.SourceConflict)));

        Assert.Equal(LogCoverageConclusion.Unknown, result.Conclusion);
        Assert.Empty(result.KnownGaps);
        Assert.NotEmpty(result.UnknownRanges);
    }

    [Fact]
    public void F03UnknownCandidatesOnEitherSideOfTheFrontierStayUnknown()
    {
        var before = Evaluate(
            LogTestData.AvailableStart(200),
            LogTestData.LogCandidate(
                LogTestData.Id(235),
                100,
                200,
                LogTestData.Metadata(
                    first: 100,
                    last: 200,
                    status: LogEvidenceStatus.MissingFields)));
        var after = Evaluate(
            LogTestData.AvailableStart(200),
            LogTestData.LogCandidate(
                LogTestData.Id(236),
                300,
                400,
                LogTestData.Metadata(
                    first: 300,
                    last: 400,
                    status: LogEvidenceStatus.MissingFields)));

        Assert.Equal(LogCoverageConclusion.Unknown, before.Conclusion);
        Assert.Equal(LogCoverageConclusion.Unknown, after.Conclusion);
        Assert.Empty(before.KnownGaps);
        Assert.Empty(after.KnownGaps);
    }

    [Fact]
    public void F03SourceConflictDoesNotEraseAlreadyProvenPrefix()
    {
        var result = Evaluate(
            LogTestData.AvailableStart(200),
            LogTestData.LogCandidate(LogTestData.Id(232), 200, 250),
            LogTestData.LogCandidate(
                LogTestData.Id(233),
                250,
                300,
                LogTestData.Metadata(
                    first: 250,
                    last: 300,
                    status: LogEvidenceStatus.SourceConflict)));

        Assert.Equal(LogCoverageConclusion.Unknown, result.Conclusion);
        Assert.Equal(new[] { LogTestData.Id(232) }, result.UsedBackupSetIds);
        Assert.Equal(250m, result.FrontierLsn!.Value.Value);
        Assert.Empty(result.KnownGaps);
    }

    [Fact]
    public void F04MissingCopyOfAnotherDatabaseDoesNotCreateCurrentPathGap()
    {
        var otherDatabase = new BackupDatabaseIdentity(LogTestData.Id(90), LogTestData.Id(91));
        var result = Evaluate(
            LogTestData.AvailableStart(200),
            LogTestData.LogCandidate(
                LogTestData.Id(240),
                200,
                300,
                LogTestData.Metadata(database: otherDatabase),
                copy: LogLocationCopyState.Missing));

        Assert.Equal(LogCoverageConclusion.UnsupportedBranch, result.Conclusion);
        Assert.Empty(result.UsedBackupSetIds);
        Assert.Empty(result.KnownGaps);
        Assert.Contains(LogReason.ActualIdentityUnsupported, result.ReasonCodes);
    }

    [Fact]
    public void F04MissingCopyOfNonLogOrCrossForkDoesNotBypassItsGate()
    {
        var nonLog = Evaluate(
            LogTestData.AvailableStart(200),
            LogTestData.LogCandidate(
                LogTestData.Id(241),
                200,
                300,
                LogTestData.Metadata(type: BackupType.Full),
                copy: LogLocationCopyState.Missing));
        var crossFork = Evaluate(
            LogTestData.AvailableStart(200),
            LogTestData.LogCandidate(
                LogTestData.Id(242),
                200,
                300,
                LogTestData.Metadata(branch: new BackupRecoveryBranch(LogTestData.Id(4), LogTestData.Id(92))),
                copy: LogLocationCopyState.Missing));

        Assert.Equal(LogCoverageConclusion.Unknown, nonLog.Conclusion);
        Assert.Empty(nonLog.KnownGaps);
        Assert.Contains(LogReason.ActualTypeNotLog, nonLog.ReasonCodes);
        Assert.Equal(LogCoverageConclusion.UnsupportedBranch, crossFork.Conclusion);
        Assert.Empty(crossFork.KnownGaps);
        Assert.Contains(LogReason.ActualBranchUnsupported, crossFork.ReasonCodes);
    }

    [Fact]
    public void F04MissingCopyWithUnresolvedAlternativeDoesNotCreateGap()
    {
        var result = Evaluate(
            LogTestData.AvailableStart(200),
            LogTestData.LogCandidate(
                LogTestData.Id(243),
                200,
                300,
                copy: LogLocationCopyState.Missing),
            LogTestData.LogCandidate(
                LogTestData.Id(244),
                null,
                null,
                LogTestData.Metadata(
                    first: null,
                    last: null,
                    status: LogEvidenceStatus.MissingFields)));

        Assert.Equal(LogCoverageConclusion.Unknown, result.Conclusion);
        Assert.Empty(result.KnownGaps);
        Assert.NotEmpty(result.UnknownRanges);
        Assert.Contains(LogReason.CopyMissing, result.ReasonCodes);
    }

    [Fact]
    public void F05PerCandidateSnapshotsAllowContinuousCrossVersionProtection()
    {
        var first = LogTestData.LogCandidate(
            LogTestData.Id(250),
            200,
            250,
            LogTestData.Metadata(first: 200, last: 250, compression: false),
            protection: new LogProtectionExpectation(UseChecksum: true, UseCompression: false));
        var second = LogTestData.LogCandidate(
            LogTestData.Id(251),
            250,
            300,
            LogTestData.Metadata(first: 250, last: 300, compression: true),
            protection: new LogProtectionExpectation(UseChecksum: true, UseCompression: true));

        var result = Evaluate(LogTestData.AvailableStart(200), first, second);

        Assert.Equal(LogCoverageConclusion.Continuous, result.Conclusion);
        Assert.Equal(300m, result.FrontierLsn!.Value.Value);
        Assert.Equal(2, result.UsedBackupSetIds.Count);
    }

    [Fact]
    public void F05ChecksumChangeIsAlsoValidatedPerCandidateSnapshot()
    {
        var first = LogTestData.LogCandidate(
            LogTestData.Id(252),
            200,
            250,
            LogTestData.Metadata(first: 200, last: 250, checksum: true),
            protection: new LogProtectionExpectation(UseChecksum: true, UseCompression: false));
        var second = LogTestData.LogCandidate(
            LogTestData.Id(253),
            250,
            300,
            LogTestData.Metadata(first: 250, last: 300, checksum: false, compression: false),
            protection: new LogProtectionExpectation(UseChecksum: false, UseCompression: false));

        var result = Evaluate(LogTestData.AvailableStart(200), first, second);

        Assert.Equal(LogCoverageConclusion.Continuous, result.Conclusion);
        Assert.Equal(300m, result.FrontierLsn!.Value.Value);
    }

    [Fact]
    public void F05ActualDeviationFromOwnSnapshotIsRejected()
    {
        var valid = LogTestData.LogCandidate(
            LogTestData.Id(254),
            200,
            250,
            LogTestData.Metadata(first: 200, last: 250, compression: false),
            protection: new LogProtectionExpectation(UseChecksum: true, UseCompression: false));
        var mismatched = LogTestData.LogCandidate(
            LogTestData.Id(255),
            250,
            300,
            LogTestData.Metadata(first: 250, last: 300, compression: false),
            protection: new LogProtectionExpectation(UseChecksum: true, UseCompression: true));

        var result = Evaluate(LogTestData.AvailableStart(200), valid, mismatched);

        Assert.Equal(LogCoverageConclusion.Unknown, result.Conclusion);
        Assert.Equal(new[] { LogTestData.Id(254) }, result.UsedBackupSetIds);
        Assert.Contains(LogReason.ActualProtectionMismatch, result.ReasonCodes);
    }

    [Fact]
    public void F05MissingCandidateSnapshotCannotBypassProtectionCheck()
    {
        var candidate = new LogCoverageCandidate
        {
            BackupSetId = LogTestData.Id(256),
            Actual = LogTestData.Metadata(),
            ProtectionExpectation = null,
        };

        var result = Evaluate(LogTestData.AvailableStart(200), candidate);

        Assert.Equal(LogCoverageConclusion.Unknown, result.Conclusion);
        Assert.Empty(result.UsedBackupSetIds);
        Assert.Contains(LogReason.ActualProtectionUnknown, result.ReasonCodes);
    }

    [Fact]
    public void F06RemoteScopeRequiresConcreteTargetIdentity()
    {
        Assert.Throws<ArgumentException>(() => new LogLocationScope(LogStorageLocation.Remote));
        Assert.Throws<ArgumentException>(
            () => new LogLocationScope(LogStorageLocation.Remote, Guid.Empty));
        Assert.Throws<ArgumentException>(
            () => new LogLocationScope(LogStorageLocation.Local, LogTestData.Id(900)));
    }

    [Fact]
    public void F06RemoteTargetScopePreventsCrossTargetChain()
    {
        var targetA = LogLocationScope.Remote(LogTestData.Id(900));
        var targetB = LogLocationScope.Remote(LogTestData.Id(901));
        var startA = Select(
            [LogTestData.FullCandidate(last: 200, scope: targetA)],
            scope: targetA);

        var crossTarget = Evaluate(
            startA,
            LogTestData.LogCandidate(
                LogTestData.Id(260),
                200,
                300,
                scope: targetB));
        var sameTarget = Evaluate(
            startA,
            LogTestData.LogCandidate(
                LogTestData.Id(261),
                200,
                300,
                scope: targetA));

        Assert.Equal(targetA, startA.Scope);
        Assert.NotNull(startA.FullBackupSetId);
        Assert.Equal(LogCoverageConclusion.Gap, crossTarget.Conclusion);
        Assert.Empty(crossTarget.UsedBackupSetIds);
        Assert.Equal(LogCoverageConclusion.Continuous, sameTarget.Conclusion);
        Assert.Equal(targetA, sameTarget.Start.Scope);
        Assert.Equal(targetA, sameTarget.Scope);
    }

    [Fact]
    public void F06SameBackupAtLocalAndRemoteDoesNotConflict()
    {
        var id = LogTestData.Id(262);
        var localStart = LogTestData.AvailableStart(200);
        var remote = LogLocationScope.Remote(LogTestData.Id(902));

        var result = Evaluate(
            localStart,
            LogTestData.LogCandidate(id, 200, 300),
            LogTestData.LogCandidate(
                id,
                200,
                300,
                scope: remote));

        Assert.Equal(LogCoverageConclusion.Continuous, result.Conclusion);
        Assert.Empty(result.Conflicts);
        Assert.Single(result.UsedBackupSetIds);
    }

    private static LogDataStartDecision Select(
        LogDataStartCandidate[] candidates,
        Guid? preferredFullBackupSetId = null,
        LogLocationScope? scope = null)
    {
        return LogDataStartRules.Select(new LogDataStartRequest(
            scope ?? LogLocationScope.Local,
            candidates,
            preferredFullBackupSetId));
    }

    private static LogCoverageResult Evaluate(
        LogDataStartDecision start,
        params LogCoverageCandidate[] candidates)
    {
        return LogCoverageRules.Evaluate(new LogCoverageRequest(
            start,
            enumerationComplete: true,
            candidates));
    }

    private static void AssertStartConflict(
        LogDataStartDecision decision,
        Guid backupSetId,
        string fieldCode)
    {
        Assert.Equal(LogDataStartAvailability.Unknown, decision.Availability);
        Assert.Equal(LogReason.StartIdentityConflict, decision.ReasonCode);
        Assert.Null(decision.RestorePointLsn);
        var conflict = Assert.Single(decision.Conflicts);
        Assert.Equal(backupSetId, conflict.BackupSetId);
        Assert.Equal(fieldCode, conflict.FieldCode);
    }

    private static void AssertCoverageConflict(
        LogCoverageResult result,
        Guid backupSetId,
        string fieldCode)
    {
        Assert.Equal(LogCoverageConclusion.Unknown, result.Conclusion);
        Assert.Empty(result.UsedBackupSetIds);
        Assert.Contains(LogReason.DuplicateIdentityConflict, result.ReasonCodes);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(backupSetId, conflict.BackupSetId);
        Assert.Equal(fieldCode, conflict.FieldCode);
    }
}
