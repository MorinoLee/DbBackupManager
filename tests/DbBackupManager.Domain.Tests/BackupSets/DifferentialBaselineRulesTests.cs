using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupSets;

public sealed class DifferentialBaselineRulesTests
{
    private static readonly Guid ManagedId = Id(1);
    private static readonly Guid FullGuid = Id(2);
    private static readonly BackupDatabaseIdentity Database = new(Id(3), Id(4));
    private static readonly BackupRecoveryBranch Branch = new(Id(5), Id(5));
    private static readonly BackupLsn Checkpoint = new(7000000000000000000000001m);
    private static readonly BackupLsn OtherLsn = new(7000000000000000000000002m);

    [Fact]
    public void GuidAndBothLsnsIdentifyTheManagedOrdinaryFull()
    {
        var differential = Differential();
        var candidates = new[]
        {
            new ManagedFullBackupCandidate(Id(6), Full() with { BackupSetGuid = Id(7) }),
            Managed(),
        };
        var decision = Dependency(differential, candidates);
        AssertDecision(decision, DifferentialBaselineConclusion.Verified, DifferentialBaselineReason.ManagedFullVerified);
        Assert.Equal(ManagedId, decision.ManagedFullId);
    }

    [Fact]
    public void SameLsnWithDifferentGuidCannotSelectTheWrongFull()
    {
        var decision = Dependency(Differential() with
        {
            ActualBase = Reference() with { BaseBackupSetGuid = Id(99) },
        });
        AssertDecision(decision, DifferentialBaselineConclusion.Unmanaged, DifferentialBaselineReason.ManagedFullNotFound);
    }

    [Fact]
    public void MatchingGuidWithDifferentCheckpointIsMismatch()
    {
        var decision = Dependency(Differential(), [Managed(Full() with { CheckpointLsn = OtherLsn })]);
        AssertDecision(decision, DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.LsnMismatch);
    }

    [Fact]
    public void DatabaseBackupLsnMustAlsoMatchTheActualBase()
    {
        AssertDecision(
            Dependency(Differential() with { DatabaseBackupLsn = OtherLsn }),
            DifferentialBaselineConclusion.Mismatch,
            DifferentialBaselineReason.DatabaseBackupLsnMismatch);
    }

    [Fact]
    public void SameNameDifferentDatabaseCannotUseTheManagedFull()
    {
        var actual = Reference() with { Database = Database with { DatabaseGuid = Id(100) } };
        AssertDecision(
            Dependency(Differential() with { ActualBase = actual }),
            DifferentialBaselineConclusion.Mismatch,
            DifferentialBaselineReason.DatabaseIdentityMismatch);
    }

    [Fact]
    public void MatchingBindingButDifferentFamilyIsMismatch()
    {
        AssertDecision(
            Dependency(Differential(), [Managed(Full() with { Database = Database with { FamilyGuid = Id(100) } })]),
            DifferentialBaselineConclusion.Mismatch,
            DifferentialBaselineReason.DatabaseIdentityMismatch);
    }

    [Fact]
    public void FullDatabaseBindingMustMatchTheDifferential()
    {
        AssertDecision(
            Dependency(Differential(), [Managed(Full() with { Database = Database with { DatabaseGuid = Id(100) } })]),
            DifferentialBaselineConclusion.Mismatch,
            DifferentialBaselineReason.DatabaseIdentityMismatch);
    }

    [Fact]
    public void CopyOnlyFullCannotBecomeADifferentialBase()
    {
        AssertDecision(
            Dependency(Differential(), [Managed(Full() with { IsCopyOnly = true })]),
            DifferentialBaselineConclusion.Mismatch,
            DifferentialBaselineReason.CopyOnlyFull);
    }

    [Fact]
    public void CopyOnlyDifferentialAndNonDifferentialTypeAreMismatch()
    {
        AssertDecision(Dependency(Differential() with { IsCopyOnly = true }),
            DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.CopyOnlyDifferential);
        AssertDecision(Dependency(Differential() with { Type = BackupType.Full }),
            DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.BackupTypeMismatch);
        AssertDecision(Dependency(Differential(), [Managed(Full() with { Type = BackupType.Differential })]),
            DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.BackupTypeMismatch);
    }

    [Fact]
    public void DifferentRecoveryBranchesCannotBeLinked()
    {
        AssertDecision(
            Dependency(Differential(), [Managed(Full() with { Branch = new(Id(101), Id(101)) })]),
            DifferentialBaselineConclusion.Mismatch,
            DifferentialBaselineReason.RecoveryBranchMismatch);
    }

    [Fact]
    public void DataBackupSpanningRecoveryBranchesIsNotAccepted()
    {
        AssertDecision(
            Dependency(Differential() with { ActualBase = Reference() with { Branch = new(Id(5), Id(101)) } }),
            DifferentialBaselineConclusion.Mismatch,
            DifferentialBaselineReason.RecoveryBranchMismatch);
    }

    [Fact]
    public void ExternalFullInsertedBeforeDifferentialExecutionIsUnmanaged()
    {
        var external = Full() with { BackupSetGuid = Id(102), CheckpointLsn = OtherLsn };
        var actual = Reference() with { BaseBackupSetGuid = external.BackupSetGuid, BaseLsn = OtherLsn };
        var differential = Differential() with { ActualBase = actual, DatabaseBackupLsn = OtherLsn };
        AssertDecision(
            Dependency(differential, [Managed()], external),
            DifferentialBaselineConclusion.Unmanaged,
            DifferentialBaselineReason.ExternalFullObserved);
    }

    [Fact]
    public void ExternalFullAfterDifferentialChangesActiveBaseButNotHistoricalDependency()
    {
        var differential = Differential();
        var external = Full() with { BackupSetGuid = Id(102), CheckpointLsn = OtherLsn };
        var current = FileBase() with { BaseBackupSetGuid = external.BackupSetGuid, BaseLsn = OtherLsn };
        var before = Dependency(differential);
        var active = DifferentialBaselineRules.EvaluateActiveBaseline(Database, Active(current), [Managed()], external);
        var after = Dependency(differential, [Managed()], external);

        Assert.Equal(before, after);
        Assert.Equal(ManagedId, after.ManagedFullId);
        AssertDecision(active, DifferentialBaselineConclusion.Unmanaged, DifferentialBaselineReason.ExternalFullObserved);
    }

    [Fact]
    public void ExternalEvidenceWithConflictingLsnIsMismatch()
    {
        var actual = Reference() with { BaseBackupSetGuid = Id(102) };
        AssertDecision(
            Dependency(Differential() with { ActualBase = actual }, [], Full() with
            {
                BackupSetGuid = Id(102),
                CheckpointLsn = OtherLsn,
            }),
            DifferentialBaselineConclusion.Mismatch,
            DifferentialBaselineReason.LsnMismatch);
    }

    [Fact]
    public void RegisteredFullCannotHideConflictingOrUnavailableHistory()
    {
        AssertDecision(Dependency(Differential(), [Managed()], Full() with { CheckpointLsn = OtherLsn }),
            DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.LsnMismatch);
        AssertDecision(Dependency(Differential(), [Managed()], Full() with { Status = BaselineEvidenceStatus.PermissionDenied }),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.PermissionDenied);
    }

    [Fact]
    public void MatchingDataFileBasesIdentifyOneActiveManagedFull()
    {
        var active = Active(FileBase(), FileBase());
        var result = DifferentialBaselineRules.EvaluateActiveBaseline(Database, active, [Managed()]);
        AssertDecision(result, DifferentialBaselineConclusion.Verified, DifferentialBaselineReason.ManagedFullVerified);
        Assert.Equal(ManagedId, result.ManagedFullId);
    }

    [Fact]
    public void DifferentDataFileBaseGuidsOrLsnsAreUnknown()
    {
        var differentGuid = Active(FileBase(), FileBase() with { BaseBackupSetGuid = Id(103) });
        var differentLsn = Active(FileBase(), FileBase() with { BaseLsn = OtherLsn });
        AssertDecision(DifferentialBaselineRules.EvaluateActiveBaseline(Database, differentGuid, [Managed()]),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MultipleBases);
        AssertDecision(DifferentialBaselineRules.EvaluateActiveBaseline(Database, differentLsn, [Managed()]),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MultipleBases);
    }

    [Fact]
    public void EmptyOrPartiallyUnknownDataFileEvidenceCannotBeHealthy()
    {
        var empty = Active();
        var incomplete = Active(FileBase(), FileBase() with { BaseLsn = null });
        AssertDecision(DifferentialBaselineRules.EvaluateActiveBaseline(Database, empty, [Managed()]),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields);
        AssertDecision(DifferentialBaselineRules.EvaluateActiveBaseline(Database, incomplete, [Managed()]),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields);
    }

    [Fact]
    public void DataFileEvidenceIsASnapshotThatCannotBeModifiedByItsCaller()
    {
        var files = new List<ActiveDataFileBaselineEvidence> { FileBase() };
        var active = new ActiveDifferentialBaselineEvidence(Database, Branch.RecoveryForkId, files);
        files.Add(FileBase() with { BaseLsn = OtherLsn });
        Assert.Single(active.DataFileBases);
        Assert.Throws<NotSupportedException>(() => ((IList<ActiveDataFileBaselineEvidence>)active.DataFileBases).Clear());
    }

    [Theory]
    [InlineData(BaselineEvidenceStatus.MissingFields, DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields)]
    [InlineData(BaselineEvidenceStatus.PermissionDenied, DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.PermissionDenied)]
    [InlineData(BaselineEvidenceStatus.HistoryNotFound, DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.HistoryNotFound)]
    [InlineData(BaselineEvidenceStatus.MultipleBases, DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MultipleBases)]
    [InlineData(BaselineEvidenceStatus.SourceConflict, DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.SourceConflict)]
    public void IncompleteOrConflictingSourcesCannotShowHealthy(
        BaselineEvidenceStatus status, DifferentialBaselineConclusion conclusion, string reason)
    {
        var actual = Reference() with { Status = status };
        AssertDecision(Dependency(Differential() with { ActualBase = actual }), conclusion, reason);
        AssertDecision(DifferentialBaselineRules.EvaluateActiveBaseline(Database, Active(FileBase() with { Status = status }), [Managed()]), conclusion, reason);
        AssertDecision(Dependency(Differential(), [Managed(Full() with { Status = status })]), conclusion, reason);
        AssertDecision(Dependency(Differential(), [], Full() with { Status = status }), conclusion, reason);
    }

    public static TheoryData<DifferentialBaseEvidence> IncompleteReferences => new()
    {
        Reference() with { BaseBackupSetGuid = null },
        Reference() with { BaseBackupSetGuid = Guid.Empty },
        Reference() with { BaseLsn = null },
        Reference() with { Database = Database with { DatabaseGuid = null } },
        Reference() with { Database = Database with { FamilyGuid = null } },
        Reference() with { Database = Database with { DatabaseGuid = Guid.Empty } },
        Reference() with { Branch = Branch with { FirstRecoveryForkId = null } },
        Reference() with { Branch = Branch with { RecoveryForkId = null } },
        Reference() with { Branch = Branch with { RecoveryForkId = Guid.Empty } },
    };

    [Theory]
    [MemberData(nameof(IncompleteReferences))]
    public void MissingReferenceFieldsAreUnknown(DifferentialBaseEvidence actual)
    {
        AssertDecision(Dependency(Differential() with { ActualBase = actual }),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields);
    }

    public static TheoryData<FullBackupBaselineEvidence> IncompleteFulls => new()
    {
        Full() with { Type = null },
        Full() with { IsCopyOnly = null },
        Full() with { CheckpointLsn = null },
        Full() with { Database = Database with { DatabaseGuid = null } },
        Full() with { Database = Database with { FamilyGuid = null } },
        Full() with { Branch = Branch with { FirstRecoveryForkId = null } },
        Full() with { Branch = Branch with { RecoveryForkId = null } },
    };

    [Theory]
    [MemberData(nameof(IncompleteFulls))]
    public void MissingFullFieldsAreUnknown(FullBackupBaselineEvidence full)
    {
        AssertDecision(Dependency(Differential(), [Managed(full)]),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields);
        AssertDecision(DifferentialBaselineRules.EvaluateActiveBaseline(Database, Active(FileBase()), [Managed(full)]),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields);
    }

    public static TheoryData<DifferentialBackupEvidence> IncompleteDifferentials => new()
    {
        Differential() with { Type = null },
        Differential() with { IsCopyOnly = null },
        Differential() with { DatabaseBackupLsn = null },
    };

    [Theory]
    [MemberData(nameof(IncompleteDifferentials))]
    public void MissingDifferentialFieldsAreUnknown(DifferentialBackupEvidence differential)
    {
        AssertDecision(Dependency(differential),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields);
    }

    [Fact]
    public void MissingExpectedIdentityDoesNotAcceptAnyDatabase()
    {
        AssertDecision(DifferentialBaselineRules.EvaluateDependency(new(null, null), Differential(), [Managed()]),
            DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields);
    }

    [Fact]
    public void DuplicateManagedGuidIsNotResolvedByOrder()
    {
        var candidates = new[] { Managed(), new ManagedFullBackupCandidate(Id(200), Full()) };
        AssertDecision(Dependency(Differential(), candidates),
            DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.DuplicateManagedGuid);
    }

    [Fact]
    public void ActiveManagedBaseUsesTheSameIdentityGuidAndLsnRules()
    {
        AssertDecision(DifferentialBaselineRules.EvaluateActiveBaseline(Database, Active(FileBase()), [Managed()]),
            DifferentialBaselineConclusion.Verified, DifferentialBaselineReason.ManagedFullVerified);
        AssertDecision(DifferentialBaselineRules.EvaluateActiveBaseline(Database,
                Active(FileBase() with { BaseLsn = OtherLsn }), [Managed()]),
            DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.LsnMismatch);
    }

    [Fact]
    public void InvalidEvidenceStatusAndEmptyManagedIdAreCallerErrors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Dependency(Differential() with
        {
            ActualBase = Reference() with { Status = (BaselineEvidenceStatus)999 },
        }));
        Assert.Throws<ArgumentException>(() => Dependency(Differential(), [new(Guid.Empty, Full())]));
    }

    private static DifferentialBaselineDecision Dependency(
        DifferentialBackupEvidence differential,
        IReadOnlyList<ManagedFullBackupCandidate>? managed = null,
        FullBackupBaselineEvidence? external = null) =>
        DifferentialBaselineRules.EvaluateDependency(Database, differential, managed ?? [Managed()], external);

    private static DifferentialBackupEvidence Differential() => new(Reference(), BackupType.Differential, false, Checkpoint);

    private static DifferentialBaseEvidence Reference() => new(Database, Branch, FullGuid, Checkpoint);

    private static ActiveDataFileBaselineEvidence FileBase() => new(FullGuid, Checkpoint);

    private static ActiveDifferentialBaselineEvidence Active(params ActiveDataFileBaselineEvidence[] files) =>
        new(Database, Branch.RecoveryForkId, files);

    private static FullBackupBaselineEvidence Full() => new(FullGuid, Database, Branch, BackupType.Full, false, Checkpoint);

    private static ManagedFullBackupCandidate Managed(FullBackupBaselineEvidence? full = null) => new(ManagedId, full ?? Full());

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    private static void AssertDecision(
        DifferentialBaselineDecision decision, DifferentialBaselineConclusion conclusion, string reason)
    {
        Assert.Equal(conclusion, decision.Conclusion);
        Assert.Equal(reason, decision.ReasonCode);
        if (conclusion != DifferentialBaselineConclusion.Verified)
        {
            Assert.Null(decision.ManagedFullId);
        }
    }
}
