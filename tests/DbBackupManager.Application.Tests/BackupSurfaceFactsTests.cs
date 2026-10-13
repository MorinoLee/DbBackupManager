using DbBackupManager.Application.BackupPlans;
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
