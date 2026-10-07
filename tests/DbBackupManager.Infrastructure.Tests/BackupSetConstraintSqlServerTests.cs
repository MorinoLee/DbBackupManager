using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupSetConstraintSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    // 独立列清单同时覆盖两个表；每个 Known 分支都必须拒绝 NULL。
    public static TheoryData<string, string> NullableMetadataColumns
    {
        get
        {
            string[] columns =
            [
                "BackupSetGuid",
                "DatabaseGuid",
                "FamilyGuid",
                "Type",
                "FirstLsn",
                "LastLsn",
                "CheckpointLsn",
                "DatabaseBackupLsn",
                "DifferentialBaseLsn",
                "DifferentialBaseGuid",
                "FirstRecoveryForkId",
                "RecoveryForkId",
                "ForkPointLsn",
                "IsCopyOnly",
                "HasBackupChecksums",
                "IsCompressed",
                "IsDamaged",
                "IsSnapshot",
                "HasIncompleteMetadata",
                "SqlStartedLocal",
                "SqlFinishedLocal",
            ];
            var data = new TheoryData<string, string>();
            foreach (var table in new[] { "BackupSets", "BackupSetEvidence" })
                foreach (var column in columns) data.Add(table, column);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(NullableMetadataColumns))]
    public async Task KnownNullableValueCannotBeNullAndUnknownCannotContainAValue(string table, string column)
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var command = BackupSetTestData.Command(work, new()) with
        {
            Assessment = new(BackupMetadataState.Known, DifferentialBaselineConclusion.Unknown,
                DifferentialBaselineReason.MissingFields)
        };
        await BackupSetTestData.RegisterAsync(database, command);
        await using var db = database.CreateContext();
        var id = table == "BackupSets" ? command.BackupSetId :
            await db.BackupSetEvidence.Where(x => x.MutationId == command.MutationId && x.EntryNumber == 0)
                .Select(x => x.Id).SingleAsync();
        // 表名和列名只来自固定的测试清单，值通过参数传入。
        var nullSql = $"UPDATE [{table}] SET [{column}State]='Known',[{column}]=NULL WHERE [Id]={{0}}";
        var nullError = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(nullSql, id));
        Assert.Equal(547, nullError.Number);
        Assert.Contains($"CK_{table}_{(column == "Type" ? "" : column)}", nullError.Message, StringComparison.Ordinal);
        var stateSql = $"UPDATE [{table}] SET [{column}State]=NULL WHERE [Id]={{0}}";
        var stateError = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(stateSql, id));
        Assert.Equal(515, stateError.Number);
        var value = column.EndsWith("Lsn", StringComparison.Ordinal) ? "1"
            : column is "Type" ? "'Differential'"
            : column.StartsWith("Sql", StringComparison.Ordinal) ? "'2026-10-07T12:00:00'"
            : column.StartsWith("Is", StringComparison.Ordinal) || column.StartsWith("Has", StringComparison.Ordinal) ? "1"
            : "'00000001-0000-0000-0000-000000000001'";
        var unknownSql = $"UPDATE [{table}] SET [{column}State]='Unknown',[{column}]={value} WHERE [Id]={{0}}";
        var unknownError = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(unknownSql, id));
        Assert.Equal(547, unknownError.Number);
        Assert.Contains($"CK_{table}_{(column == "Type" ? "" : column)}", unknownError.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string, string, int> ConditionalConstraintViolations => new()
    {
        { "BackupSets", "ReconciliationCount", "[ReconciliationCount]=NULL", 515 },
        { "BackupSets", "ReconciliationCount", "[ReconciliationCount]=0", 547 },
        { "BackupSets", "Dependency", "[BaseBackupSetId]=[Id]", 547 },
        { "BackupSets", "Conflict", "[HasMetadataConflict]=1,[AssessmentState]='NotApplicable',[Conclusion]=NULL,[BaselineReasonCode]=NULL", 547 },
        { "BackupSetEvidence", "Sequence", "[ReconciliationNumber]=NULL", 515 },
        { "BackupSetEvidence", "Sequence", "[EntryNumber]=NULL", 515 },
        { "BackupSetEvidence", "Sequence", "[ReconciliationNumber]=0", 547 },
        { "BackupSetEvidence", "Sequence", "[EntryNumber]=-1", 547 },
        { "BackupSetEvidence", "Source", "[Source]=NULL", 515 },
        { "BackupSetEvidence", "Source", "[Source]='Other'", 547 },
        { "BackupSetEvidence", "Kind", "[Kind]=NULL", 515 },
        { "BackupSetEvidence", "Kind", "[Kind]='Other'", 547 },
        { "BackupSetEvidence", "ObservedAtUtc", "[ObservedAtUtc]=NULL", 515 },
        { "BackupSetEvidence", "ObservedAtUtc", "[ObservedAtUtc]='2026-10-07T12:00:00+01:00'", 547 },
        { "BackupSets", "DifferentialFields", "[TypeState]='Known',[Type]='Full'", 547 },
        { "BackupSetEvidence", "DifferentialFields", "[TypeState]='Known',[Type]='Full'", 547 },
        { "BackupSets", "Assessment", "[Conclusion]=NULL", 547 },
        { "BackupSetEvidence", "Assessment", "[Conclusion]=NULL", 547 },
        { "BackupSets", "Assessment", "[BaselineReasonCode]=NULL", 547 },
        { "BackupSetEvidence", "Assessment", "[BaselineReasonCode]=NULL", 547 },
        { "BackupSets", "Assessment", "[BaselineReasonCode]='baseline.unrecognized'", 547 },
        { "BackupSetEvidence", "Assessment", "[BaselineReasonCode]='baseline.unrecognized'", 547 },
        { "BackupSets", "Completion", "[CompletionSource]='PlatformObserved',[CompletionReasonCode]='completion.platform_observed',[CompletedAtUtc]=NULL", 547 },
        { "BackupSetEvidence", "Completion", "[CompletionSource]='PlatformObserved',[CompletionReasonCode]='completion.platform_observed',[CompletedAtUtc]=NULL", 547 },
        { "BackupSets", "Completion", "[CompletionSource]='SqlLocalTime',[CompletionReasonCode]='completion.sql_local_converted',[CompletedAtUtc]=NULL", 547 },
        { "BackupSetEvidence", "Completion", "[CompletionSource]='SqlLocalTime',[CompletionReasonCode]='completion.sql_local_converted',[CompletedAtUtc]=NULL", 547 },
        { "BackupSets", "Completion", "[CompletionReasonCode]=NULL", 515 },
        { "BackupSetEvidence", "Completion", "[CompletionReasonCode]=NULL", 515 },
        { "BackupSets", "Completion", "[CompletionSource]='Unknown',[CompletedAtUtc]='2026-10-07T12:00:00+00:00'", 547 },
        { "BackupSetEvidence", "Completion", "[CompletionSource]='Unknown',[CompletedAtUtc]='2026-10-07T12:00:00+00:00'", 547 }
    };

    [Theory]
    [MemberData(nameof(ConditionalConstraintViolations))]
    public async Task ConditionalChecksRejectNullAndOtherInvalidCombinations(
        string table, string constraint, string assignments, int number)
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var command = BackupSetTestData.Command(work, new()) with
        {
            Assessment = new(BackupMetadataState.Known, DifferentialBaselineConclusion.Unknown,
                DifferentialBaselineReason.MissingFields),
            Completion = new(null, BackupCompletionTimeSource.Unknown, BackupCompletionTimeReason.SqlFinishMissing)
        };
        await BackupSetTestData.RegisterAsync(database, command);
        await using var db = database.CreateContext();
        var id = table == "BackupSets" ? command.BackupSetId :
            await db.BackupSetEvidence.Where(x => x.MutationId == command.MutationId && x.EntryNumber == 0)
                .Select(x => x.Id).SingleAsync();
        var violationSql = $"UPDATE [{table}] SET {assignments} WHERE [Id]={{0}}";
        var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(violationSql, id));
        Assert.Equal(number, error.Number);
        if (number == 547) Assert.Contains($"CK_{table}_{constraint}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryNewCheckHasAnExplicitNullViolationCaseAndAllForeignKeysRestrict()
    {
        using var db = database.CreateContext();
        foreach (var entity in new[] { typeof(BackupSet), typeof(BackupSetEvidence) })
        {
            var model = db.GetService<IDesignTimeModel>().Model.FindEntityType(entity)!;
            Assert.All(model.GetForeignKeys(), fk => Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior));
            var table = model.GetTableName()!;
            var covered = NullableMetadataColumns.Select(x => $"CK_{x[0]}_{x[1]}")
                .Concat(ConditionalConstraintViolations.Select(x => $"CK_{x[0]}_{x[1]}")).ToHashSet(StringComparer.Ordinal);
            Assert.All(model.GetCheckConstraints(), check => Assert.Contains(check.Name!, covered));
            Assert.DoesNotContain(model.GetProperties(), property => property.Name is "UserName" or "ServerName"
                or "MachineName" or "BackupName" or "BackupDescription");
            Assert.Equal(entity == typeof(BackupSet) ? "BackupSets" : "BackupSetEvidence", table);
        }
    }
}
