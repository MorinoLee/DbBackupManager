using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.BackupLogs;

/// <summary>同身份观察之间的稳定冲突字段；用于保留冲突而不是静默去重。</summary>
public sealed record LogObservationConflict(
    Guid BackupSetId,
    LogLocationScope Scope,
    string FieldCode);

public static class LogFactField
{
    public const string Type = "type";
    public const string CopyOnly = "copy_only";
    public const string RecoveryModel = "recovery_model";
    public const string BeginsLogChain = "begins_log_chain";
    public const string BulkLoggedData = "bulk_logged_data";
    public const string ForceOffline = "force_offline";
    public const string UseChecksum = "use_checksum";
    public const string UseCompression = "use_compression";
    public const string Damaged = "damaged";
    public const string Snapshot = "snapshot";
    public const string Incomplete = "incomplete";
    public const string FirstLsn = "first_lsn";
    public const string LastLsn = "last_lsn";
    public const string DatabaseBackupLsn = "database_backup_lsn";
    public const string ForkPointLsn = "fork_point_lsn";
    public const string DatabaseGuid = "database_guid";
    public const string FamilyGuid = "family_guid";
    public const string FirstRecoveryFork = "first_recovery_fork";
    public const string RecoveryFork = "recovery_fork";
    public const string Generation = "generation";
    public const string CompletionTime = "completion_time";
    public const string EvidenceStatus = "evidence_status";
    public const string IsManaged = "is_managed";
    public const string CopyState = "copy_state";
    public const string DifferentialDependency = "differential_dependency";
    public const string MatchesFull = "matches_full";
    public const string ProtectionExpectation = "protection_expectation";
}

internal sealed record LogObservationMerge(
    LogBackupActualMetadata Merged,
    string? ConflictField);

/// <summary>
/// 同身份观察的补全/冲突规则：双方都已知且不同才是冲突；缺值可由另一观察补全，
/// 但不把缺值填成 false，也不静默选第一项。
/// </summary>
internal static class LogObservationReconciler
{
    public static LogObservationMerge Reconcile(
        LogBackupActualMetadata left,
        LogBackupActualMetadata right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Status == LogEvidenceStatus.SourceConflict
            || right.Status == LogEvidenceStatus.SourceConflict)
        {
            return new LogObservationMerge(left, LogFactField.EvidenceStatus);
        }

        var conflict = FirstConflict(left, right);
        return conflict is null
            ? new LogObservationMerge(Merge(left, right), null)
            : new LogObservationMerge(left, conflict);
    }

    private static string? FirstConflict(
        LogBackupActualMetadata left,
        LogBackupActualMetadata right)
    {
        if (Differ(left.Type, right.Type))
        {
            return LogFactField.Type;
        }

        if (Differ(left.IsCopyOnly, right.IsCopyOnly))
        {
            return LogFactField.CopyOnly;
        }

        if (Differ(left.RecoveryModel, right.RecoveryModel))
        {
            return LogFactField.RecoveryModel;
        }

        if (Differ(left.BeginsLogChain, right.BeginsLogChain))
        {
            return LogFactField.BeginsLogChain;
        }

        if (Differ(left.HasBulkLoggedData, right.HasBulkLoggedData))
        {
            return LogFactField.BulkLoggedData;
        }

        if (Differ(left.IsForceOffline, right.IsForceOffline))
        {
            return LogFactField.ForceOffline;
        }

        if (Differ(left.UseChecksum, right.UseChecksum))
        {
            return LogFactField.UseChecksum;
        }

        if (Differ(left.UseCompression, right.UseCompression))
        {
            return LogFactField.UseCompression;
        }

        if (Differ(left.IsDamaged, right.IsDamaged))
        {
            return LogFactField.Damaged;
        }

        if (Differ(left.IsSnapshot, right.IsSnapshot))
        {
            return LogFactField.Snapshot;
        }

        if (Differ(left.IsIncomplete, right.IsIncomplete))
        {
            return LogFactField.Incomplete;
        }

        if (Differ(left.FirstLsn, right.FirstLsn))
        {
            return LogFactField.FirstLsn;
        }

        if (Differ(left.LastLsn, right.LastLsn))
        {
            return LogFactField.LastLsn;
        }

        if (Differ(left.DatabaseBackupLsn, right.DatabaseBackupLsn))
        {
            return LogFactField.DatabaseBackupLsn;
        }

        if (Differ(left.ForkPointLsn, right.ForkPointLsn))
        {
            return LogFactField.ForkPointLsn;
        }

        if (left.Database is { } leftDatabase && right.Database is { } rightDatabase)
        {
            if (Differ(leftDatabase.DatabaseGuid, rightDatabase.DatabaseGuid))
            {
                return LogFactField.DatabaseGuid;
            }

            if (Differ(leftDatabase.FamilyGuid, rightDatabase.FamilyGuid))
            {
                return LogFactField.FamilyGuid;
            }
        }

        if (left.Branch is { } leftBranch && right.Branch is { } rightBranch)
        {
            if (Differ(leftBranch.FirstRecoveryForkId, rightBranch.FirstRecoveryForkId))
            {
                return LogFactField.FirstRecoveryFork;
            }

            if (Differ(leftBranch.RecoveryForkId, rightBranch.RecoveryForkId))
            {
                return LogFactField.RecoveryFork;
            }
        }

        if (Differ(left.Generation?.GenerationId, right.Generation?.GenerationId))
        {
            return LogFactField.Generation;
        }

        return Differ(left.CompletedAtUtc, right.CompletedAtUtc)
            ? LogFactField.CompletionTime
            : null;
    }

    private static LogBackupActualMetadata Merge(
        LogBackupActualMetadata left,
        LogBackupActualMetadata right)
    {
        return new LogBackupActualMetadata
        {
            Type = left.Type ?? right.Type,
            IsCopyOnly = left.IsCopyOnly ?? right.IsCopyOnly,
            RecoveryModel = left.RecoveryModel ?? right.RecoveryModel,
            BeginsLogChain = left.BeginsLogChain ?? right.BeginsLogChain,
            HasBulkLoggedData = left.HasBulkLoggedData ?? right.HasBulkLoggedData,
            IsForceOffline = left.IsForceOffline ?? right.IsForceOffline,
            UseChecksum = left.UseChecksum ?? right.UseChecksum,
            UseCompression = left.UseCompression ?? right.UseCompression,
            IsDamaged = left.IsDamaged ?? right.IsDamaged,
            IsSnapshot = left.IsSnapshot ?? right.IsSnapshot,
            IsIncomplete = left.IsIncomplete ?? right.IsIncomplete,
            FirstLsn = left.FirstLsn ?? right.FirstLsn,
            LastLsn = left.LastLsn ?? right.LastLsn,
            DatabaseBackupLsn = left.DatabaseBackupLsn ?? right.DatabaseBackupLsn,
            ForkPointLsn = left.ForkPointLsn ?? right.ForkPointLsn,
            Database = MergeDatabase(left.Database, right.Database),
            Branch = MergeBranch(left.Branch, right.Branch),
            Generation = MergeGeneration(left.Generation, right.Generation),
            CompletedAtUtc = left.CompletedAtUtc ?? right.CompletedAtUtc,
            ObservedAtUtc = Later(left.ObservedAtUtc, right.ObservedAtUtc),
            Source = MergeSource(left.Source, right.Source),
            Status = MergeStatus(left.Status, right.Status),
        };
    }

    private static BackupDatabaseIdentity? MergeDatabase(
        BackupDatabaseIdentity? left,
        BackupDatabaseIdentity? right) =>
        left is null
            ? right
            : right is null
                ? left
                : new BackupDatabaseIdentity(
                    left.DatabaseGuid ?? right.DatabaseGuid,
                    left.FamilyGuid ?? right.FamilyGuid);

    private static BackupRecoveryBranch? MergeBranch(
        BackupRecoveryBranch? left,
        BackupRecoveryBranch? right) =>
        left is null
            ? right
            : right is null
                ? left
                : new BackupRecoveryBranch(
                    left.FirstRecoveryForkId ?? right.FirstRecoveryForkId,
                    left.RecoveryForkId ?? right.RecoveryForkId);

    private static LogChainGeneration? MergeGeneration(
        LogChainGeneration? left,
        LogChainGeneration? right)
    {
        if (left is not { IsKnown: true })
        {
            return right;
        }

        if (right is not { IsKnown: true }
            || left.GenerationId != right.GenerationId)
        {
            return left;
        }

        return left.Evidence != LogGenerationEvidenceKind.Unspecified
            ? right.Evidence == LogGenerationEvidenceKind.Unspecified
                ? left
                : (int)left.Evidence <= (int)right.Evidence ? left : right
            : right;
    }

    private static LogEvidenceStatus MergeStatus(
        LogEvidenceStatus left,
        LogEvidenceStatus right)
    {
        if (left == LogEvidenceStatus.Complete || right == LogEvidenceStatus.Complete)
        {
            return LogEvidenceStatus.Complete;
        }

        if (left == LogEvidenceStatus.SourceConflict
            || right == LogEvidenceStatus.SourceConflict)
        {
            return LogEvidenceStatus.SourceConflict;
        }

        return (LogEvidenceStatus)Math.Min((int)left, (int)right);
    }

    private static LogObservationSource MergeSource(
        LogObservationSource left,
        LogObservationSource right)
    {
        if (left == LogObservationSource.Unspecified)
        {
            return right;
        }

        if (right == LogObservationSource.Unspecified)
        {
            return left;
        }

        return (int)left <= (int)right ? left : right;
    }

    private static DateTimeOffset? Later(
        DateTimeOffset? left,
        DateTimeOffset? right) =>
        left is null
            ? right
            : right is null
                ? left
                : left >= right ? left : right;

    private static bool Differ<T>(T? left, T? right)
        where T : struct =>
        left is { } leftValue
        && right is { } rightValue
        && !EqualityComparer<T>.Default.Equals(leftValue, rightValue);
}
