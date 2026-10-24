using DbBackupManager.Application.BackupLogs;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupLogs;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class LogAssessmentLedgerSqlServerTests
{
    private static readonly Guid DatabaseId = Guid.NewGuid();
    private static readonly Guid PlanId = Guid.NewGuid();
    private static readonly Guid VersionId = Guid.NewGuid();
    private static readonly Guid ConfigId = Guid.NewGuid();
    private static readonly Guid DatabaseGuid = Guid.NewGuid();
    private static readonly Guid FamilyGuid = Guid.NewGuid();
    private static readonly Guid Fork = Guid.NewGuid();
    private static readonly Guid GenerationId = Guid.NewGuid();
    private static readonly Guid SourceRevision = Guid.NewGuid();
    private static readonly DateTimeOffset At = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task C17C19C28CommitReadReplayConflictAndAtomicRollback()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        var factory = new BackupExecutionContractSqlServerTests.Factory(fixture);
        var store = new LogAssessmentCommitStore(factory, TimeProvider.System);
        var prepared = await PrepareAsync(store, Guid.NewGuid());
        var roundTrip = LogAssessmentCanonicalizer.Deserialize(LogAssessmentCanonicalizer.Serialize(prepared));
        Assert.Equal(prepared.PayloadFingerprint, LogAssessmentCanonicalizer.PayloadFingerprint(roundTrip));
        Assert.Equal(prepared.RequestFingerprint, LogAssessmentCanonicalizer.RequestFingerprint(roundTrip.Input));

        Assert.Equal(LogAssessmentCommitCode.Succeeded, (await store.CommitAsync(prepared)).Code);
        var read = await store.ReadAsync(prepared.Input.DatabaseId, prepared.Input.MutationId);
        Assert.Equal(LogAssessmentReadCode.Found, read.Code);
        Assert.Equal(prepared.PayloadFingerprint, read.Value!.PayloadFingerprint);
        Assert.Equal(5, read.Value.Observations.Count);
        Assert.Equal(prepared.Coverage.Conclusion, read.Value.Coverage.Conclusion);

        Assert.Equal(LogAssessmentCommitCode.AlreadyApplied, (await store.CommitAsync(prepared)).Code);
        var changed = Rebind(prepared with
        {
            Input = prepared.Input with { ObservedAtUtc = At.AddMinutes(1) },
        });
        Assert.Equal(LogAssessmentCommitCode.Conflict, (await store.CommitAsync(changed)).Code);

        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(1, await db.LogAssessmentCommits.CountAsync());
            Assert.Equal(1, await db.LogGenerationEvidence.CountAsync());
        }
    }

    [Fact]
    public async Task C27AssessmentResultsAreReadByStableAssessmentIdNotLatestRow()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        var factory = new BackupExecutionContractSqlServerTests.Factory(fixture);
        var store = new LogAssessmentCommitStore(factory, TimeProvider.System);
        var first = await PrepareAsync(store, Guid.NewGuid());
        var second = await PrepareAsync(store, Guid.NewGuid());
        Assert.NotEqual(first.AssessmentId, second.AssessmentId);

        Assert.Equal(LogAssessmentCommitCode.Succeeded, (await store.CommitAsync(first)).Code);
        Assert.Equal(LogAssessmentCommitCode.Succeeded, (await store.CommitAsync(second)).Code);
        var firstRead = await store.ReadAsync(first.Input.DatabaseId, first.Input.MutationId);
        var secondRead = await store.ReadAsync(second.Input.DatabaseId, second.Input.MutationId);
        Assert.Equal(first.PayloadFingerprint, firstRead.Value!.PayloadFingerprint);
        Assert.Equal(second.PayloadFingerprint, secondRead.Value!.PayloadFingerprint);
        Assert.NotEqual(firstRead.Value.PayloadFingerprint, secondRead.Value.PayloadFingerprint);
        await using var db = fixture.CreateContext();
        Assert.Equal(2, await db.LogAssessmentCommits.CountAsync());
    }

    [Fact]
    public async Task C17ConcurrentSamePayloadOneRowAndSameGenerationOneIdentity()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        var factory = new BackupExecutionContractSqlServerTests.Factory(fixture);
        var first = new LogAssessmentCommitStore(factory, TimeProvider.System);
        var second = new LogAssessmentCommitStore(factory, TimeProvider.System);
        var prepared = await PrepareAsync(first, Guid.NewGuid());
        var other = await PrepareAsync(second, Guid.NewGuid());

        var results = await Task.WhenAll(first.CommitAsync(prepared), second.CommitAsync(prepared));
        Assert.Single(results, x => x.Code == LogAssessmentCommitCode.Succeeded);
        Assert.Single(results, x => x.Code == LogAssessmentCommitCode.AlreadyApplied);
        Assert.Equal(LogAssessmentCommitCode.Succeeded, (await first.CommitAsync(other)).Code);

        await using var db = fixture.CreateContext();
        Assert.Equal(2, await db.LogAssessmentCommits.CountAsync());
        Assert.Equal(1, await db.LogGenerationEvidence.CountAsync());
        Assert.Equal(2, await db.LogObservationEntries.CountAsync() / prepared.Observations.Count);
    }

    [Fact]
    public async Task C19InvalidCoverageOrDuplicateObservationRollsBackWholeCommit()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        var factory = new BackupExecutionContractSqlServerTests.Factory(fixture);
        var store = new LogAssessmentCommitStore(factory, TimeProvider.System);
        var prepared = await PrepareAsync(store, Guid.NewGuid());
        var invalidCoverage = Rebind(prepared with
        {
            Coverage = prepared.Coverage with { ScopeKind = "Remote", StorageTargetId = null },
        });
        Assert.Equal(LogAssessmentCommitCode.Conflict, (await store.CommitAsync(invalidCoverage)).Code);

        var duplicate = Rebind(prepared with
        {
            Observations = [prepared.Observations[0], prepared.Observations[0]],
        });
        Assert.Equal(LogAssessmentCommitCode.Conflict, (await store.CommitAsync(duplicate)).Code);

        await using var db = fixture.CreateContext();
        Assert.Equal(0, await db.LogAssessmentCommits.CountAsync());
        Assert.Equal(0, await db.LogObservationEntries.CountAsync());
        Assert.Equal(0, await db.LogCoverageEntries.CountAsync());
    }

    [Fact]
    public async Task C18EmptyRoundTripAndDataBlocksDown()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        await using var db = fixture.CreateContext();
        var migrations = db.Database.GetMigrations().ToArray();
        var latest = migrations[^1];
        var previous = migrations[^2];
        Assert.EndsWith("AddLogAssessmentLedger", latest, StringComparison.Ordinal);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);
        await migrator.MigrateAsync(latest);
        await migrator.MigrateAsync(previous);
        await migrator.MigrateAsync(latest);
        var factory = new BackupExecutionContractSqlServerTests.Factory(fixture);
        var store = new LogAssessmentCommitStore(factory, TimeProvider.System);
        var prepared = await PrepareAsync(store, Guid.NewGuid());
        Assert.Equal(LogAssessmentCommitCode.Succeeded, (await store.CommitAsync(prepared)).Code);

        var error = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(previous));
        Assert.Equal(51015, error.Number);
        Assert.True(await db.LogAssessmentCommits.AnyAsync(x => x.Id == prepared.AssessmentId));
        Assert.Contains(latest, await db.Database.GetAppliedMigrationsAsync());
    }

    private static async Task<PreparedLogAssessment> PrepareAsync(ILogAssessmentCommitStore store, Guid mutationId)
    {
        var candidate = new LogAssessmentCandidate(Guid.NewGuid(), new()
        {
            Type = BackupType.Full,
            IsCopyOnly = false,
            RecoveryModel = DatabaseRecoveryModel.Full,
            BeginsLogChain = false,
            HasBulkLoggedData = false,
            IsForceOffline = false,
            UseChecksum = true,
            UseCompression = false,
            IsDamaged = false,
            IsSnapshot = false,
            IsIncomplete = false,
            FirstLsn = new(10),
            LastLsn = new(100),
            DatabaseBackupLsn = new(0),
            ForkPointLsn = new(10),
            Database = new(DatabaseGuid, FamilyGuid),
            Branch = new(Fork, Fork),
            Generation = new(GenerationId, LogGenerationEvidenceKind.RecoveryFork),
            ObservedAtUtc = At.AddMinutes(-1),
            Source = LogObservationSource.Platform,
            Status = LogEvidenceStatus.Complete,
        }, LogLocationScope.Local, LogLocationCopyState.Available, true, new(true, false), null, null, SourceRevision);
        var request = new LogAssessmentRequest(DatabaseId, mutationId, "synthetic", PlanId, VersionId, ConfigId,
            new(DatabaseGuid, FamilyGuid), LogLocationScope.Local, LogRequestIntent.Manual, At, 60,
            new("synthetic-sql", Guid.NewGuid(), true, false, null, 30), new(), LogCapabilityState.Supported,
            LogResourceAvailability.Available, new(10, 1000, 100), new(0, 0, 1000),
            new(10, 1000, 100), new(0, 0, 1000), true, null, [candidate]);
        var service = new LogAssessmentCompositionService(new Reader(), store);
        return await service.PrepareAsync(request);
    }

    private static PreparedLogAssessment Rebind(PreparedLogAssessment prepared)
    {
        var requestFingerprint = LogAssessmentCanonicalizer.RequestFingerprint(prepared.Input);
        var withRequest = prepared with { RequestFingerprint = requestFingerprint };
        return withRequest with { PayloadFingerprint = LogAssessmentCanonicalizer.PayloadFingerprint(withRequest) };
    }

    private sealed class Reader : ITargetSqlLogReadinessReader
    {
        public Task<TargetSqlLogReadinessEvidence> ReadAsync(TargetSqlConnectionInput connection,
            TargetSqlLogReadinessRequest request, CancellationToken cancellationToken = default)
        {
            var database = new TargetSqlDatabaseReadinessObservation(1, "synthetic",
                TargetSqlDatabaseState.Online, TargetSqlRecoveryModel.Full, true, At, []);
            var recovery = new TargetSqlRecoveryStatusObservation(true, new(DatabaseGuid, FamilyGuid),
                Fork, Fork, TargetSqlLogLsnState.Known, new BackupLsn(50), true, At, []);
            return Task.FromResult(new TargetSqlLogReadinessEvidence(DatabaseId, "synthetic",
                new(DatabaseGuid, FamilyGuid), new TargetSqlServerInfo("17.0.4025.3", null, null, 4, 17, true),
                database, recovery, database, true, true, recovery));
        }
    }
}
