using System.Data;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Infrastructure.TargetSql;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class LogReadinessValuesTests
{
    [Fact]
    public void B05ObservedNullIsNotUnknownAndMissingColumnIsUnknown()
    {
        using var nullTable = new DataTable();
        nullTable.Columns.Add("last_log_backup_lsn", typeof(decimal));
        nullTable.Rows.Add(DBNull.Value);
        using var nullReader = nullTable.CreateDataReader();
        Assert.True(nullReader.Read());
        var nullIssues = new List<BackupMetadataReadIssue>();
        var observedNull = SqlClientTargetSqlSession.ReadLastLogBackupLsn(nullReader, nullIssues);
        Assert.Equal(TargetSqlLogLsnState.ObservedNull, observedNull.State);
        Assert.Null(observedNull.Lsn);
        Assert.Empty(nullIssues);

        using var missingTable = new DataTable();
        missingTable.Columns.Add("other", typeof(int));
        missingTable.Rows.Add(1);
        using var missingReader = missingTable.CreateDataReader();
        Assert.True(missingReader.Read());
        var missingIssues = new List<BackupMetadataReadIssue>();
        var missing = SqlClientTargetSqlSession.ReadLastLogBackupLsn(missingReader, missingIssues);
        Assert.Equal(TargetSqlLogLsnState.Unknown, missing.State);
        Assert.Contains(new("last_log_backup_lsn", BackupMetadataReadProblem.MissingColumn), missingIssues);
    }

    [Fact]
    public void B03TwentyFiveDigitLsnIsExactAndInvalidValuesStayUnknown()
    {
        using var table = new DataTable();
        table.Columns.Add("last_log_backup_lsn", typeof(decimal));
        table.Rows.Add(9999999999999999999999999m);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        var issues = new List<BackupMetadataReadIssue>();
        var known = SqlClientTargetSqlSession.ReadLastLogBackupLsn(reader, issues);
        Assert.Equal(TargetSqlLogLsnState.Known, known.State);
        Assert.Equal(BackupLsn.MaximumValue, known.Lsn!.Value.Value);

        using var invalidTable = new DataTable();
        invalidTable.Columns.Add("last_log_backup_lsn", typeof(decimal));
        invalidTable.Rows.Add(-1m);
        using var invalidReader = invalidTable.CreateDataReader();
        Assert.True(invalidReader.Read());
        var invalidIssues = new List<BackupMetadataReadIssue>();
        var invalid = SqlClientTargetSqlSession.ReadLastLogBackupLsn(invalidReader, invalidIssues);
        Assert.Equal(TargetSqlLogLsnState.Unknown, invalid.State);
        Assert.Contains(new("last_log_backup_lsn", BackupMetadataReadProblem.InvalidValue), invalidIssues);
    }
}
