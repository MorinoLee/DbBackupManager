using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class LogMetadataMigrationSqlServerTests
{
    [Fact]
    public async Task B15EmptyDatabaseRoundTripsAndAllFourHostsReceiveUnknownDefaults()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        await using var db = fixture.CreateContext();
        var migrations = db.Database.GetMigrations().ToArray();
        var latest = migrations[^1];
        var previous = migrations[^2];
        Assert.EndsWith("AddLogMetadataFields", latest, StringComparison.Ordinal);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(previous);
        Assert.Equal(0, await CountColumnsAsync(db));

        await migrator.MigrateAsync(latest);

        Assert.Equal(33, await CountColumnsAsync(db));
        Assert.Equal(16, await CountUnknownStateDefaultsAsync(db));
        Assert.Equal(1, await CountSourceObservedAtUtcAsync(db));
        Assert.Equal(latest, (await db.Database.GetAppliedMigrationsAsync()).Last());
    }

    [Fact]
    public async Task B15OldBackupSetRowUpgradesToUnknownAndBlocksDown()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        await using var db = fixture.CreateContext();
        var migrations = db.Database.GetMigrations().ToArray();
        var latest = migrations[^1];
        var previous = migrations[^2];
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE [BackupSets] NOCHECK CONSTRAINT [FK_BackupSets_BackupAttempts_TaskId_AttemptId];
            ALTER TABLE [BackupSets] NOCHECK CONSTRAINT [FK_BackupSets_ManagedDatabases_DatabaseId];
            INSERT INTO [BackupSets] (
                [Id],[TaskId],[AttemptId],[DatabaseId],[SqlSuccessObserved],[HasMetadataConflict],
                [ReconciliationCount],[AssessmentState],[CompletionReasonCode],[CompletionSource],
                [BackupSetGuidState],[CheckpointLsnState],[DatabaseBackupLsnState],[DatabaseGuidState],
                [DifferentialBaseGuidState],[DifferentialBaseLsnState],[FamilyGuidState],[FirstLsnState],
                [FirstRecoveryForkIdState],[ForkPointLsnState],[HasBackupChecksumsState],
                [HasIncompleteMetadataState],[IsCompressedState],[IsCopyOnlyState],[IsDamagedState],
                [IsSnapshotState],[LastLsnState],[RecoveryForkIdState],[SqlFinishedLocalState],
                [SqlStartedLocalState],[TypeState],[CreatedAtUtc],[UpdatedAtUtc])
            VALUES (@id, @task, @attempt, @database, 1, 0, 1, 'NotApplicable', 'completion.sql_finish_missing',
                'Unknown', 'Unknown','Unknown','Unknown','Unknown','Unknown','Unknown','Unknown','Unknown',
                'Unknown','Unknown','Unknown','Unknown','Unknown','Unknown','Unknown','Unknown','Unknown',
                'Unknown','Unknown','Unknown','Unknown', @now, @now);
            """,
            new Microsoft.Data.SqlClient.SqlParameter("@id", id),
            new Microsoft.Data.SqlClient.SqlParameter("@task", Guid.NewGuid()),
            new Microsoft.Data.SqlClient.SqlParameter("@attempt", Guid.NewGuid()),
            new Microsoft.Data.SqlClient.SqlParameter("@database", Guid.NewGuid()),
            new Microsoft.Data.SqlClient.SqlParameter("@now", now));

        await migrator.MigrateAsync(latest);

        db.ChangeTracker.Clear();
        var upgraded = await db.BackupSets.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(BackupMetadataState.Unknown, upgraded.Metadata.RecoveryModel.State);
        Assert.Equal(BackupMetadataState.Unknown, upgraded.Metadata.BeginsLogChain.State);
        Assert.Equal(BackupMetadataState.Unknown, upgraded.Metadata.HasBulkLoggedData.State);
        Assert.Equal(BackupMetadataState.Unknown, upgraded.Metadata.IsForceOffline.State);

        var error = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(
            () => migrator.MigrateAsync(previous));
        Assert.Equal(51014, error.Number);
        Assert.True(await db.BackupSets.AsNoTracking().AnyAsync(x => x.Id == id));
    }

    [Fact]
    public async Task B10B13NewFieldsAndSourceTimePersistAndReplay()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        var work = await BackupSetTestData.WorkAsync(fixture);
        var metadata = BackupSetTestData.Full() with
        {
            RecoveryModel = BackupMetadata.Known(DbBackupManager.Domain.Configuration.DatabaseRecoveryModel.Full),
            BeginsLogChain = BackupMetadata.Known(false),
            HasBulkLoggedData = BackupMetadata.Known(false),
            IsForceOffline = BackupMetadata.Known(false),
        };
        var sourceAt = new DateTimeOffset(2026, 10, 8, 1, 0, 0, TimeSpan.Zero);
        var baseCommand = BackupSetTestData.Command(work, metadata);
        var command = baseCommand with
        {
            Observations =
            [
                new(BackupSetEvidenceSource.BackupHeader, BackupSetEvidenceKind.Backup,
                    metadata, baseCommand.Completion,
                    BackupSetAssessment.NotApplicable, sourceAt),
            ],
        };

        var first = await BackupSetTestData.RegisterAsync(fixture, command);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, first.Code);
        await using var db = fixture.CreateContext();
        var evidence = await db.BackupSetEvidence.AsNoTracking()
            .SingleAsync(x => x.MutationId == command.MutationId && x.EntryNumber == 2);
        Assert.Equal(DatabaseRecoveryModel.Full, evidence.Metadata.RecoveryModel.Value);
        Assert.False(evidence.Metadata.BeginsLogChain.Value);
        Assert.Equal(sourceAt, evidence.SourceObservedAtUtc);
        var set = await db.BackupSets.AsNoTracking().SingleAsync(x => x.Id == command.BackupSetId);
        Assert.Equal(DatabaseRecoveryModel.Full, set.Metadata.RecoveryModel.Value);

        var replay = await BackupSetTestData.RegisterAsync(fixture, command);
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, replay.Code);
        Assert.Equal(3, await db.BackupSetEvidence.CountAsync(x => x.MutationId == command.MutationId));
    }

    private static Task<int> CountColumnsAsync(PlatformDbContext db) => db.Database.SqlQueryRaw<int>(
        """
        SELECT COUNT(*) AS [Value]
        FROM sys.columns AS c
        WHERE OBJECT_NAME(c.object_id) IN ('BackupSets','BackupSetEvidence',
            'BackupPlanExecutionOperations','BackupPlanExecutionObservations')
          AND c.name IN ('RecoveryModel','RecoveryModelState','BeginsLogChain','BeginsLogChainState',
              'HasBulkLoggedData','HasBulkLoggedDataState','IsForceOffline','IsForceOfflineState',
              'SourceObservedAtUtc')
        """).SingleAsync();

    private static Task<int> CountUnknownStateDefaultsAsync(PlatformDbContext db) => db.Database.SqlQueryRaw<int>(
        """
        SELECT COUNT(*) AS [Value]
        FROM sys.columns AS c
        JOIN sys.default_constraints AS d ON d.object_id = c.default_object_id
        WHERE OBJECT_NAME(c.object_id) IN ('BackupSets','BackupSetEvidence',
            'BackupPlanExecutionOperations','BackupPlanExecutionObservations')
          AND c.name IN ('RecoveryModelState','BeginsLogChainState',
              'HasBulkLoggedDataState','IsForceOfflineState')
          AND d.definition LIKE N'%Unknown%'
        """).SingleAsync();

    private static Task<int> CountSourceObservedAtUtcAsync(PlatformDbContext db) => db.Database.SqlQueryRaw<int>(
        """
        SELECT COUNT(*) AS [Value]
        FROM sys.columns AS c
        WHERE OBJECT_NAME(c.object_id) = 'BackupSetEvidence' AND c.name = 'SourceObservedAtUtc'
        """).SingleAsync();
}
