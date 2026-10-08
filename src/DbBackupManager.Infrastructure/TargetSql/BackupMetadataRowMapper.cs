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
            SqlStartedLocal = Local("BackupStartDate"),
            SqlFinishedLocal = Local("BackupFinishDate")
        };
        metadata.Validate();
        return new(source, metadata, Issues.Count == 0 ? BaselineEvidenceStatus.Complete : BaselineEvidenceStatus.MissingFields, Issues.AsReadOnly());
    }
}
