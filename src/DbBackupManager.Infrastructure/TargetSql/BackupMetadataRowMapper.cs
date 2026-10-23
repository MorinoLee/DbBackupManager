using System.Data.Common;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Infrastructure.TargetSql;

internal sealed class BackupMetadataRowMapper(DbDataReader reader)
{
    private readonly Dictionary<string, int> columns = Enumerable.Range(0, reader.FieldCount)
        .ToDictionary(reader.GetName, x => x, StringComparer.OrdinalIgnoreCase);
    public List<BackupMetadataReadIssue> Issues { get; } = [];

    public BackupMetadataField<T> Field<T>(string column, Func<object, T> convert, bool nullNotApplicable = false) where T : struct
    {
        if (!columns.TryGetValue(column, out var ordinal))
        {
            Issues.Add(new(column, BackupMetadataReadProblem.MissingColumn));
            return default;
        }
        if (reader.IsDBNull(ordinal))
        {
            if (nullNotApplicable) return BackupMetadata.NotApplicable<T>();
            Issues.Add(new(column, BackupMetadataReadProblem.NullValue));
            return default;
        }
        try { return BackupMetadata.Known(convert(reader.GetValue(ordinal))); }
        catch (Exception exception) when (exception is ArgumentException or InvalidCastException or FormatException or OverflowException)
        {
            Issues.Add(new(column, BackupMetadataReadProblem.InvalidValue));
            return default;
        }
    }
    public BackupMetadataField<Guid> GuidField(string column) => Field(column, value =>
        value is Guid id && id != Guid.Empty ? id : throw new ArgumentException("GUID 无效。"));
    public BackupMetadataField<BackupLsn> Lsn(string column) => Field(column, value => new BackupLsn((decimal)value));
    private BackupMetadataField<bool> Flag(string column) => Field(column, value => value switch
    {
        true or (byte)1 or (short)1 => true,
        false or (byte)0 or (short)0 => false,
        _ => throw new ArgumentException("标志位无效。")
    });
    public BackupMetadataField<DateTime> Local(string column) => Field(column, value =>
        value is DateTime { Kind: DateTimeKind.Unspecified } date ? date : throw new ArgumentException("SQL 时间的 Kind 无效。"));
    public BackupMetadataField<DatabaseRecoveryModel> RecoveryModel() =>
        Field("RecoveryModel", value => value switch
        {
            string text => text.Trim().ToUpperInvariant() switch
            {
                "FULL" => DatabaseRecoveryModel.Full,
                "BULK_LOGGED" or "BULK-LOGGED" => DatabaseRecoveryModel.BulkLogged,
                "SIMPLE" => DatabaseRecoveryModel.Simple,
                _ => throw new ArgumentException("恢复模式无效。")
            },
            _ => throw new ArgumentException("恢复模式类型无效。")
        });

    public BackupMetadataSourceEvidence Map(BackupSetEvidenceSource source)
    {
        var type = Field("BackupType", value => value switch
        {
            (byte)1 or (short)1 => BackupType.Full,
            (byte)5 or (short)5 => BackupType.Differential,
            (byte)2 or (short)2 => BackupType.Log,
            _ => throw new ArgumentException("不支持的备份类型。")
        });
        var firstFork = GuidField("FirstRecoveryForkID");
        var lastFork = GuidField("RecoveryForkID");
        var metadata = new BackupSetMetadata
        {
            BackupSetGuid = GuidField("BackupSetGUID"),
            DatabaseGuid = GuidField("BindingID"),
            FamilyGuid = GuidField("FamilyGUID"),
            Type = type,
            FirstRecoveryForkId = firstFork,
            RecoveryForkId = lastFork,
            FirstLsn = Lsn("FirstLSN"),
            LastLsn = Lsn("LastLSN"),
            CheckpointLsn = Lsn("CheckpointLSN"),
            DatabaseBackupLsn = Lsn("DatabaseBackupLSN"),
            DifferentialBaseLsn = type.Value is BackupType.Full or BackupType.Log ? BackupMetadata.NotApplicable<BackupLsn>() : Lsn("DifferentialBaseLSN"),
            DifferentialBaseGuid = type.Value is BackupType.Full or BackupType.Log ? BackupMetadata.NotApplicable<Guid>() : GuidField("DifferentialBaseGUID"),
            ForkPointLsn = Field("ForkPointLSN", value => new BackupLsn((decimal)value),
                firstFork.Value is not null && firstFork.Value == lastFork.Value),
            IsCopyOnly = Flag("IsCopyOnly"),
            HasBackupChecksums = Flag("HasBackupChecksums"),
            IsCompressed = Flag("Compressed"),
            IsDamaged = Flag("IsDamaged"),
            IsSnapshot = Flag("IsSnapshot"),
            HasIncompleteMetadata = Flag("HasIncompleteMetadata"),
            RecoveryModel = RecoveryModel(),
            BeginsLogChain = Flag("BeginsLogChain"),
            HasBulkLoggedData = Flag("HasBulkLoggedData"),
            IsForceOffline = Flag("IsForceOffline"),
            SqlStartedLocal = Local("BackupStartDate"),
            SqlFinishedLocal = Local("BackupFinishDate")
        };
        metadata.Validate();
        // 新 LOG 四字段不反向要求旧 FULL/DIFF 来源提供；LOG 或类型未知时缺失仍是证据问题。
        var relevantIssues = type.Value is BackupType.Full or BackupType.Differential
            ? Issues.Where(issue => issue.Field is not ("RecoveryModel" or "BeginsLogChain"
                or "HasBulkLoggedData" or "IsForceOffline")).ToArray()
            : Issues.ToArray();
        return new(source, metadata,
            relevantIssues.Length == 0 ? BaselineEvidenceStatus.Complete : BaselineEvidenceStatus.MissingFields,
            Array.AsReadOnly(relevantIssues));
    }
}
