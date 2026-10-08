using System.Data;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;
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
}
