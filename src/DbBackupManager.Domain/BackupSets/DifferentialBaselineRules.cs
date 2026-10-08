using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.BackupSets;

public static class DifferentialBaselineRules
{
    public static DifferentialBaselineDecision EvaluateDependency(
        BackupDatabaseIdentity expectedDatabase,
        DifferentialBackupEvidence differential,
        IReadOnlyList<ManagedFullBackupCandidate> managedFulls,
        FullBackupBaselineEvidence? externalFull = null)
    {
        ArgumentNullException.ThrowIfNull(differential);
        var actual = differential.ActualBase;
        ValidateInputs(expectedDatabase, actual, managedFulls, externalFull);
        if (EvidenceProblem(actual.Status) is { } problem)
        {
            return problem;
        }

        if (differential.Type is not null and not BackupType.Differential)
        {
            return Mismatch(DifferentialBaselineReason.BackupTypeMismatch);
        }

        if (differential.IsCopyOnly == true)
        {
            return Mismatch(DifferentialBaselineReason.CopyOnlyDifferential);
        }

        if (differential.Type is null || differential.IsCopyOnly is null || differential.DatabaseBackupLsn is null)
        {
            return Unknown(DifferentialBaselineReason.MissingFields);
        }

        if (ReferenceProblem(expectedDatabase, actual) is { } referenceProblem)
        {
            return referenceProblem;
        }

        if (differential.DatabaseBackupLsn != actual.BaseLsn)
        {
            return Mismatch(DifferentialBaselineReason.DatabaseBackupLsnMismatch);
        }

        return ResolveFull(actual.BaseBackupSetGuid, managedFulls, externalFull, full => FullProblem(actual, full));
    }

    public static DifferentialBaselineDecision EvaluateActiveBaseline(
        BackupDatabaseIdentity expectedDatabase,
        ActiveDifferentialBaselineEvidence active,
        IReadOnlyList<ManagedFullBackupCandidate> managedFulls,
        FullBackupBaselineEvidence? externalFull = null)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(expectedDatabase);
        ValidateCandidates(managedFulls, externalFull);
        foreach (var fileBase in active.DataFileBases)
        {
            ArgumentNullException.ThrowIfNull(fileBase);
        }

        if (active.Status == BaselineEvidenceStatus.SourceConflict
            || active.DataFileBases.Any(fileBase => fileBase.Status == BaselineEvidenceStatus.SourceConflict))
        {
            return Mismatch(DifferentialBaselineReason.SourceConflict);
        }

        if (EvidenceProblem(active.Status) is { } activeProblem)
        {
            return activeProblem;
        }

        if (active.DataFileBases.Count == 0 || !CompleteIdentity(expectedDatabase)
            || !CompleteIdentity(active.Database) || !KnownGuid(active.CurrentRecoveryForkId))
        {
            return Unknown(DifferentialBaselineReason.MissingFields);
        }

        if (expectedDatabase != active.Database)
        {
            return Mismatch(DifferentialBaselineReason.DatabaseIdentityMismatch);
        }

        foreach (var fileBase in active.DataFileBases)
        {
            if (EvidenceProblem(fileBase.Status) is { } problem)
            {
                return problem;
            }

            if (!KnownGuid(fileBase.BaseBackupSetGuid) || fileBase.BaseLsn is null)
            {
                return Unknown(DifferentialBaselineReason.MissingFields);
            }
        }

        var current = active.DataFileBases[0];
        if (active.DataFileBases.Any(fileBase => fileBase.BaseBackupSetGuid != current.BaseBackupSetGuid
            || fileBase.BaseLsn != current.BaseLsn))
        {
            return Unknown(DifferentialBaselineReason.MultipleBases);
        }

        return ResolveFull(current.BaseBackupSetGuid, managedFulls, externalFull,
            full => ActiveFullProblem(active, current, full));
    }

    private static DifferentialBaselineDecision? ReferenceProblem(
        BackupDatabaseIdentity expected,
        DifferentialBaseEvidence actual)
    {
        if (!CompleteIdentity(expected) || !CompleteIdentity(actual.Database)
            || !CompleteBranch(actual.Branch) || !KnownGuid(actual.BaseBackupSetGuid) || actual.BaseLsn is null)
        {
            return Unknown(DifferentialBaselineReason.MissingFields);
        }

        if (expected != actual.Database)
        {
            return Mismatch(DifferentialBaselineReason.DatabaseIdentityMismatch);
        }

        return actual.Branch.FirstRecoveryForkId != actual.Branch.RecoveryForkId
            ? Mismatch(DifferentialBaselineReason.RecoveryBranchMismatch)
            : null;
    }

    private static DifferentialBaselineDecision ResolveFull(
        Guid? baseBackupSetGuid,
        IReadOnlyList<ManagedFullBackupCandidate> managedFulls,
        FullBackupBaselineEvidence? external,
        Func<FullBackupBaselineEvidence, DifferentialBaselineDecision?> fullProblem)
    {
        // 外部历史也是证据：同一 GUID 的矛盾不能被已登记的受管 FULL 掩盖。
        if (external is not null && external.BackupSetGuid == baseBackupSetGuid
            && fullProblem(external) is { } externalProblem)
        {
            return externalProblem;
        }

        var matches = managedFulls.Where(full => full.Evidence.BackupSetGuid == baseBackupSetGuid).ToArray();
        if (matches.Length > 1)
        {
            return Mismatch(DifferentialBaselineReason.DuplicateManagedGuid);
        }

        if (matches.Length == 1)
        {
            var full = matches[0];
            return fullProblem(full.Evidence)
                ?? new DifferentialBaselineDecision(
                    DifferentialBaselineConclusion.Verified,
                    DifferentialBaselineReason.ManagedFullVerified,
                    full.BackupSetId);
        }

        if (external is not null && external.BackupSetGuid == baseBackupSetGuid)
        {
            return new DifferentialBaselineDecision(
                DifferentialBaselineConclusion.Unmanaged,
                DifferentialBaselineReason.ExternalFullObserved);
        }

        return new DifferentialBaselineDecision(
            DifferentialBaselineConclusion.Unmanaged,
            DifferentialBaselineReason.ManagedFullNotFound);
    }

    private static DifferentialBaselineDecision? FullProblem(
        DifferentialBaseEvidence actual,
        FullBackupBaselineEvidence full)
    {
        if (FullEvidenceProblem(full) is { } problem)
        {
            return problem;
        }

        if (actual.Database != full.Database)
        {
            return Mismatch(DifferentialBaselineReason.DatabaseIdentityMismatch);
        }

        if (actual.Branch != full.Branch)
        {
            return Mismatch(DifferentialBaselineReason.RecoveryBranchMismatch);
        }

        return actual.BaseLsn != full.CheckpointLsn
            ? Mismatch(DifferentialBaselineReason.LsnMismatch)
            : null;
    }

    private static DifferentialBaselineDecision? ActiveFullProblem(
        ActiveDifferentialBaselineEvidence active,
        ActiveDataFileBaselineEvidence fileBase,
        FullBackupBaselineEvidence full)
    {
        if (FullEvidenceProblem(full) is { } problem)
        {
            return problem;
        }

        if (active.Database != full.Database)
        {
            return Mismatch(DifferentialBaselineReason.DatabaseIdentityMismatch);
        }

        if (full.Branch.FirstRecoveryForkId != full.Branch.RecoveryForkId
            || full.Branch.RecoveryForkId != active.CurrentRecoveryForkId)
        {
            return Mismatch(DifferentialBaselineReason.RecoveryBranchMismatch);
        }

        return fileBase.BaseLsn != full.CheckpointLsn
            ? Mismatch(DifferentialBaselineReason.LsnMismatch)
            : null;
    }

    private static DifferentialBaselineDecision? FullEvidenceProblem(FullBackupBaselineEvidence full)
    {
        if (EvidenceProblem(full.Status) is { } problem)
        {
            return problem;
        }

        if (full.Type is not null and not BackupType.Full)
        {
            return Mismatch(DifferentialBaselineReason.BackupTypeMismatch);
        }

        if (full.IsCopyOnly == true)
        {
            return Mismatch(DifferentialBaselineReason.CopyOnlyFull);
        }

        if (full.Type is null || full.IsCopyOnly is null || full.CheckpointLsn is null
            || !CompleteIdentity(full.Database) || !CompleteBranch(full.Branch))
        {
            return Unknown(DifferentialBaselineReason.MissingFields);
        }

        return null;
    }

    private static DifferentialBaselineDecision? EvidenceProblem(BaselineEvidenceStatus status) => status switch
    {
        BaselineEvidenceStatus.Complete => null,
        BaselineEvidenceStatus.SourceConflict => Mismatch(DifferentialBaselineReason.SourceConflict),
        BaselineEvidenceStatus.MissingFields => Unknown(DifferentialBaselineReason.MissingFields),
        BaselineEvidenceStatus.PermissionDenied => Unknown(DifferentialBaselineReason.PermissionDenied),
        BaselineEvidenceStatus.HistoryNotFound => Unknown(DifferentialBaselineReason.HistoryNotFound),
        BaselineEvidenceStatus.MultipleBases => Unknown(DifferentialBaselineReason.MultipleBases),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "基线证据状态无效。"),
    };

    private static void ValidateInputs(
        BackupDatabaseIdentity expected,
        DifferentialBaseEvidence actual,
        IReadOnlyList<ManagedFullBackupCandidate> managedFulls,
        FullBackupBaselineEvidence? external)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(actual.Database);
        ArgumentNullException.ThrowIfNull(actual.Branch);
        ValidateCandidates(managedFulls, external);
    }

    private static void ValidateCandidates(
        IReadOnlyList<ManagedFullBackupCandidate> managedFulls,
        FullBackupBaselineEvidence? external)
    {
        ArgumentNullException.ThrowIfNull(managedFulls);
        foreach (var full in managedFulls)
        {
            ArgumentNullException.ThrowIfNull(full);
            if (full.BackupSetId == Guid.Empty)
            {
                throw new ArgumentException("受管备份集标识不能为空。", nameof(managedFulls));
            }

            ValidateFull(full.Evidence);
        }

        if (external is not null)
        {
            ValidateFull(external);
        }
    }

    private static void ValidateFull(FullBackupBaselineEvidence full)
    {
        ArgumentNullException.ThrowIfNull(full);
        ArgumentNullException.ThrowIfNull(full.Database);
        ArgumentNullException.ThrowIfNull(full.Branch);
    }

    private static bool KnownGuid(Guid? value) => value is { } guid && guid != Guid.Empty;

    private static bool CompleteIdentity(BackupDatabaseIdentity identity) =>
        KnownGuid(identity.DatabaseGuid) && KnownGuid(identity.FamilyGuid);

    private static bool CompleteBranch(BackupRecoveryBranch branch) =>
        KnownGuid(branch.FirstRecoveryForkId) && KnownGuid(branch.RecoveryForkId);

    private static DifferentialBaselineDecision Unknown(string reason) => new(DifferentialBaselineConclusion.Unknown, reason);

    private static DifferentialBaselineDecision Mismatch(string reason) => new(DifferentialBaselineConclusion.Mismatch, reason);
}
