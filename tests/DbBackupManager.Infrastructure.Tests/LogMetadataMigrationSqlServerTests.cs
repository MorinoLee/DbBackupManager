using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
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
    public async Task B11B15ValidLegacyFullDiffCopyOnlyRowsUpgradeAndMaterialize()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        await using var db = fixture.CreateContext();
        var migrations = db.Database.GetMigrations().ToArray();
        var latest = migrations[^1];
        var previous = migrations[^2];
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);
        await db.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE [BackupSets] NOCHECK CONSTRAINT [FK_BackupSets_BackupAttempts_TaskId_AttemptId];
            ALTER TABLE [BackupSets] NOCHECK CONSTRAINT [FK_BackupSets_ManagedDatabases_DatabaseId];
            """);
        var full = BackupSetTestData.Full() with { BackupSetGuid = BackupMetadata.Known(Guid.NewGuid()) };
        var copyOnly = full with
        {
            BackupSetGuid = BackupMetadata.Known(Guid.NewGuid()),
            IsCopyOnly = BackupMetadata.Known(true),
        };
        var differential = full with
        {
            BackupSetGuid = BackupMetadata.Known(Guid.NewGuid()),
            Type = BackupMetadata.Known(BackupType.Differential),
            DifferentialBaseGuid = BackupMetadata.Known(Guid.NewGuid()),
            DifferentialBaseLsn = BackupMetadata.Known(new BackupLsn(10)),
            DatabaseBackupLsn = BackupMetadata.Known(new BackupLsn(10)),
            ForkPointLsn = new BackupMetadataField<BackupLsn>(),
        };
        var ids = new List<Guid>();
        foreach (var metadata in new[] { full, copyOnly, differential })
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await InsertLegacySetAsync(db, id, metadata);
        }

        await migrator.MigrateAsync(latest);

        db.ChangeTracker.Clear();
        var rows = await db.BackupSets.AsNoTracking().Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id).ToArrayAsync();
        Assert.Equal(3, rows.Length);
        var byId = rows.ToDictionary(x => x.Id);
        Assert.Equal(BackupType.Full, byId[ids[0]].Metadata.Type.Value);
        Assert.False(byId[ids[0]].Metadata.IsCopyOnly.Value);
        Assert.Equal(BackupType.Full, byId[ids[1]].Metadata.Type.Value);
        Assert.True(byId[ids[1]].Metadata.IsCopyOnly.Value);
        Assert.Equal(BackupType.Differential, byId[ids[2]].Metadata.Type.Value);
        Assert.NotNull(byId[ids[2]].Metadata.DifferentialBaseGuid.Value);
        Assert.All(rows, row =>
        {
            Assert.Equal(BackupMetadataState.Unknown, row.Metadata.RecoveryModel.State);
            Assert.Equal(BackupMetadataState.Unknown, row.Metadata.BeginsLogChain.State);
            Assert.Equal(BackupMetadataState.Unknown, row.Metadata.HasBulkLoggedData.State);
            Assert.Equal(BackupMetadataState.Unknown, row.Metadata.IsForceOffline.State);
        });
    }

    [Fact]
    public async Task B11B15ValidLegacyEvidenceMaterializesWithNullSourceTime()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        await using var db = fixture.CreateContext();
        var migrations = db.Database.GetMigrations().ToArray();
        var latest = migrations[^1];
        var previous = migrations[^2];
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);
        await db.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE [BackupSets] NOCHECK CONSTRAINT [FK_BackupSets_BackupAttempts_TaskId_AttemptId];
            ALTER TABLE [BackupSets] NOCHECK CONSTRAINT [FK_BackupSets_ManagedDatabases_DatabaseId];
            ALTER TABLE [BackupSetEvidence] NOCHECK CONSTRAINT [FK_BackupSetEvidence_BackupAttempts_TaskId_AttemptId];
            ALTER TABLE [BackupSetEvidence] NOCHECK CONSTRAINT [FK_BackupSetEvidence_BackupSets_TaskId_AttemptId_BackupSetId];
            """);
        var setId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var metadata = BackupSetTestData.Full() with { BackupSetGuid = BackupMetadata.Known(Guid.NewGuid()) };
        await InsertLegacySetAsync(db, setId, metadata, taskId, attemptId);
        await InsertLegacyEvidenceAsync(db, Guid.NewGuid(), setId, taskId, attemptId, metadata);

        await migrator.MigrateAsync(latest);

        db.ChangeTracker.Clear();
        var evidence = await db.BackupSetEvidence.AsNoTracking().SingleAsync(x => x.BackupSetId == setId);
        Assert.Equal(BackupType.Full, evidence.Metadata.Type.Value);
        Assert.True(evidence.Metadata.BackupSetGuid.Value is not null);
        Assert.Null(evidence.SourceObservedAtUtc);
        Assert.Equal(BackupMetadataState.Unknown, evidence.Metadata.RecoveryModel.State);
        Assert.Equal(BackupMetadataState.Unknown, evidence.Metadata.BeginsLogChain.State);
    }

    private static async Task InsertLegacyEvidenceAsync(
        PlatformDbContext db, Guid id, Guid setId, Guid taskId, Guid attemptId, BackupSetMetadata metadata)
    {
        var parameters = new List<Microsoft.Data.SqlClient.SqlParameter>
        {
            new("@id", id),
            new("@task", taskId),
            new("@attempt", attemptId),
            new("@BackupSetId", setId),
            new("@MutationId", Guid.NewGuid()),
            new("@ObservedAtUtc", DateTimeOffset.UtcNow),
            new("@ReconciliationNumber", 1),
            new("@EntryNumber", System.Data.SqlDbType.Int) { Value = 0 },
            new("@Source", "BackupHeader"),
            new("@Kind", "Backup"),
            new("@SqlSuccessObserved", true),
            new("@AssessmentState", "NotApplicable"),
            new("@CompletionReasonCode", "completion.sql_finish_missing"),
            new("@CompletionSource", "Unknown"),
        };
        var columns = new List<string>
        {
            "Id", "TaskId", "AttemptId", "BackupSetId", "MutationId", "ObservedAtUtc",
            "ReconciliationNumber", "EntryNumber", "Source", "Kind", "SqlSuccessObserved",
            "AssessmentState", "CompletionReasonCode", "CompletionSource",
        };
        AppendMetadataColumns(metadata, columns, parameters);
        var sql = $"""
            INSERT INTO [BackupSetEvidence] ({string.Join(",", columns.Select(x => $"[{x}]"))})
            VALUES ({string.Join(",", parameters.Select(x => x.ParameterName))});
            """;
        await ExecuteInsertAsync(db, sql, parameters);
    }

    private static async Task ExecuteInsertAsync(PlatformDbContext db, string sql,
        IEnumerable<Microsoft.Data.SqlClient.SqlParameter> parameters)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = (Microsoft.Data.SqlClient.SqlCommand)connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.ParameterName, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static void AppendMetadataColumns(BackupSetMetadata metadata, List<string> columns,
        List<Microsoft.Data.SqlClient.SqlParameter> parameters)
    {
        foreach (var field in new[]
        {
            "BackupSetGuid", "CheckpointLsn", "DatabaseBackupLsn", "DatabaseGuid", "DifferentialBaseGuid",
            "DifferentialBaseLsn", "FamilyGuid", "FirstLsn", "FirstRecoveryForkId", "ForkPointLsn",
            "HasBackupChecksums", "HasIncompleteMetadata", "IsCompressed", "IsCopyOnly", "IsDamaged",
            "IsSnapshot", "LastLsn", "RecoveryForkId", "SqlFinishedLocal", "SqlStartedLocal", "Type",
        })
        {
            var value = typeof(BackupSetMetadata).GetProperty(field)!.GetValue(metadata)!;
            var state = value.GetType().GetProperty("State")!.GetValue(value)!.ToString()!;
            var raw = value.GetType().GetProperty("Value")!.GetValue(value);
            columns.Add(field + "State");
            parameters.Add(new("@" + field + "State", state));
            columns.Add(field);
            parameters.Add(new("@" + field, raw switch
            {
                null => DBNull.Value,
                BackupLsn lsn => lsn.Value,
                BackupType type => type.ToString(),
                DatabaseRecoveryModel model => model.ToString(),
                _ => raw,
            }));
        }
    }

    [Fact]
    public async Task B11B15ValidLegacyAppliedReceiptUpgradesAndReplays()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        var work = await BackupSetTestData.WorkAsync(fixture);
        var metadata = BackupSetTestData.Full() with { BackupSetGuid = BackupMetadata.Known(Guid.NewGuid()) };
        var observedAt = new DateTimeOffset(2026, 10, 8, 2, 0, 0, TimeSpan.Zero);
        var identity = new ReserveBackupExecutionOperation(Guid.NewGuid(), work.Lease.TaskId, work.Lease.BackupAttemptId,
            BackupExecutionOperationKind.Metadata, 1, Guid.NewGuid(), observedAt);
        var frozenIdentity = identity with { MutationId = Guid.NewGuid(), Sequence = 2, ObservedAtUtc = observedAt.AddSeconds(1) };
        var operations = new BackupExecutionOperationStore(
            new BackupExecutionContractSqlServerTests.Factory(fixture), TimeProvider.System);
        var facts = new BackupExecutionFacts
        {
            Outcome = BackupExecutionOutcome.Unknown,
            SqlSuccessObserved = true,
            EvidenceAtUtc = observedAt,
            Metadata = metadata,
            Completion = new(observedAt, BackupCompletionTimeSource.PlatformObserved, BackupCompletionTimeReason.PlatformObserved),
            Assessment = BackupSetAssessment.NotApplicable,
        };
        var frozenFacts = facts with { EvidenceAtUtc = observedAt.AddSeconds(1) };
        Assert.Equal(BackupExecutionContractCode.Succeeded, (await operations.ReserveAsync(work.Lease, identity)).Code);
        Assert.Equal(BackupExecutionContractCode.Succeeded, (await operations.FreezeAsync(work.Lease,
            new(identity, facts, [new(0, BackupExecutionObservationSource.BackupHeader, BackupSetEvidenceKind.Backup, facts)]))).Code);
        Assert.Equal(BackupExecutionContractCode.Succeeded, (await operations.ReserveAsync(work.Lease, frozenIdentity)).Code);
        Assert.Equal(BackupExecutionContractCode.Succeeded, (await operations.FreezeAsync(work.Lease,
            new(frozenIdentity, frozenFacts,
                [new(0, BackupExecutionObservationSource.Msdb, BackupSetEvidenceKind.Backup, frozenFacts)]))).Code);

        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE [BackupPlanExecutionOperations] SET [State] = 'Applied' WHERE [Id] = {0};", identity.MutationId);
            var migrations = db.Database.GetMigrations().ToArray();
            var latest = migrations[^1];
            await DropLogMetadataColumnsAsync(db);
            await db.GetService<IMigrator>().MigrateAsync(latest);

            var upgraded = await operations.ReadAsync(identity);
            Assert.Equal(BackupExecutionContractCode.AlreadyApplied, upgraded.Code);
            Assert.Equal(BackupExecutionOperationState.Applied, upgraded.Value!.State);
            Assert.Equal(BackupMetadataState.Unknown, upgraded.Value.Facts.Metadata.RecoveryModel.State);
            Assert.Equal(BackupMetadataState.Unknown, upgraded.Value.Facts.Metadata.BeginsLogChain.State);
            Assert.Equal(observedAt, upgraded.Value.Facts.EvidenceAtUtc);
            var observation = Assert.Single(upgraded.Value.Observations);
            Assert.Equal(observedAt, observation.Facts.EvidenceAtUtc);

            var stillFrozen = await operations.ReadAsync(frozenIdentity);
            Assert.Equal(BackupExecutionContractCode.AlreadyApplied, stillFrozen.Code);
            Assert.Equal(BackupExecutionOperationState.Frozen, stillFrozen.Value!.State);
            Assert.Equal(BackupMetadataState.Unknown, stillFrozen.Value.Facts.Metadata.HasBulkLoggedData.State);
            Assert.Equal(observedAt.AddSeconds(1), stillFrozen.Value.Facts.EvidenceAtUtc);
            Assert.Single(stillFrozen.Value.Observations);
        }
    }

    internal static async Task DropLogMetadataColumnsAsync(PlatformDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync($"""
            DECLARE @sql nvarchar(max) = N'';
            SELECT @sql = STRING_AGG(CONVERT(nvarchar(max),
                N'ALTER TABLE ' + QUOTENAME(OBJECT_NAME(parent_object_id)) + N' DROP CONSTRAINT ' + QUOTENAME(name) + N';'), N'')
            FROM sys.default_constraints
            WHERE parent_object_id IN (OBJECT_ID('BackupSets'), OBJECT_ID('BackupSetEvidence'),
                OBJECT_ID('BackupPlanExecutionOperations'), OBJECT_ID('BackupPlanExecutionObservations'))
              AND parent_column_id IN (SELECT column_id FROM sys.columns WHERE object_id = parent_object_id
                AND name IN ('RecoveryModel','RecoveryModelState','BeginsLogChain','BeginsLogChainState',
                    'HasBulkLoggedData','HasBulkLoggedDataState','IsForceOffline','IsForceOfflineState','SourceObservedAtUtc'));
            EXEC sp_executesql @sql;
            SET @sql = N'';
            SELECT @sql = STRING_AGG(CONVERT(nvarchar(max),
                N'ALTER TABLE ' + QUOTENAME(OBJECT_NAME(parent_object_id)) + N' DROP CONSTRAINT ' + QUOTENAME(name) + N';'), N'')
            FROM sys.check_constraints
            WHERE parent_object_id IN (OBJECT_ID('BackupSets'), OBJECT_ID('BackupSetEvidence'),
                OBJECT_ID('BackupPlanExecutionOperations'), OBJECT_ID('BackupPlanExecutionObservations'))
              AND (definition LIKE N'%RecoveryModel%' OR definition LIKE N'%BeginsLogChain%'
                OR definition LIKE N'%HasBulkLoggedData%' OR definition LIKE N'%IsForceOffline%'
                OR definition LIKE N'%SourceObservedAtUtc%');
            EXEC sp_executesql @sql;
            ALTER TABLE [BackupSets] DROP COLUMN [RecoveryModelState],[RecoveryModel],[BeginsLogChainState],[BeginsLogChain],
                [HasBulkLoggedDataState],[HasBulkLoggedData],[IsForceOfflineState],[IsForceOffline];
            ALTER TABLE [BackupSetEvidence] DROP COLUMN [RecoveryModelState],[RecoveryModel],[BeginsLogChainState],[BeginsLogChain],
                [HasBulkLoggedDataState],[HasBulkLoggedData],[IsForceOfflineState],[IsForceOffline],[SourceObservedAtUtc];
            ALTER TABLE [BackupPlanExecutionOperations] DROP COLUMN [RecoveryModelState],[RecoveryModel],[BeginsLogChainState],[BeginsLogChain],
                [HasBulkLoggedDataState],[HasBulkLoggedData],[IsForceOfflineState],[IsForceOffline];
            ALTER TABLE [BackupPlanExecutionObservations] DROP COLUMN [RecoveryModelState],[RecoveryModel],[BeginsLogChainState],[BeginsLogChain],
                [HasBulkLoggedDataState],[HasBulkLoggedData],[IsForceOfflineState],[IsForceOffline];
            DELETE FROM [__EFMigrationsHistory] WHERE [MigrationId] LIKE '%_AddLogMetadataFields';
            """);
    }

    private static async Task InsertLegacySetAsync(PlatformDbContext db, Guid id, BackupSetMetadata metadata)
        => await InsertLegacySetAsync(db, id, metadata, Guid.NewGuid(), Guid.NewGuid());

    private static async Task InsertLegacySetAsync(
        PlatformDbContext db, Guid id, BackupSetMetadata metadata, Guid taskId, Guid attemptId)
    {
        var parameters = new List<Microsoft.Data.SqlClient.SqlParameter>
        {
            new("@id", id),
            new("@task", taskId),
            new("@attempt", attemptId),
            new("@database", Guid.NewGuid()),
            new("@SqlSuccessObserved", true),
            new("@HasMetadataConflict", false),
            new("@ReconciliationCount", 1),
            new("@AssessmentState", "NotApplicable"),
            new("@CompletionReasonCode", "completion.sql_finish_missing"),
            new("@CompletionSource", "Unknown"),
            new("@CreatedAtUtc", DateTimeOffset.UtcNow),
            new("@UpdatedAtUtc", DateTimeOffset.UtcNow),
        };
        var columns = new List<string>
        {
            "Id", "TaskId", "AttemptId", "DatabaseId", "SqlSuccessObserved", "HasMetadataConflict",
            "ReconciliationCount", "AssessmentState", "CompletionReasonCode", "CompletionSource", "CreatedAtUtc", "UpdatedAtUtc",
        };
        AppendMetadataColumns(metadata, columns, parameters);
        var sql = $"""
            INSERT INTO [BackupSets] ({string.Join(",", columns.Select(x => $"[{x}]"))})
            VALUES ({string.Join(",", parameters.Select(x => x.ParameterName))});
            """;
        await ExecuteInsertAsync(db, sql, parameters);
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
