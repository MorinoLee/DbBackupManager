using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.BackupSets;

public enum BackupMetadataState { Unknown, Known, NotApplicable }

public readonly record struct BackupMetadataField<T> where T : struct
{
    internal BackupMetadataField(T? value, BackupMetadataState state) { Value = value; State = state; }
    public T? Value { get; init; }
    public BackupMetadataState State { get; init; }

    internal BackupMetadataField<T> Merge(BackupMetadataField<T> incoming, ref bool conflict)
    {
        if (State == BackupMetadataState.Unknown) return incoming;
        if (incoming.State != BackupMetadataState.Unknown && this != incoming) conflict = true;
        return this;
    }
}

public static class BackupMetadata
{
    public static BackupMetadataField<T> Known<T>(T value) where T : struct => new(value, BackupMetadataState.Known);
    public static BackupMetadataField<T> NotApplicable<T>() where T : struct => new(null, BackupMetadataState.NotApplicable);
}

/// <summary>只包含身份、LSN、恢复分支、类型、标志和原始 SQL 本地时间。</summary>
public sealed record BackupSetMetadata
{
    public BackupMetadataField<Guid> BackupSetGuid { get; init; }
    public BackupMetadataField<Guid> DatabaseGuid { get; init; }
    public BackupMetadataField<Guid> FamilyGuid { get; init; }
    public BackupMetadataField<BackupType> Type { get; init; }
    public BackupMetadataField<BackupLsn> FirstLsn { get; init; }
    public BackupMetadataField<BackupLsn> LastLsn { get; init; }
    public BackupMetadataField<BackupLsn> CheckpointLsn { get; init; }
    public BackupMetadataField<BackupLsn> DatabaseBackupLsn { get; init; }
    public BackupMetadataField<BackupLsn> DifferentialBaseLsn { get; init; }
    public BackupMetadataField<Guid> DifferentialBaseGuid { get; init; }
    public BackupMetadataField<Guid> FirstRecoveryForkId { get; init; }
    public BackupMetadataField<Guid> RecoveryForkId { get; init; }
    public BackupMetadataField<BackupLsn> ForkPointLsn { get; init; }
    public BackupMetadataField<bool> IsCopyOnly { get; init; }
    public BackupMetadataField<bool> HasBackupChecksums { get; init; }
    public BackupMetadataField<bool> IsCompressed { get; init; }
    public BackupMetadataField<bool> IsDamaged { get; init; }
    public BackupMetadataField<bool> IsSnapshot { get; init; }
    public BackupMetadataField<bool> HasIncompleteMetadata { get; init; }
    public BackupMetadataField<DateTime> SqlStartedLocal { get; init; }
    public BackupMetadataField<DateTime> SqlFinishedLocal { get; init; }

    public void Validate()
    {
        ValidateField(BackupSetGuid);
        ValidateField(DatabaseGuid);
        ValidateField(FamilyGuid);
        ValidateField(Type);
        ValidateField(FirstLsn);
        ValidateField(LastLsn);
        ValidateField(CheckpointLsn);
        ValidateField(DatabaseBackupLsn);
        ValidateField(DifferentialBaseLsn, allowNotApplicable: true);
        ValidateField(DifferentialBaseGuid, allowNotApplicable: true);
        ValidateField(FirstRecoveryForkId);
        ValidateField(RecoveryForkId);
        ValidateField(ForkPointLsn, allowNotApplicable: true);
        ValidateField(IsCopyOnly);
        ValidateField(HasBackupChecksums);
        ValidateField(IsCompressed);
        ValidateField(IsDamaged);
        ValidateField(IsSnapshot);
        ValidateField(HasIncompleteMetadata);
        ValidateField(SqlStartedLocal);
        ValidateField(SqlFinishedLocal);
        if (Type.Value is BackupType.Full or BackupType.Log
            && (DifferentialBaseLsn.State != BackupMetadataState.NotApplicable
                || DifferentialBaseGuid.State != BackupMetadataState.NotApplicable))
            throw new ArgumentException("FULL/LOG 的差异基线必须标记为不适用。");
        if (Type.Value == BackupType.Differential
            && (DifferentialBaseLsn.State == BackupMetadataState.NotApplicable
                || DifferentialBaseGuid.State == BackupMetadataState.NotApplicable))
            throw new ArgumentException("DIFF 的差异基线不能标记为不适用。");
        if (Type.State == BackupMetadataState.Unknown
            && (DifferentialBaseLsn.State == BackupMetadataState.NotApplicable
                || DifferentialBaseGuid.State == BackupMetadataState.NotApplicable))
            throw new ArgumentException("类型未知时，差异基线不能标记为不适用。");
    }

    private static void ValidateField<T>(BackupMetadataField<T> field, bool allowNotApplicable = false) where T : struct
    {
        if (!Enum.IsDefined(field.State)
            || (field.State == BackupMetadataState.Known) != field.Value.HasValue
            || field.State == BackupMetadataState.NotApplicable && !allowNotApplicable)
            throw new ArgumentException("元数据的值与状态不一致。");
        if (field.Value is Guid id && id == Guid.Empty)
            throw new ArgumentException("已知 GUID 不能为空。");
        if (field.Value is DateTime local && local.Kind != DateTimeKind.Unspecified)
            throw new ArgumentException("SQL 本地时间必须保持未指定时区。");
        if (field.Value is BackupType type && !Enum.IsDefined(type))
            throw new ArgumentException("备份类型无效。");
    }

    public BackupSetMetadata Merge(BackupSetMetadata incoming, out bool hasConflict)
    {
        incoming.Validate();
        var conflict = false;
        var merged = new BackupSetMetadata
        {
            BackupSetGuid = BackupSetGuid.Merge(incoming.BackupSetGuid, ref conflict),
            DatabaseGuid = DatabaseGuid.Merge(incoming.DatabaseGuid, ref conflict),
            FamilyGuid = FamilyGuid.Merge(incoming.FamilyGuid, ref conflict),
            Type = Type.Merge(incoming.Type, ref conflict),
            FirstLsn = FirstLsn.Merge(incoming.FirstLsn, ref conflict),
            LastLsn = LastLsn.Merge(incoming.LastLsn, ref conflict),
            CheckpointLsn = CheckpointLsn.Merge(incoming.CheckpointLsn, ref conflict),
            DatabaseBackupLsn = DatabaseBackupLsn.Merge(incoming.DatabaseBackupLsn, ref conflict),
            DifferentialBaseLsn = DifferentialBaseLsn.Merge(incoming.DifferentialBaseLsn, ref conflict),
            DifferentialBaseGuid = DifferentialBaseGuid.Merge(incoming.DifferentialBaseGuid, ref conflict),
            FirstRecoveryForkId = FirstRecoveryForkId.Merge(incoming.FirstRecoveryForkId, ref conflict),
            RecoveryForkId = RecoveryForkId.Merge(incoming.RecoveryForkId, ref conflict),
            ForkPointLsn = ForkPointLsn.Merge(incoming.ForkPointLsn, ref conflict),
            IsCopyOnly = IsCopyOnly.Merge(incoming.IsCopyOnly, ref conflict),
            HasBackupChecksums = HasBackupChecksums.Merge(incoming.HasBackupChecksums, ref conflict),
            IsCompressed = IsCompressed.Merge(incoming.IsCompressed, ref conflict),
            IsDamaged = IsDamaged.Merge(incoming.IsDamaged, ref conflict),
            IsSnapshot = IsSnapshot.Merge(incoming.IsSnapshot, ref conflict),
            HasIncompleteMetadata = HasIncompleteMetadata.Merge(incoming.HasIncompleteMetadata, ref conflict),
            SqlStartedLocal = SqlStartedLocal.Merge(incoming.SqlStartedLocal, ref conflict),
            SqlFinishedLocal = SqlFinishedLocal.Merge(incoming.SqlFinishedLocal, ref conflict),
        };
        hasConflict = conflict;
        return conflict ? this : merged;
    }
}
