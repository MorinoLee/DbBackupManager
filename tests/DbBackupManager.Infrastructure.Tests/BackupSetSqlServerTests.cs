using DbBackupManager.Application.BackupSets;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupSetSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Fact]
    public async Task AllLsnColumnsRoundTripTwentyFiveDigitsWithoutLosingTheLastDigit()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var metadata = BackupSetTestData.Full() with
        {
            Type = BackupMetadata.Known(BackupType.Differential),
            DifferentialBaseLsn = BackupMetadata.Known(new BackupLsn(1234567890123456789012345m)),
            DifferentialBaseGuid = BackupMetadata.Known(Guid.NewGuid())
        };
        var command = BackupSetTestData.Command(work, metadata) with
        {
            Assessment = new(BackupMetadataState.Known, DifferentialBaselineConclusion.Unknown,
                DifferentialBaselineReason.MissingFields),
            Observations = [new(BackupSetEvidenceSource.BackupHeader, BackupSetEvidenceKind.Backup, metadata,
                new(null, BackupCompletionTimeSource.Unknown, BackupCompletionTimeReason.ServerTimeZoneUnknown),
                new(BackupMetadataState.Known, DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields))]
        };
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, (await BackupSetTestData.RegisterAsync(database, command)).Code);
        await using var db = database.CreateContext();
        var set = await db.BackupSets.SingleAsync(x => x.Id == command.BackupSetId);
        Assert.Equal(metadata, set.Metadata);
        Assert.NotEqual(set.Metadata.FirstLsn.Value, set.Metadata.LastLsn.Value);
        Assert.Equal(new BackupLsn(1234567890123456789012345m), set.Metadata.FirstLsn.Value);
        Assert.Equal(new BackupLsn(1234567890123456789012346m), set.Metadata.LastLsn.Value);
        var evidence = await db.BackupSetEvidence.SingleAsync(x => x.MutationId == command.MutationId && x.EntryNumber == 2);
        Assert.Equal(metadata, evidence.Metadata);
        Assert.Equal(DateTimeKind.Unspecified, set.Metadata.SqlFinishedLocal.Value!.Value.Kind);
        Assert.Equal(DateTimeKind.Unspecified, evidence.Metadata.SqlFinishedLocal.Value!.Value.Kind);
        Assert.Null(evidence.Completion.CompletedAtUtc);
        Assert.Equal(BackupCompletionTimeReason.ServerTimeZoneUnknown, evidence.Completion.ReasonCode);
        var columns = await db.Database.SqlQueryRaw<string>(
            "SELECT COLUMN_NAME AS [Value] FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME IN ('BackupSets','BackupSetEvidence') "
            + "AND COLUMN_NAME LIKE '%Lsn' AND (DATA_TYPE <> 'numeric' OR NUMERIC_PRECISION <> 25 OR NUMERIC_SCALE <> 0)").ToArrayAsync();
        Assert.Empty(columns);
    }

    [Fact]
    public async Task ContradictorySourcesAppendEvidenceWithoutConfirmingEitherGuid()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var header = BackupSetTestData.Full();
        var history = header with { BackupSetGuid = BackupMetadata.Known(Guid.NewGuid()) };
        var command = BackupSetTestData.Command(work, header);
        command = command with
        {
            Observations =
            [
                new(BackupSetEvidenceSource.BackupHeader, BackupSetEvidenceKind.Backup,
                    header, command.Completion, BackupSetAssessment.NotApplicable),
                new(BackupSetEvidenceSource.Msdb, BackupSetEvidenceKind.Backup,
                    history, command.Completion, BackupSetAssessment.NotApplicable)
            ]
        };
        var result = await BackupSetTestData.RegisterAsync(database, command);
        Assert.True(result.Value!.HasConflict);
        await using var db = database.CreateContext();
        var set = await db.BackupSets.SingleAsync(x => x.Id == command.BackupSetId);
        Assert.Equal(BackupMetadataState.Unknown, set.Metadata.BackupSetGuid.State);
        Assert.Null(set.Metadata.BackupSetGuid.Value);
        Assert.True(set.SqlSuccessObserved);
        Assert.Equal(command.Completion, set.Completion);
        var evidence = await db.BackupSetEvidence.Where(x => x.MutationId == command.MutationId).ToArrayAsync();
        Assert.Equal(4, evidence.Length);
        Assert.Contains(evidence, x => x.Source == BackupSetEvidenceSource.BackupHeader && x.Metadata == header);
        Assert.Contains(evidence, x => x.Source == BackupSetEvidenceSource.Msdb && x.Metadata == history);
    }

    [Fact]
    public async Task VerifiedDependencySurvivesLaterExternalFullActiveBaselineEvidence()
    {
        var fullWork = await BackupSetTestData.WorkAsync(database);
        var full = BackupSetTestData.Command(fullWork);
        await BackupSetTestData.RegisterAsync(database, full);
        await using var db = database.CreateContext();
        var policyId = await db.BackupTasks.Where(x => x.Id == fullWork.Task.TaskId).Select(x => x.PolicyId).SingleAsync();
        var diffWork = await BackupSetTestData.WorkAsync(database, policyId);
        var facts = full.Metadata with
        {
            BackupSetGuid = BackupMetadata.Known(Guid.NewGuid()),
            Type = BackupMetadata.Known(BackupType.Differential),
            DatabaseBackupLsn = full.Metadata.CheckpointLsn,
            DifferentialBaseLsn = full.Metadata.CheckpointLsn,
            DifferentialBaseGuid = full.Metadata.BackupSetGuid
        };
        var identity = new BackupDatabaseIdentity(facts.DatabaseGuid.Value, facts.FamilyGuid.Value);
        var branch = new BackupRecoveryBranch(facts.FirstRecoveryForkId.Value, facts.RecoveryForkId.Value);
        var decision = DifferentialBaselineRules.EvaluateDependency(identity,
            new(new(identity, branch, facts.DifferentialBaseGuid.Value, facts.DifferentialBaseLsn.Value),
                BackupType.Differential, false, facts.DatabaseBackupLsn.Value),
            [new(full.BackupSetId, new(full.Metadata.BackupSetGuid.Value, identity, branch,
                BackupType.Full, false, full.Metadata.CheckpointLsn.Value))]);
        Assert.Equal(DifferentialBaselineConclusion.Verified, decision.Conclusion);
        var diff = BackupSetTestData.Command(diffWork, facts) with
        {
            Assessment = BackupSetAssessment.FromDecision(decision),
            BaseBackupSetId = decision.ManagedFullId
        };
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, (await BackupSetTestData.RegisterAsync(database, diff)).Code);
        var external = full.Metadata with
        {
            BackupSetGuid = BackupMetadata.Known(Guid.NewGuid()),
            CheckpointLsn = BackupMetadata.Known(new BackupLsn(1234567890123456789012346m))
        };
        var rawDatabaseBase = new BackupSetMetadata
        {
            DatabaseGuid = external.DatabaseGuid,
            FamilyGuid = external.FamilyGuid,
            FirstRecoveryForkId = external.FirstRecoveryForkId,
            RecoveryForkId = external.RecoveryForkId,
            DifferentialBaseGuid = external.BackupSetGuid,
            DifferentialBaseLsn = external.CheckpointLsn
        };
        var unmanaged = new BackupSetAssessment(BackupMetadataState.Known,
            DifferentialBaselineConclusion.Unmanaged, DifferentialBaselineReason.ExternalFullObserved);
        var next = diff with
        {
            MutationId = Guid.NewGuid(),
            Observations =
            [
                new(BackupSetEvidenceSource.Msdb, BackupSetEvidenceKind.ActiveBaseline,
                    external, full.Completion, unmanaged),
                new(BackupSetEvidenceSource.Database, BackupSetEvidenceKind.ActiveBaseline,
                    rawDatabaseBase, full.Completion, unmanaged)
            ]
        };
        Assert.False((await BackupSetTestData.RegisterAsync(database, next)).Value!.HasConflict);
        var stored = await db.BackupSets.SingleAsync(x => x.Id == diff.BackupSetId);
        Assert.Equal(full.BackupSetId, stored.BaseBackupSetId);
        Assert.Equal(DifferentialBaselineConclusion.Verified, stored.Assessment.Conclusion);
        Assert.Equal(2, await db.BackupSets.CountAsync(x => x.DatabaseId == full.DatabaseId));
        Assert.False(await db.BackupSets.AnyAsync(x => x.Metadata.BackupSetGuid.Value == external.BackupSetGuid.Value));
        var active = await db.BackupSetEvidence.SingleAsync(x => x.MutationId == next.MutationId && x.EntryNumber == 3);
        Assert.Equal(unmanaged, active.Assessment);
        Assert.Equal(rawDatabaseBase, active.Metadata);
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch,
            (await BackupSetTestData.RegisterAsync(database, diff with { BaseBackupSetId = null })).Code);
        await BackupSetTestData.RegisterAsync(database, full with
        {
            MutationId = Guid.NewGuid(),
            Metadata = full.Metadata with { CheckpointLsn = external.CheckpointLsn }
        });
        var refused = await BackupSetTestData.RegisterAsync(database, diff with { MutationId = Guid.NewGuid() });
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch, refused.Code);
        var sourceConflict = diff with
        {
            MutationId = Guid.NewGuid(),
            BaseBackupSetId = null,
            Assessment = new(BackupMetadataState.Known, DifferentialBaselineConclusion.Mismatch,
                DifferentialBaselineReason.SourceConflict)
        };
        var recorded = await BackupSetTestData.RegisterAsync(database, sourceConflict);
        Assert.True(recorded.Value!.HasConflict);
        Assert.True((await BackupSetTestData.RegisterAsync(database, sourceConflict)).Value!.HasConflict);
        var protectedDependency = await db.BackupSets.AsNoTracking().SingleAsync(x => x.Id == diff.BackupSetId);
        Assert.Equal(full.BackupSetId, protectedDependency.BaseBackupSetId);
        Assert.Equal(DifferentialBaselineConclusion.Mismatch, protectedDependency.Assessment.Conclusion);
    }

    [Fact]
    public async Task FullNotApplicableAndUnknownCompletionArePersistedSeparately()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var command = BackupSetTestData.Command(work) with
        {
            Completion = new(null, BackupCompletionTimeSource.Unknown, BackupCompletionTimeReason.SqlFinishMissing),
            Metadata = BackupSetTestData.Full() with { IsSnapshot = default }
        };
        await BackupSetTestData.RegisterAsync(database, command);
        await using var db = database.CreateContext();
        var set = await db.BackupSets.SingleAsync(x => x.Id == command.BackupSetId);
        Assert.True(set.SqlSuccessObserved);
        Assert.Null(set.Completion.CompletedAtUtc);
        Assert.Equal(BackupCompletionTimeSource.Unknown, set.Completion.Source);
        Assert.Null(set.Metadata.DifferentialBaseLsn.Value);
        Assert.Equal(BackupMetadataState.NotApplicable, set.Metadata.DifferentialBaseLsn.State);
        Assert.Null(set.Metadata.DifferentialBaseGuid.Value);
        Assert.Equal(BackupMetadataState.NotApplicable, set.Metadata.DifferentialBaseGuid.State);
        Assert.Null(set.Metadata.IsSnapshot.Value);
    }

    [Fact]
    public async Task ConcurrentReplayCreatesOneSetAndOneReconciliationAndRejectsChangedPayload()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var command = BackupSetTestData.Command(work);
        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => BackupSetTestData.RegisterAsync(database, command)));
        Assert.Single(results, x => x.Code == BackupTaskStoreResultCode.Succeeded);
        Assert.Equal(3, results.Count(x => x.Code == BackupTaskStoreResultCode.AlreadyApplied));
        Assert.All(results, x => Assert.Equal(command.BackupSetId, x.Value!.BackupSetId));
        var changed = await BackupSetTestData.RegisterAsync(database,
            command with { Metadata = command.Metadata with { FirstLsn = BackupMetadata.Known(new BackupLsn(1)) } });
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch, changed.Code);
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.BackupSets.CountAsync(x => x.AttemptId == work.Attempt.Id));
        Assert.Equal(2, await db.BackupSetEvidence.CountAsync(x => x.AttemptId == work.Attempt.Id));
        Assert.Equal(1, (await db.BackupSets.SingleAsync(x => x.AttemptId == work.Attempt.Id)).ReconciliationCount);
    }

    [Fact]
    public async Task ConcurrentConflictsAppendEvidenceAndNeverOverwriteConfirmedGuidOrLsn()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var command = BackupSetTestData.Command(work);
        await BackupSetTestData.RegisterAsync(database, command);
        var reads = new[]
        {
            command with { MutationId = Guid.NewGuid(), Metadata = command.Metadata with
                { BackupSetGuid = BackupMetadata.Known(Guid.NewGuid()) } },
            command with { MutationId = Guid.NewGuid(), Metadata = command.Metadata with
                { CheckpointLsn = BackupMetadata.Known(new BackupLsn(1234567890123456789012346m)) } }
        };
        var results = await Task.WhenAll(reads.Select(x => BackupSetTestData.RegisterAsync(database, x)));
        Assert.All(results, x => Assert.True(x.Value!.HasConflict));
        await Task.WhenAll(reads.Select(x => BackupSetTestData.RegisterAsync(database, x)));
        await using var db = database.CreateContext();
        var set = await db.BackupSets.SingleAsync(x => x.Id == command.BackupSetId);
        Assert.Equal(command.Metadata, set.Metadata);
        Assert.True(set.HasMetadataConflict);
        Assert.True(set.SqlSuccessObserved);
        Assert.Equal(DifferentialBaselineConclusion.Mismatch, set.Assessment.Conclusion);
        Assert.Equal(DifferentialBaselineReason.SourceConflict, set.Assessment.ReasonCode);
        Assert.Equal(3, set.ReconciliationCount);
        var evidence = await db.BackupSetEvidence.Where(x => x.AttemptId == work.Attempt.Id).ToArrayAsync();
        Assert.Equal(6, evidence.Length);
        Assert.Equal(3, evidence.Select(x => x.ReconciliationNumber).Distinct().Count());
        Assert.Contains(evidence, x => x.Metadata.BackupSetGuid == reads[0].Metadata.BackupSetGuid);
        Assert.Contains(evidence, x => x.Metadata.CheckpointLsn == reads[1].Metadata.CheckpointLsn);
    }

    [Fact]
    public async Task UnknownFactsCanBeFilledAndSqlSuccessSurvivesFailedReconciliation()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var unknown = BackupSetTestData.Command(work, new()) with
        {
            Completion = new(null, BackupCompletionTimeSource.Unknown, BackupCompletionTimeReason.ServerOffsetUnknown),
            Assessment = new(BackupMetadataState.Known, DifferentialBaselineConclusion.Unknown,
                DifferentialBaselineReason.PermissionDenied)
        };
        await BackupSetTestData.RegisterAsync(database, unknown);
        var full = BackupSetTestData.Command(work) with { BackupSetId = unknown.BackupSetId };
        await BackupSetTestData.RegisterAsync(database, full);
        var missing = unknown with { MutationId = Guid.NewGuid(), SqlSuccessObserved = false };
        await BackupSetTestData.RegisterAsync(database, missing);
        await using var db = database.CreateContext();
        var set = await db.BackupSets.SingleAsync(x => x.Id == full.BackupSetId);
        Assert.Equal(full.Metadata, set.Metadata);
        Assert.True(set.SqlSuccessObserved);
        Assert.Equal(full.Completion, set.Completion);
        Assert.Equal(DifferentialBaselineConclusion.Unknown, set.Assessment.Conclusion);
        Assert.Equal(DifferentialBaselineReason.PermissionDenied, set.Assessment.ReasonCode);
        Assert.False(set.HasMetadataConflict);
    }

    [Fact]
    public async Task AnotherAttemptsGuidBecomesConflictEvidenceRatherThanASecondSet()
    {
        var firstWork = await BackupSetTestData.WorkAsync(database);
        var first = BackupSetTestData.Command(firstWork);
        await BackupSetTestData.RegisterAsync(database, first);
        var secondWork = await BackupSetTestData.WorkAsync(database);
        var second = BackupSetTestData.Command(secondWork, first.Metadata);
        var result = await BackupSetTestData.RegisterAsync(database, second);
        Assert.True(result.Value!.HasConflict);
        Assert.Null(result.Value.BackupSetId);
        await using var db = database.CreateContext();
        Assert.False(await db.BackupSets.AnyAsync(x => x.AttemptId == secondWork.Attempt.Id));
        Assert.Equal(2, await db.BackupSetEvidence.CountAsync(x => x.AttemptId == secondWork.Attempt.Id));
    }

    [Fact]
    public async Task LostOrStaleLeaseCannotRegisterNewFactsButCommittedReplayRemainsReadable()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var command = BackupSetTestData.Command(work);
        await BackupSetTestData.RegisterAsync(database, command);
        await using (var db = database.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [BackupTasks] SET [LeaseToken]={Guid.NewGuid()} WHERE [Id]={work.Task.TaskId}");
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied,
            (await BackupSetTestData.RegisterAsync(database, command)).Code);
        Assert.Equal(BackupTaskStoreResultCode.LeaseLost,
            (await BackupSetTestData.RegisterAsync(database, command with { MutationId = Guid.NewGuid() })).Code);
    }

    [Fact]
    public async Task NewCopiesRequireTheCorrectSetAndAttemptPathWhileLegacyCopiesRemainNull()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var command = BackupSetTestData.Command(work);
        await BackupSetTestData.RegisterAsync(database, command);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<BackupSetRegistrationService>();
        var noSet = Copy(work, work.Attempt.WorkerSourceFilePath);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RegisterCopyAsync(work.Lease, noSet));
        var badPath = Copy(work, @"\\synthetic-host\synthetic-share\wrong.bak");
        badPath.AssociateBackupSet(command.BackupSetId);
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch,
            (await service.RegisterCopyAsync(work.Lease, badPath)).Code);
        var copy = Copy(work, work.Attempt.WorkerSourceFilePath);
        copy.AssociateBackupSet(command.BackupSetId);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, (await service.RegisterCopyAsync(work.Lease, copy)).Code);
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, (await service.RegisterCopyAsync(work.Lease, copy)).Code);
        await using var db = database.CreateContext();
        Assert.Equal(command.BackupSetId, (await db.BackupFiles.SingleAsync(x => x.Id == copy.Id)).BackupSetId);
        var legacyWork = await BackupSetTestData.WorkAsync(database);
        var legacy = Copy(legacyWork, legacyWork.Attempt.WorkerSourceFilePath);
        db.BackupFiles.Add(legacy);
        await db.SaveChangesAsync();
        Assert.Null((await db.BackupFiles.SingleAsync(x => x.Id == legacy.Id)).BackupSetId);
    }

    [Fact]
    public async Task CompositeForeignKeysRejectCrossTaskAttemptAndCrossAttemptCopy()
    {
        var first = await BackupSetTestData.WorkAsync(database);
        var second = await BackupSetTestData.WorkAsync(database);
        var command = BackupSetTestData.Command(first);
        await using var db = database.CreateContext();
        var invalid = new BackupSet(Guid.NewGuid(), second.Task.TaskId, first.Attempt.Id,
            second.Snapshot.Identity.DatabaseId, new(), command.Completion,
            BackupSetAssessment.NotApplicable, null, true);
        invalid.Reconcile(new(), command.Completion, BackupSetAssessment.NotApplicable, null, true);
        db.BackupSets.Add(invalid);
        var insertion = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var exception = Assert.IsType<SqlException>(insertion.InnerException);
        Assert.Equal(547, exception.Number);
        Assert.Contains("FK_BackupSets_BackupAttempts_TaskId_AttemptId", exception.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();
        await BackupSetTestData.RegisterAsync(database, command);
        var file = Copy(second, second.Attempt.WorkerSourceFilePath);
        file.AssociateBackupSet(command.BackupSetId);
        db.BackupFiles.Add(file);
        var copyError = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(547, Assert.IsType<SqlException>(copyError.InnerException).Number);
    }

    [Fact]
    public async Task ConfirmedFactsAndAppendOnlyEvidenceAreGuardedBySaveChanges()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var command = BackupSetTestData.Command(work);
        await BackupSetTestData.RegisterAsync(database, command);
        await using (var db = database.CreateContext())
        {
            var set = await db.BackupSets.SingleAsync(x => x.Id == command.BackupSetId);
            db.Entry(set).ComplexProperty(x => x.Metadata).ComplexProperty(x => x.BackupSetGuid)
                .Property(x => x.Value).CurrentValue = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = database.CreateContext())
        {
            var evidence = await db.BackupSetEvidence.FirstAsync(x => x.MutationId == command.MutationId);
            db.Entry(evidence).ComplexProperty(x => x.Metadata).ComplexProperty(x => x.FirstLsn)
                .Property(x => x.Value).CurrentValue = new BackupLsn(1);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = database.CreateContext())
        {
            db.BackupSets.Remove(await db.BackupSets.SingleAsync(x => x.Id == command.BackupSetId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task MigrationDownReallyRunsAndRejectsDataWithoutDroppingTablesOrHistory()
    {
        var temporary = new PlatformDatabaseSqlServerFixture();
        try
        {
            await temporary.InitializeAsync();
            await using var db = temporary.CreateContext();
            var migrations = db.Database.GetMigrations().ToArray();
            var previous = migrations[^2];
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(previous);
            Assert.Equal(0, await TableCountAsync(db));
            Assert.False(await HasAssociationColumnAsync(db));
            await migrator.MigrateAsync();
            Assert.Equal(2, await TableCountAsync(db));
            Assert.True(await HasAssociationColumnAsync(db));
            var work = await BackupSetTestData.WorkAsync(temporary);
            var command = BackupSetTestData.Command(work);
            Assert.Equal(BackupTaskStoreResultCode.Succeeded, (await BackupSetTestData.RegisterAsync(temporary, command)).Code);
            var error = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(previous));
            Assert.Equal(51001, error.Number);
            Assert.Equal(2, await TableCountAsync(db));
            Assert.True(await HasAssociationColumnAsync(db));
            Assert.Equal(migrations, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Equal(command.Metadata, (await db.BackupSets.SingleAsync()).Metadata);
        }
        finally { await temporary.DisposeAsync(); }
    }

    private static BackupFile Copy(BackupExecutionWorkItem work, string path) =>
        BackupFile.CreateLocal(Guid.NewGuid(), work.Task.TaskId, work.Attempt.Id, work.Snapshot.Identity.DatabaseId,
            work.Snapshot.Identity.ServerId, work.Snapshot.WorkerSourceEndpoint.Protocol, path, 1024,
            DateTimeOffset.UtcNow, 7);

    private static Task<int> TableCountAsync(PlatformDbContext db) => db.Database.SqlQueryRaw<int>(
        "SELECT COUNT(*) AS [Value] FROM sys.tables WHERE [name] IN ('BackupSets','BackupSetEvidence')").SingleAsync();
    private static async Task<bool> HasAssociationColumnAsync(PlatformDbContext db) => await db.Database.SqlQueryRaw<int>(
        "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE [object_id]=OBJECT_ID('BackupFiles') AND [name]='BackupSetId'").SingleAsync() == 1;
}
