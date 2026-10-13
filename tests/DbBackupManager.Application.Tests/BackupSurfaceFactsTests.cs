using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.Tests;

public sealed class BackupSurfaceFactsTests
{
    private static readonly Guid AttemptId = Guid.NewGuid();
    private static readonly Guid FullId = Guid.NewGuid();

    [Fact]
    public void SqlSuccessRequiresAppliedFactAndDoesNotFollowTaskStage()
    {
        var missing = BackupSurfaceFacts.Describe(
            BackupType.Full, null, null, null, BackupStorageMode.LocalOnly, null, [], []);
        Assert.Equal("Full", missing.BackupType);
        Assert.Equal(BackupSurfaceFacts.Unknown, missing.SqlStatus);
        Assert.Equal(BackupSurfaceFacts.Unknown, missing.FileVerifyStatus);
        Assert.Equal(BackupSurfaceFacts.NotApplicable, missing.TransferStatus);
        Assert.Equal(BackupSurfaceFacts.Unknown, missing.DependencyStatus);

        var observed = Describe(BackupExecutionOperationKind.SqlResult, BackupExecutionOutcome.Succeeded, sqlSuccess: true);
        Assert.Equal(BackupSurfaceFacts.Succeeded, observed.SqlStatus);
        Assert.Equal(BackupSurfaceFacts.Unknown, observed.FileVerifyStatus);
        Assert.Equal(BackupSurfaceFacts.Unknown, observed.DependencyStatus);

        var withoutEvidence = Describe(BackupExecutionOperationKind.SqlResult, BackupExecutionOutcome.Succeeded, sqlSuccess: false);
        Assert.Equal(BackupSurfaceFacts.Indeterminate, withoutEvidence.SqlStatus);
    }

    [Fact]
    public void VerifyAndTransferStaySeparateFromDependency()
    {
        var operations = new[]
        {
            Operation(BackupExecutionOperationKind.VerifyLocal, 1, BackupExecutionOutcome.Succeeded, false),
            Operation(BackupExecutionOperationKind.Transfer, 1, BackupExecutionOutcome.Indeterminate, false),
        };
        var surface = BackupSurfaceFacts.Describe(
            BackupType.Differential,
            Guid.NewGuid(),
            Guid.NewGuid(),
            2,
            BackupStorageMode.LocalAndRemote,
            AttemptId,
            operations,
            [Dependency(BackupType.Differential, DifferentialBaselineConclusion.Verified, FullId)]);
        Assert.Equal("Differential", surface.BackupType);
        Assert.Equal(2, surface.PlanVersionNumber);
        Assert.Equal(BackupSurfaceFacts.Succeeded, surface.FileVerifyStatus);
        Assert.Equal(BackupSurfaceFacts.Indeterminate, surface.TransferStatus);
        Assert.Equal(BackupSurfaceFacts.Verified, surface.DependencyStatus);
    }

    [Theory]
    [InlineData(DifferentialBaselineConclusion.Unknown)]
    [InlineData(DifferentialBaselineConclusion.Unmanaged)]
    public void UnprovenDifferentialDependencyStaysUnknown(DifferentialBaselineConclusion conclusion)
    {
        var surface = BackupSurfaceFacts.Describe(
            BackupType.Differential, null, null, null, BackupStorageMode.LocalOnly, AttemptId, [],
            [Dependency(BackupType.Differential, conclusion, conclusion == DifferentialBaselineConclusion.Verified ? FullId : null)]);
        Assert.Equal(BackupSurfaceFacts.Unknown, surface.DependencyStatus);
    }

    [Fact]
    public void ConflictingOrLogDependencyIsNotReportedHealthy()
    {
        var conflict = BackupSurfaceFacts.Describe(
            BackupType.Differential, null, null, null, null, AttemptId, [],
            [Dependency(BackupType.Differential, DifferentialBaselineConclusion.Mismatch, FullId) with { HasMetadataConflict = true }]);
        Assert.Equal(BackupSurfaceFacts.Mismatch, conflict.DependencyStatus);

        var log = BackupSurfaceFacts.Describe(
            BackupType.Log, null, null, null, BackupStorageMode.LocalOnly, AttemptId, [],
            [Dependency(BackupType.Log, null, null)]);
        Assert.Equal(BackupSurfaceFacts.Unknown, log.DependencyStatus);
        Assert.Equal(BackupSurfaceFacts.NotApplicable, log.TransferStatus);
    }

    [Fact]
    public void RemoteGapIsVisibleWithoutTreatingUnknownAsRecoverable()
    {
        var remote = BackupRecoverySurface.Project(
            BackupFileLocation.Remote,
            Guid.NewGuid(),
            30,
            LocationRecoveryConclusion.NotRestorable,
            ActiveBaseline.Known(FullId),
            [PlanLocationRecovery.RemoteBaseUnconfirmed, RecoveryProtectionReason.OutsideWindowUnneeded]);
        Assert.Equal(BackupRecoverySurface.BaselineKnown, remote.Baseline);
        Assert.Equal(FullId, remote.ManagedBaselineBackupSetId);
        Assert.True(remote.RemoteUnrecoverable);
        Assert.False(remote.Unknown);
        Assert.Equal([PlanLocationRecovery.RemoteBaseUnconfirmed], remote.GapReasonCodes);
        Assert.Empty(remote.UnknownReasonCodes);

        var unresolved = BackupRecoverySurface.Project(
            BackupFileLocation.Remote,
            Guid.NewGuid(),
            7,
            LocationRecoveryConclusion.NotRestorable,
            ActiveBaseline.Unresolved,
            [PlanLocationRecovery.DependencyUnproven, PlanLocationRecovery.OwnershipUnproven]);
        Assert.Equal(BackupRecoverySurface.BaselineUnknown, unresolved.Baseline);
        Assert.Null(unresolved.ManagedBaselineBackupSetId);
        Assert.True(unresolved.Unknown);
        Assert.False(unresolved.RemoteUnrecoverable);
        Assert.Empty(unresolved.GapReasonCodes);
        Assert.Contains(PlanLocationRecovery.DependencyUnproven, unresolved.UnknownReasonCodes);
        Assert.Contains(PlanLocationRecovery.OwnershipUnproven, unresolved.UnknownReasonCodes);
        Assert.DoesNotContain("enable_log", string.Join(' ', unresolved.GapReasonCodes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("remote.staging_missing")]
    [InlineData("plan.retention.claimed")]
    [InlineData("plan.retention.protected")]
    [InlineData(PlanLocationRecovery.RemoteStagingDeferred)]
    public void UnclassifiedReasonIsUnknownEvenWhenConclusionLooksRestorable(string reason)
    {
        var remote = BackupRecoverySurface.Project(
            BackupFileLocation.Remote,
            Guid.NewGuid(),
            30,
            LocationRecoveryConclusion.RestorableWindowSatisfied,
            ActiveBaseline.Known(FullId),
            [reason]);
        Assert.True(remote.Unknown);
        Assert.False(remote.RemoteUnrecoverable);
        Assert.Equal(BackupRecoverySurface.BaselineKnown, remote.Baseline);
        Assert.Equal([reason], remote.UnknownReasonCodes);
        Assert.Empty(remote.GapReasonCodes);
    }

    [Fact]
    public void UnclassifiedReasonStaysUnknownBesideARemoteGap()
    {
        var remote = BackupRecoverySurface.Project(
            BackupFileLocation.Remote,
            Guid.NewGuid(),
            30,
            LocationRecoveryConclusion.NotRestorable,
            ActiveBaseline.Known(FullId),
            [PlanLocationRecovery.RemoteBaseUnconfirmed, "remote.staging_missing"]);
        Assert.True(remote.Unknown);
        Assert.True(remote.RemoteUnrecoverable);
        Assert.Equal([PlanLocationRecovery.RemoteBaseUnconfirmed], remote.GapReasonCodes);
        Assert.Equal(["remote.staging_missing"], remote.UnknownReasonCodes);
    }

    [Fact]
    public void DifferentialBaseIsHiddenWhenTypeIsUnknown()
    {
        Assert.Equal(FullId, BackupSurfaceFacts.DifferentialBase(BackupMetadataState.Known, BackupType.Differential, FullId));
        Assert.Null(BackupSurfaceFacts.DifferentialBase(BackupMetadataState.Unknown, BackupType.Differential, FullId));
        Assert.Null(BackupSurfaceFacts.DifferentialBase(BackupMetadataState.Known, BackupType.Full, FullId));
    }

    private static BackupTaskSurface Describe(
        BackupExecutionOperationKind kind, BackupExecutionOutcome outcome, bool sqlSuccess) =>
        BackupSurfaceFacts.Describe(
            BackupType.Full, null, null, null, BackupStorageMode.RemoteOnly, AttemptId,
            [Operation(kind, 1, outcome, sqlSuccess)], []);

    private static BackupOperationFact Operation(
        BackupExecutionOperationKind kind, int sequence, BackupExecutionOutcome outcome, bool sqlSuccess) =>
        new(kind, sequence, BackupExecutionOperationState.Applied, outcome, sqlSuccess);

    private static BackupDependencyFact Dependency(
        BackupType type, DifferentialBaselineConclusion? conclusion, Guid? baseId) =>
        new(
            AttemptId,
            BackupMetadataState.Known,
            type,
            baseId,
            conclusion is null ? BackupMetadataState.NotApplicable : BackupMetadataState.Known,
            conclusion,
            false);
}

public sealed class BackupFileRetentionScopeTests
{
    [Fact]
    public void IncompleteGraphDoesNotTreatWindowReasonsAsDeletable()
    {
        var pageFile = Guid.NewGuid();
        var assessments = Assessments(
            pageFile,
            RecoveryWindowVerdict.Deletable,
            RecoveryProtectionReason.OutsideWindowUnneeded);
        var incomplete = BackupFileRetentionScope.Select([pageFile], assessments, locationGraphComplete: false);
        Assert.Equal(RecoveryProtectionReason.WindowUnknown, incomplete[pageFile]);

        var complete = BackupFileRetentionScope.Select([pageFile], assessments, locationGraphComplete: true);
        Assert.Equal(RecoveryProtectionReason.OutsideWindowUnneeded, complete[pageFile]);
    }

    [Fact]
    public void IncompleteGraphKeepsReasonsKnownFromThePageFile()
    {
        var pageFile = Guid.NewGuid();
        var assessments = Assessments(pageFile, RecoveryWindowVerdict.Protected, PlanLocationRecovery.OwnershipUnproven);
        var reasons = BackupFileRetentionScope.Select([pageFile], assessments, locationGraphComplete: false);
        Assert.Equal(PlanLocationRecovery.OwnershipUnproven, reasons[pageFile]);
        Assert.DoesNotContain(Guid.NewGuid(), reasons.Keys);
    }

    [Fact]
    public void OnlyFilesWithProvenLocationGraphKeepWindowReasons()
    {
        var proven = Guid.NewGuid();
        var unproven = Guid.NewGuid();
        var assessments = new[]
        {
            new PlanLocationAssessment(
                BackupFileLocation.Local,
                null,
                7,
                LocationRecoveryConclusion.RestorableWindowSatisfied,
                [
                    new PlanCopyRetentionDecision(proven, Guid.NewGuid(), RecoveryWindowVerdict.Deletable, RecoveryProtectionReason.OutsideWindowUnneeded),
                    new PlanCopyRetentionDecision(unproven, Guid.NewGuid(), RecoveryWindowVerdict.Deletable, RecoveryProtectionReason.OutsideWindowUnneeded),
                ]),
        };
        var reasons = BackupFileRetentionScope.Select(
            [proven, unproven],
            assessments,
            new HashSet<Guid> { proven });
        Assert.Equal(RecoveryProtectionReason.OutsideWindowUnneeded, reasons[proven]);
        Assert.Equal(RecoveryProtectionReason.WindowUnknown, reasons[unproven]);
        Assert.True(BackupFileRetentionScope.DependsOnLocationGraph(RecoveryProtectionReason.OutsideWindowUnneeded));
        Assert.False(BackupFileRetentionScope.DependsOnLocationGraph(RecoveryProtectionReason.WindowUnknown));
    }

    private static PlanLocationAssessment[] Assessments(Guid fileId, RecoveryWindowVerdict verdict, string reason) =>
    [
        new(
            BackupFileLocation.Local,
            null,
            7,
            LocationRecoveryConclusion.NotRestorable,
            [new PlanCopyRetentionDecision(fileId, Guid.NewGuid(), verdict, reason)]),
    ];
}
