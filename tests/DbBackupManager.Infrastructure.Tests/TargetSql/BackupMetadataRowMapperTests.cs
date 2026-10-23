using System.Data;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.TargetSql;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class BackupMetadataRowMapperTests
{
    [Fact]
    public void MissingColumnsAndNullValuesRemainDistinctAndUnknown()
    {
        using var table = new DataTable();
        table.Columns.Add("BackupSetGUID", typeof(Guid));
        table.Rows.Add(DBNull.Value);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        var result = new BackupMetadataRowMapper(reader).Map(BackupSetEvidenceSource.BackupHeader);
        Assert.Equal(BaselineEvidenceStatus.MissingFields, result.Status);
        Assert.Equal(BackupMetadataState.Unknown, result.Metadata.BackupSetGuid.State);
        Assert.Contains(new("BackupSetGUID", BackupMetadataReadProblem.NullValue), result.Issues);
        Assert.Contains(new("FirstLSN", BackupMetadataReadProblem.MissingColumn), result.Issues);
        Assert.Equal(BackupMetadataState.Unknown, result.Metadata.DifferentialBaseLsn.State);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void ReadsByNamePreservesTwentyFiveDigitLsnsAndRawTime(int type)
    {
        using var table = new DataTable();
        table.Columns.Add("IgnoredFreeText", typeof(string));
        table.Columns.Add("BackupType", typeof(byte));
        table.Columns.Add("LastLSN", typeof(decimal));
        table.Columns.Add("FirstLSN", typeof(decimal));
        table.Columns.Add("BackupFinishDate", typeof(DateTime));
        table.Columns.Add("Compressed", typeof(byte));
        table.Rows.Add("不保存的合成自由文本", (byte)type, 1234567890123456789012346m,
            1234567890123456789012345m, new DateTime(2026, 10, 8, 1, 2, 3), (byte)0);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        var result = new BackupMetadataRowMapper(reader).Map(BackupSetEvidenceSource.BackupHeader);
        Assert.Equal(new BackupLsn(1234567890123456789012345m), result.Metadata.FirstLsn.Value);
        Assert.Equal(new BackupLsn(1234567890123456789012346m), result.Metadata.LastLsn.Value);
        Assert.Equal(DateTimeKind.Unspecified, result.Metadata.SqlFinishedLocal.Value!.Value.Kind);
        Assert.False(result.Metadata.IsCompressed.Value);
        Assert.Equal(type == 1 ? BackupMetadataState.NotApplicable : BackupMetadataState.Unknown,
            result.Metadata.DifferentialBaseLsn.State);
        Assert.DoesNotContain(result.Issues, x => x.Field == "IgnoredFreeText");
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData("10000000000000000000000000")]
    public void InvalidLsnIsUnknownRatherThanRounded(string input)
    {
        using var table = new DataTable();
        table.Columns.Add("FirstLSN", typeof(decimal));
        table.Rows.Add(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture));
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        var result = new BackupMetadataRowMapper(reader).Map(BackupSetEvidenceSource.Msdb);
        Assert.Equal(BackupMetadataState.Unknown, result.Metadata.FirstLsn.State);
        Assert.Contains(new("FirstLSN", BackupMetadataReadProblem.InvalidValue), result.Issues);
    }

    [Fact]
    public void FourLogFieldsReadByNameAndPreserveKnownFalse()
    {
        using var table = new DataTable();
        table.Columns.Add("IsForceOffline", typeof(byte));
        table.Columns.Add("BeginsLogChain", typeof(byte));
        table.Columns.Add("RecoveryModel", typeof(string));
        table.Columns.Add("HasBulkLoggedData", typeof(byte));
        table.Rows.Add((byte)0, (byte)1, "BULK_LOGGED", (byte)0);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());

        var result = new BackupMetadataRowMapper(reader).Map(BackupSetEvidenceSource.BackupHeader);

        Assert.False(result.Metadata.IsForceOffline.Value);
        Assert.True(result.Metadata.BeginsLogChain.Value);
        Assert.False(result.Metadata.HasBulkLoggedData.Value);
        Assert.Equal(DatabaseRecoveryModel.BulkLogged, result.Metadata.RecoveryModel.Value);
        Assert.DoesNotContain(result.Issues, issue => issue.Field is "BeginsLogChain" or "HasBulkLoggedData"
            or "IsForceOffline" or "RecoveryModel");
    }

    [Theory]
    [InlineData("FULL", DatabaseRecoveryModel.Full)]
    [InlineData("BULK_LOGGED", DatabaseRecoveryModel.BulkLogged)]
    [InlineData("BULK-LOGGED", DatabaseRecoveryModel.BulkLogged)]
    [InlineData("SIMPLE", DatabaseRecoveryModel.Simple)]
    public void RecoveryModelMapsBothBulkSpellingsWithoutDefaultingToFull(
        string value,
        DatabaseRecoveryModel expected)
    {
        using var table = new DataTable();
        table.Columns.Add("RecoveryModel", typeof(string));
        table.Rows.Add(value);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());

        var result = new BackupMetadataRowMapper(reader).Map(BackupSetEvidenceSource.Msdb);

        Assert.Equal(expected, result.Metadata.RecoveryModel.Value);
    }

    [Fact]
    public void UnknownRecoveryModelIsNotDefaultedToFull()
    {
        using var table = new DataTable();
        table.Columns.Add("RecoveryModel", typeof(string));
        table.Rows.Add("SOMETHING_ELSE");
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());

        var result = new BackupMetadataRowMapper(reader).Map(BackupSetEvidenceSource.Msdb);

        Assert.Equal(BackupMetadataState.Unknown, result.Metadata.RecoveryModel.State);
        Assert.Contains(new("RecoveryModel", BackupMetadataReadProblem.InvalidValue), result.Issues);
    }

    [Fact]
    public void MissingOrNullFourLogFieldsRemainUnknownNotFalse()
    {
        using var table = new DataTable();
        table.Columns.Add("BeginsLogChain", typeof(byte));
        table.Rows.Add(DBNull.Value);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());

        var result = new BackupMetadataRowMapper(reader).Map(BackupSetEvidenceSource.BackupHeader);

        Assert.Equal(BackupMetadataState.Unknown, result.Metadata.BeginsLogChain.State);
        Assert.Equal(BackupMetadataState.Unknown, result.Metadata.RecoveryModel.State);
        Assert.Equal(BackupMetadataState.Unknown, result.Metadata.HasBulkLoggedData.State);
        Assert.Equal(BackupMetadataState.Unknown, result.Metadata.IsForceOffline.State);
        Assert.Contains(new("BeginsLogChain", BackupMetadataReadProblem.NullValue), result.Issues);
        Assert.Contains(new("HasBulkLoggedData", BackupMetadataReadProblem.MissingColumn), result.Issues);
    }

    [Fact]
    public void InvalidLogFlagIsUnknownAndKeepsIssue()
    {
        using var table = new DataTable();
        table.Columns.Add("BeginsLogChain", typeof(int));
        table.Rows.Add(2);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());

        var result = new BackupMetadataRowMapper(reader).Map(BackupSetEvidenceSource.Msdb);

        Assert.Equal(BackupMetadataState.Unknown, result.Metadata.BeginsLogChain.State);
        Assert.Contains(new("BeginsLogChain", BackupMetadataReadProblem.InvalidValue), result.Issues);
    }
}
