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

        return ResolveFull(actual, managedFulls, externalFull);
    }

    public static DifferentialBaselineDecision EvaluateActiveBaseline(
        BackupDatabaseIdentity expectedDatabase,
        ActiveDifferentialBaselineEvidence active,
        IReadOnlyList<ManagedFullBackupCandidate> managedFulls,
        FullBackupBaselineEvidence? externalFull = null)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(expectedDatabase);
        ArgumentNullException.ThrowIfNull(managedFulls);
        if (active.DataFileBases.Count == 0)
        {
            return Unknown(DifferentialBaselineReason.MissingFields);
        }

        foreach (var fileBase in active.DataFileBases)
        {
            ValidateInputs(expectedDatabase, fileBase, managedFulls, externalFull);
        }

        if (active.DataFileBases.Any(fileBase => fileBase.Status == BaselineEvidenceStatus.SourceConflict))
        {
            return Mismatch(DifferentialBaselineReason.SourceConflict);
        }

        foreach (var fileBase in active.DataFileBases)
        {
            if ((EvidenceProblem(fileBase.Status) ?? ReferenceProblem(expectedDatabase, fileBase)) is { } problem)
            {
                return problem;
            }
        }

        var current = active.DataFileBases[0];
        if (active.DataFileBases.Any(fileBase => fileBase.BaseBackupSetGuid != current.BaseBackupSetGuid
            || fileBase.BaseLsn != current.BaseLsn))
        {
            return Unknown(DifferentialBaselineReason.MultipleBases);
        }

        if (active.DataFileBases.Any(fileBase => fileBase.Branch != current.Branch))
        {
            return Mismatch(DifferentialBaselineReason.RecoveryBranchMismatch);
        }

        return ResolveFull(current, managedFulls, externalFull);
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
        DifferentialBaseEvidence actual,
        IReadOnlyList<ManagedFullBackupCandidate> managedFulls,
        FullBackupBaselineEvidence? external)
    {
        // 外部历史也是证据：同一 GUID 的矛盾不能被已登记的受管 FULL 掩盖。
        if (external is not null && external.BackupSetGuid == actual.BaseBackupSetGuid
            && FullProblem(actual, external) is { } externalProblem)
        {
            return externalProblem;
        }

        var matches = managedFulls.Where(full => full.Evidence.BackupSetGuid == actual.BaseBackupSetGuid).ToArray();
        if (matches.Length > 1)
        {
            return Mismatch(DifferentialBaselineReason.DuplicateManagedGuid);
        }

        if (matches.Length == 1)
        {
            var full = matches[0];
            return FullProblem(actual, full.Evidence)
                ?? new DifferentialBaselineDecision(
                    DifferentialBaselineConclusion.Verified,
                    DifferentialBaselineReason.ManagedFullVerified,
                    full.BackupSetId);
        }

        if (external is not null && external.BackupSetGuid == actual.BaseBackupSetGuid)
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
