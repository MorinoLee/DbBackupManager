using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class TargetSqlLogReadinessLocalDbTests(ITestOutputHelper output)
{
    [Fact]
    public async Task B05B10OnlineFullDatabaseWithObservedNullLastLogLsnIsReadOnly()
    {
        await using var scope = new LocalDbBackupMetadataProofScope(output);
        await scope.InitializeAsync();
        var active = await scope.ReadActiveAsync();
        var reader = new SqlClientTargetSqlLogReadinessReader(
            new MetadataTestCredentialResolver(),
            new MetadataTestSessionFactory(scope.NewMetadataConnection));
        var request = new TargetSqlLogReadinessRequest(
            Guid.NewGuid(),
            scope.DatabaseName,
            active.Database,
            30);

        var evidence = await reader.ReadAsync(Connection(), request);

        Assert.NotNull(evidence.Server);
        Assert.Equal(TargetSqlDatabaseState.Online, evidence.Database.State);
        Assert.Equal(TargetSqlRecoveryModel.Full, evidence.Database.RecoveryModel);
        Assert.True(evidence.Database.QueryComplete);
        Assert.True(evidence.Recovery.WasAttempted);
        Assert.Equal(TargetSqlLogLsnState.ObservedNull, evidence.Recovery.LastLogBackupLsnState);
        Assert.Null(evidence.Recovery.LastLogBackupLsn);
        Assert.True(evidence.ExpectedIdentityMatches);
        Assert.True(evidence.StableAcrossRead);
        Assert.Equal(scope.DatabaseName, evidence.Database.Name);
        Assert.NotEqual(default, evidence.Database.ObservedAtUtc);
        Assert.NotEqual(default, evidence.Recovery.ObservedAtUtc);

        await using var command = new SqlCommand(
            "ALTER DATABASE [" + scope.DatabaseName.Replace("]", "]]", StringComparison.Ordinal) + "] SET RECOVERY SIMPLE;",
            scope.Connection);
        await command.ExecuteNonQueryAsync();
        var simple = await reader.ReadAsync(Connection(), request);
        Assert.Equal(TargetSqlRecoveryModel.Simple, simple.Database.RecoveryModel);
        Assert.True(simple.ExpectedIdentityMatches);
    }

    [Fact]
    public async Task B08DifferentExpectedGuidIsReportedWithoutRebinding()
    {
        await using var scope = new LocalDbBackupMetadataProofScope(output);
        await scope.InitializeAsync();
        var active = await scope.ReadActiveAsync();
        var reader = new SqlClientTargetSqlLogReadinessReader(
            new MetadataTestCredentialResolver(),
            new MetadataTestSessionFactory(scope.NewMetadataConnection));
        var wrong = new DbBackupManager.Domain.BackupSets.BackupDatabaseIdentity(Guid.NewGuid(), active.Database.FamilyGuid);
        var request = new TargetSqlLogReadinessRequest(Guid.NewGuid(), scope.DatabaseName, wrong, 30);

        var evidence = await reader.ReadAsync(Connection(), request);

        Assert.False(evidence.ExpectedIdentityMatches);
        Assert.Equal(active.Database.DatabaseGuid, evidence.Recovery.Database!.DatabaseGuid);
        Assert.Equal(wrong, evidence.ExpectedDatabase);
    }

    [Fact]
    public async Task B07OfflineOrInvisibleDatabaseDoesNotAttemptRecoveryStatus()
    {
        await using var scope = new LocalDbBackupMetadataProofScope(output);
        await scope.InitializeAsync();
        var active = await scope.ReadActiveAsync();
        var reader = new SqlClientTargetSqlLogReadinessReader(
            new MetadataTestCredentialResolver(),
            new MetadataTestSessionFactory(scope.NewMetadataConnection));
        var request = new TargetSqlLogReadinessRequest(
            Guid.NewGuid(), scope.DatabaseName, active.Database, 30);
        try
        {
            await using (var offline = new SqlCommand(
                "ALTER DATABASE [" + scope.DatabaseName.Replace("]", "]]", StringComparison.Ordinal)
                + "] SET OFFLINE WITH ROLLBACK IMMEDIATE;", scope.Connection))
            {
                await offline.ExecuteNonQueryAsync();
            }

            var evidence = await reader.ReadAsync(Connection(), request);
            Assert.Equal(TargetSqlDatabaseState.Offline, evidence.Database.State);
            Assert.False(evidence.Recovery.WasAttempted);
            Assert.Contains(new("recovery_status", BackupMetadataReadProblem.NotAttempted),
                evidence.Recovery.Issues);
        }
        finally
        {
            await using var online = new SqlCommand(
                "ALTER DATABASE [" + scope.DatabaseName.Replace("]", "]]", StringComparison.Ordinal) + "] SET ONLINE;",
                scope.Connection);
            await online.ExecuteNonQueryAsync();
        }

        var invisible = await reader.ReadAsync(Connection(), new TargetSqlLogReadinessRequest(
            request.PlatformDatabaseId,
            scope.DatabaseName + "_missing",
            request.ExpectedDatabase,
            30));
        Assert.Null(invisible.Database.State);
        Assert.Equal(TargetSqlFailureCode.NotFoundOrNotVisible, invisible.Database.FailureCode);
        Assert.False(invisible.Recovery.WasAttempted);
    }

    private static TargetSqlConnectionInput Connection() =>
        new("synthetic-sql", Guid.NewGuid(), true, false, null, 30);
}
