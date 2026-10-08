using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.BackupSets;

public enum DifferentialBaselineConclusion
{
    Verified = 1,
    Unmanaged = 2,
    Mismatch = 3,
    Unknown = 4
}

public enum BaselineEvidenceStatus
{
    Complete = 1,
    MissingFields = 2,
    PermissionDenied = 3,
    HistoryNotFound = 4,
    SourceConflict = 5,
    MultipleBases = 6
}

public static class DifferentialBaselineReason
{
    public const string ManagedFullVerified = "baseline.managed_full_verified";
    public const string ManagedFullNotFound = "baseline.managed_full_not_found";
    public const string ExternalFullObserved = "baseline.external_full_observed";
    public const string SourceConflict = "baseline.source_conflict";
    public const string DatabaseIdentityMismatch = "baseline.database_identity_mismatch";
    public const string RecoveryBranchMismatch = "baseline.recovery_branch_mismatch";
    public const string BackupTypeMismatch = "baseline.backup_type_mismatch";
    public const string CopyOnlyFull = "baseline.copy_only_full";
    public const string CopyOnlyDifferential = "baseline.copy_only_differential";
    public const string LsnMismatch = "baseline.lsn_mismatch";
    public const string DatabaseBackupLsnMismatch = "baseline.database_backup_lsn_mismatch";
    public const string DuplicateManagedGuid = "baseline.duplicate_managed_guid";
    public const string MissingFields = "baseline.missing_fields";
    public const string PermissionDenied = "baseline.permission_denied";
    public const string HistoryNotFound = "baseline.history_not_found";
    public const string MultipleBases = "baseline.multiple_bases";
}

public sealed record BackupDatabaseIdentity(Guid? DatabaseGuid, Guid? FamilyGuid);

public sealed record BackupRecoveryBranch(Guid? FirstRecoveryForkId, Guid? RecoveryForkId);

public sealed record DifferentialBaseEvidence(
    BackupDatabaseIdentity Database,
    BackupRecoveryBranch Branch,
    Guid? BaseBackupSetGuid,
    BackupLsn? BaseLsn,
    BaselineEvidenceStatus Status = BaselineEvidenceStatus.Complete);

public sealed record DifferentialBackupEvidence(
    DifferentialBaseEvidence ActualBase,
    BackupType? Type,
    bool? IsCopyOnly,
    BackupLsn? DatabaseBackupLsn);

public sealed record FullBackupBaselineEvidence(
    Guid? BackupSetGuid,
    BackupDatabaseIdentity Database,
    BackupRecoveryBranch Branch,
    BackupType? Type,
    bool? IsCopyOnly,
    BackupLsn? CheckpointLsn,
    BaselineEvidenceStatus Status = BaselineEvidenceStatus.Complete);

public sealed record ManagedFullBackupCandidate(Guid BackupSetId, FullBackupBaselineEvidence Evidence);

public sealed record ActiveDataFileBaselineEvidence(
    Guid? BaseBackupSetGuid,
    BackupLsn? BaseLsn,
    BaselineEvidenceStatus Status = BaselineEvidenceStatus.Complete);

public sealed class ActiveDifferentialBaselineEvidence
{
    public ActiveDifferentialBaselineEvidence(
        BackupDatabaseIdentity database,
        Guid? currentRecoveryForkId,
        IEnumerable<ActiveDataFileBaselineEvidence> dataFileBases,
        BaselineEvidenceStatus status = BaselineEvidenceStatus.Complete)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(dataFileBases);
        Database = database;
        CurrentRecoveryForkId = currentRecoveryForkId;
        DataFileBases = Array.AsReadOnly(dataFileBases.ToArray());
        Status = status;
    }

    public BackupDatabaseIdentity Database { get; }

    // 当前数据库状态只有活动分支，不表示某次备份的起止分支。
    public Guid? CurrentRecoveryForkId { get; }

    public IReadOnlyList<ActiveDataFileBaselineEvidence> DataFileBases { get; }

    public BaselineEvidenceStatus Status { get; }
}

public sealed record DifferentialBaselineDecision
{
    internal DifferentialBaselineDecision(
        DifferentialBaselineConclusion conclusion,
        string reasonCode,
        Guid? managedFullId = null)
    {
        Conclusion = conclusion;
        ReasonCode = reasonCode;
        ManagedFullId = managedFullId;
    }

    public DifferentialBaselineConclusion Conclusion { get; }

    public string ReasonCode { get; }

    public Guid? ManagedFullId { get; }
}
