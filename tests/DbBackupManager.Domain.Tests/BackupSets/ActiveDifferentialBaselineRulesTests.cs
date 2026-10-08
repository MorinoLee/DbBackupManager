using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupSets;

public sealed class ActiveDifferentialBaselineRulesTests
{
    private static readonly Guid ManagedId = Id(1);
    private static readonly Guid FullGuid = Id(2);
    private static readonly Guid CurrentFork = Id(3);
    private static readonly BackupDatabaseIdentity Database = new(Id(4), Id(5));
    private static readonly BackupLsn Checkpoint = new(7000000000000000000000001m);

    [Fact]
    public void CurrentBranchAloneIdentifiesAnActiveManagedBase()
    {
        // 活动输入没有目录起始分支；该诊断字段是否缺失不影响判定。
        var decision = Evaluate(Active(CurrentFork));
        AssertDecision(decision, DifferentialBaselineConclusion.Verified, DifferentialBaselineReason.ManagedFullVerified);
        Assert.Equal(ManagedId, decision.ManagedFullId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrEmptyCurrentBranchIsUnknown(bool emptyGuid)
    {
        AssertDecision(Evaluate(Active(emptyGuid ? Guid.Empty : null)),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentBranchDifferentFromManagedOrExternalFullIsMismatch(bool external)
    {
        var active = Active(Id(99));
        var result = external ? Evaluate(active, [], Full()) : Evaluate(active);
        AssertDecision(result, DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.RecoveryBranchMismatch);
    }

    public static TheoryData<BackupRecoveryBranch> IncompleteFullBranches => new()
    {
        new(null, CurrentFork),
        new(CurrentFork, null),
        new(Guid.Empty, CurrentFork),
        new(CurrentFork, Guid.Empty),
    };

    [Theory]
    [MemberData(nameof(IncompleteFullBranches))]
    public void IncompleteFullStartingOrEndingBranchIsUnknown(BackupRecoveryBranch branch)
    {
        var full = Full() with { Branch = branch };
        AssertDecision(Evaluate(Active(CurrentFork), [new(ManagedId, full)]),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields);
        AssertDecision(Evaluate(Active(CurrentFork), [], full),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullSpanningBranchesIsMismatchEvenWhenOneBranchMatchesCurrent(bool startingBranchMatches)
    {
        var full = Full() with
        {
            Branch = startingBranchMatches ? new(CurrentFork, Id(99)) : new(Id(99), CurrentFork),
        };
        AssertDecision(Evaluate(Active(CurrentFork), [new(ManagedId, full)]),
            DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.RecoveryBranchMismatch);
        AssertDecision(Evaluate(Active(CurrentFork), [], full),
            DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.RecoveryBranchMismatch);
    }

    [Fact]
    public void HistoricalDifferentialStillRequiresItsOwnStartingBranch()
    {
        var differential = new DifferentialBackupEvidence(
            new(Database, new(null, CurrentFork), FullGuid, Checkpoint), BackupType.Differential, false, Checkpoint);
        var result = DifferentialBaselineRules.EvaluateDependency(Database, differential, [Managed()]);
        AssertDecision(result, DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields);
    }

    [Theory]
    [InlineData(BaselineEvidenceStatus.MissingFields, DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields)]
    [InlineData(BaselineEvidenceStatus.PermissionDenied, DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.PermissionDenied)]
    [InlineData(BaselineEvidenceStatus.HistoryNotFound, DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.HistoryNotFound)]
    [InlineData(BaselineEvidenceStatus.MultipleBases, DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MultipleBases)]
    [InlineData(BaselineEvidenceStatus.SourceConflict, DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.SourceConflict)]
    public void DatabaseOrDataFileEvidenceStatusRetainsItsMeaning(
        BaselineEvidenceStatus status, DifferentialBaselineConclusion conclusion, string reason)
    {
        AssertDecision(Evaluate(Active(CurrentFork, status)), conclusion, reason);
        AssertDecision(Evaluate(new(Database, CurrentFork, [new(FullGuid, Checkpoint, status)])), conclusion, reason);
    }

    [Fact]
    public void ConfirmedFileSourceConflictWinsOverMissingCurrentBranchAndPermissionFailure()
    {
        var active = new ActiveDifferentialBaselineEvidence(Database, null,
            [new(FullGuid, Checkpoint), new(null, null, BaselineEvidenceStatus.SourceConflict)], BaselineEvidenceStatus.PermissionDenied);
        AssertDecision(Evaluate(active), DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.SourceConflict);
    }

    [Fact]
    public void ManagedFullCannotHideConflictingOrUnavailableExternalEvidence()
    {
        AssertDecision(Evaluate(Active(CurrentFork), external: Full() with { Branch = new(Id(99), Id(99)) }),
            DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.RecoveryBranchMismatch);
        AssertDecision(Evaluate(Active(CurrentFork), external: Full() with { IsCopyOnly = true }),
            DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.CopyOnlyFull);
        AssertDecision(Evaluate(Active(CurrentFork), external: Full() with { Status = BaselineEvidenceStatus.PermissionDenied }),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.PermissionDenied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveAndFullDatabaseIdentitiesMustMatchExpectedDatabase(bool fullMismatch)
    {
        var otherDatabase = Database with { DatabaseGuid = Id(99) };
        var result = fullMismatch
            ? Evaluate(Active(CurrentFork), [new(ManagedId, Full() with { Database = otherDatabase })])
            : Evaluate(new(otherDatabase, CurrentFork, [new(FullGuid, Checkpoint)]));
        AssertDecision(result, DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.DatabaseIdentityMismatch);
    }

    [Fact]
    public void CopyOnlyAndNonFullCandidatesRemainMismatch()
    {
        AssertDecision(Evaluate(Active(CurrentFork), [new(ManagedId, Full() with { IsCopyOnly = true })]),
            DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.CopyOnlyFull);
        AssertDecision(Evaluate(Active(CurrentFork), [new(ManagedId, Full() with { Type = BackupType.Differential })]),
            DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.BackupTypeMismatch);
    }

    [Fact]
    public void DifferentDataFileBasesRemainUnknown()
    {
        var differentGuid = new ActiveDifferentialBaselineEvidence(Database, CurrentFork,
            [new(FullGuid, Checkpoint), new(Id(99), Checkpoint)]);
        var differentLsn = new ActiveDifferentialBaselineEvidence(Database, CurrentFork,
            [new(FullGuid, Checkpoint), new(FullGuid, new(7000000000000000000000002m))]);
        AssertDecision(Evaluate(differentGuid), DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MultipleBases);
        AssertDecision(Evaluate(differentLsn), DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MultipleBases);
    }

    [Fact]
    public void DuplicateManagedGuidsAreStillMismatch()
    {
        AssertDecision(Evaluate(Active(CurrentFork), [Managed(), new(Id(99), Full())]),
            DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.DuplicateManagedGuid);
    }

    [Fact]
    public void MissingFileGuidLsnAndDatabaseIdentityRemainUnknown()
    {
        AssertDecision(Evaluate(new(Database, CurrentFork, [new(null, Checkpoint)])),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields);
        AssertDecision(Evaluate(new(Database, CurrentFork, [new(Guid.Empty, Checkpoint)])),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields);
        AssertDecision(Evaluate(new(Database, CurrentFork, [new(FullGuid, null)])),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields);
        AssertDecision(Evaluate(new(new(null, Database.FamilyGuid), CurrentFork, [new(FullGuid, Checkpoint)])),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields);
    }

    private static ActiveDifferentialBaselineEvidence Active(Guid? currentFork, BaselineEvidenceStatus status = BaselineEvidenceStatus.Complete) =>
        new(Database, currentFork, [new(FullGuid, Checkpoint)], status);

    private static FullBackupBaselineEvidence Full() => new(FullGuid, Database, new(CurrentFork, CurrentFork), BackupType.Full, false, Checkpoint);

    private static ManagedFullBackupCandidate Managed() => new(ManagedId, Full());

    private static DifferentialBaselineDecision Evaluate(
        ActiveDifferentialBaselineEvidence active,
        IReadOnlyList<ManagedFullBackupCandidate>? managed = null,
        FullBackupBaselineEvidence? external = null) =>
        DifferentialBaselineRules.EvaluateActiveBaseline(Database, active, managed ?? [Managed()], external);

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    private static void AssertDecision(DifferentialBaselineDecision result, DifferentialBaselineConclusion conclusion, string reason)
    {
        Assert.Equal(conclusion, result.Conclusion);
        Assert.Equal(reason, result.ReasonCode);
        if (conclusion != DifferentialBaselineConclusion.Verified)
        {
            Assert.Null(result.ManagedFullId);
        }
    }
}
