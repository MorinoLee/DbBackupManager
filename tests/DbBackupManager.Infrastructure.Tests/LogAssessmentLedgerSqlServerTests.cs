using DbBackupManager.Application.BackupLogs;
using DbBackupManager.Application.BackupTasks;
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
    public async Task R32o3SameGenerationMultipleSourceObservationsKeepSeparateEvidenceRows()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        var factory = new BackupExecutionContractSqlServerTests.Factory(fixture);
        var store = new LogAssessmentCommitStore(factory, TimeProvider.System);
        var first = FullCandidate();
        var header = first with
        {
            Actual = first.Actual with { Source = LogObservationSource.BackupHeader, ObservedAtUtc = At },
            SourceRevision = Guid.NewGuid(),
        };
        var msdb = first with
        {
            Actual = first.Actual with { Source = LogObservationSource.Msdb, ObservedAtUtc = At.AddSeconds(1) },
            SourceRevision = Guid.NewGuid(),
        };
        var prepared = await PrepareCandidatesAsync(store, Guid.NewGuid(), header, msdb);
        Assert.Equal(2, prepared.Generations.Count);
        Assert.All(prepared.Generations, x => Assert.Equal(first.Actual.Generation!.GenerationId, x.GenerationId));
        Assert.Equal(LogAssessmentCommitCode.Succeeded, (await store.CommitAsync(prepared)).Code);
        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.LogGenerationFacts.CountAsync());
        Assert.Equal(2, await db.LogGenerationEvidence.CountAsync());
    }

    [Fact]
    public async Task R32o1EmptyRequestUsesPersistedReaderAndFreezesResolvedCandidates()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        var work = await BackupSetTestData.WorkAsync(fixture);
        var command = BackupSetTestData.Command(work, BackupSetTestData.Full());
        Assert.Equal(BackupTaskStoreResultCode.Succeeded,
            (await BackupSetTestData.RegisterAsync(fixture, command)).Code);
        var databaseId = work.Snapshot.Identity.DatabaseId;
        var expected = new BackupDatabaseIdentity(command.Metadata.DatabaseGuid.Value,
            command.Metadata.FamilyGuid.Value);
        var factory = new BackupExecutionContractSqlServerTests.Factory(fixture);
        var store = new LogAssessmentCommitStore(factory, TimeProvider.System);
        var service = new LogAssessmentCompositionService(new Reader(), store,
            new LogAssessmentCandidateReader(factory));
        var request = new LogAssessmentRequest(databaseId, Guid.NewGuid(), "synthetic", PlanId, VersionId,
            ConfigId, expected, LogLocationScope.Local, LogRequestIntent.Manual, At, 60,
            new("synthetic-sql", Guid.NewGuid(), true, false, null, 30), new(), LogCapabilityState.Supported,
            LogResourceAvailability.Available, new(10, 1000, 100), new(0, 0, 1000),
            new(10, 1000, 100), new(0, 0, 1000), true, null, []);

        var prepared = await service.PrepareAsync(request);
        Assert.Empty(prepared.Input.Candidates);
        Assert.Contains(prepared.ResolvedCandidates, x => x.BackupSetId == command.BackupSetId);
        Assert.Equal(LogAssessmentCommitCode.Succeeded, (await service.CommitAsync(prepared)).Code);
        var read = await store.ReadAsync(databaseId, prepared.Input.MutationId);
        Assert.Equal(LogAssessmentReadCode.Found, read.Code);
        Assert.Contains(read.Value!.ResolvedCandidates, x => x.BackupSetId == command.BackupSetId);
        Assert.Equal(prepared.CandidateSetRevision, read.Value.CandidateSetRevision);
    }

    [Fact]
    public async Task R32o2ReaderDoesNotInventCopyOrProtectionFacts()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        var work = await BackupSetTestData.WorkAsync(fixture);
        var command = BackupSetTestData.Command(work, BackupSetTestData.Full());
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, (await BackupSetTestData.RegisterAsync(fixture, command)).Code);
        var factory = new BackupExecutionContractSqlServerTests.Factory(fixture);
        await using (var db = fixture.CreateContext())
            Assert.False(await db.BackupFiles.AnyAsync(x => x.BackupSetId == command.BackupSetId));

        var reader = new LogAssessmentCandidateReader(factory);
        var snapshot = await reader.ReadAsync(new LogAssessmentRequest(
            work.Snapshot.Identity.DatabaseId, Guid.NewGuid(), "synthetic", PlanId, VersionId, ConfigId,
            new(DatabaseGuid, FamilyGuid), LogLocationScope.Local, LogRequestIntent.Manual, At, 60,
            new("synthetic-sql", Guid.NewGuid(), true, false, null, 30), new(), LogCapabilityState.Supported,
            LogResourceAvailability.Available, new(10, 1000, 100), new(0, 0, 1000),
            new(10, 1000, 100), new(0, 0, 1000), true, null, []));

        Assert.NotEmpty(snapshot.Candidates);
        Assert.All(snapshot.Candidates, candidate =>
        {
            Assert.Equal(LogLocationCopyState.Missing, candidate.CopyState);
            Assert.Null(candidate.ProtectionExpectation);
        });
        Assert.True(snapshot.EnumerationComplete);
    }

    [Fact]
    public async Task R32o5CrossDatabaseManagedReferenceIsRejected()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        var otherWork = await BackupSetTestData.WorkAsync(fixture);
        var otherCommand = BackupSetTestData.Command(otherWork, BackupSetTestData.Full());
        Assert.Equal(BackupTaskStoreResultCode.Succeeded,
            (await BackupSetTestData.RegisterAsync(fixture, otherCommand)).Code);
        var factory = new BackupExecutionContractSqlServerTests.Factory(fixture);
        var store = new LogAssessmentCommitStore(factory, TimeProvider.System);
        var prepared = await PrepareAsync(store, Guid.NewGuid());
        var cross = Rebind(prepared with
        {
            ResolvedCandidates =
            [
                prepared.ResolvedCandidates[0] with
                {
                    BackupSetId = otherCommand.BackupSetId,
                    IsManaged = true,
                },
            ],
        });

        Assert.Equal(LogAssessmentCommitCode.Conflict, (await store.CommitAsync(cross)).Code);
        await using var db = fixture.CreateContext();
        Assert.False(await db.LogAssessmentCommits.AnyAsync(x => x.Id == cross.AssessmentId));
    }

    [Fact]
    public async Task R32o3bConcurrentDifferentAssessmentsShareGenerationFact()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        var factory = new BackupExecutionContractSqlServerTests.Factory(fixture);
        var first = new LogAssessmentCommitStore(factory, TimeProvider.System);
        var second = new LogAssessmentCommitStore(factory, TimeProvider.System);
        var shared = FullCandidate();
        var a = await PrepareCandidatesAsync(first, Guid.NewGuid(), shared);
        var b = await PrepareCandidatesAsync(second, Guid.NewGuid(), shared);

        var results = await Task.WhenAll(first.CommitAsync(a), second.CommitAsync(b));

        Assert.All(results, x => Assert.Equal(LogAssessmentCommitCode.Succeeded, x.Code));
        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.LogGenerationFacts.CountAsync());
        Assert.Equal(1, await db.LogGenerationEvidence.CountAsync());
        Assert.Equal(2, await db.LogAssessmentCommits.CountAsync());
    }

    [Fact]
    public async Task E05ReferenceOutsideResolvedCandidatesIsRejected()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        var factory = new BackupExecutionContractSqlServerTests.Factory(fixture);
        var store = new LogAssessmentCommitStore(factory, TimeProvider.System);
        var prepared = await PrepareAsync(store, Guid.NewGuid());
        var other = Guid.NewGuid();
        var changed = Rebind(prepared with
        {
            Candidates =
            [
                prepared.Candidates[0] with { BackupSetId = other },
            ],
        });

        Assert.Equal(LogAssessmentCommitCode.Conflict, (await store.CommitAsync(changed)).Code);
        await using var db = fixture.CreateContext();
        Assert.False(await db.LogAssessmentCommits.AnyAsync(x => x.Id == changed.AssessmentId));
    }

    [Fact]
    public async Task W05CoverageScopeAndUsedFlagsMustMatchFixedInput()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        var factory = new BackupExecutionContractSqlServerTests.Factory(fixture);
        var store = new LogAssessmentCommitStore(factory, TimeProvider.System);
        var prepared = await PrepareAsync(store, Guid.NewGuid());
        var wrongScope = Rebind(prepared with
        {
            Coverage = prepared.Coverage with { ScopeKind = "Remote", StorageTargetId = Guid.NewGuid() },
        });
        Assert.Equal(LogAssessmentCommitCode.Conflict, (await store.CommitAsync(wrongScope)).Code);

        var usedFull = prepared.ResolvedCandidates[0].BackupSetId;
        var usedMismatch = Rebind(prepared with
        {
            Coverage = prepared.Coverage with { UsedBackupSetIds = [usedFull] },
            Candidates =
            [
                prepared.Candidates[0] with { Used = false, Rejected = false },
            ],
        });
        Assert.Equal(LogAssessmentCommitCode.Conflict, (await store.CommitAsync(usedMismatch)).Code);
        await using var db = fixture.CreateContext();
        Assert.False(await db.LogAssessmentCommits.AnyAsync(x => x.Id == prepared.AssessmentId));
    }

    [Fact]
    public async Task W01SourceCandidatesKeepTheirOwnMetadataAndTime()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        var work = await BackupSetTestData.WorkAsync(fixture);
        var header = BackupSetTestData.Full() with { IsCompressed = new BackupMetadataField<bool>() };
        var msdb = BackupSetTestData.Full() with { IsCompressed = BackupMetadata.Known(true) };
        var command = BackupSetTestData.Command(work, header) with
        {
            Observations =
            [
                new(BackupSetEvidenceSource.BackupHeader, BackupSetEvidenceKind.Backup, header,
                    BackupSetTestData.Command(work, header).Completion, BackupSetAssessment.NotApplicable, At),
                new(BackupSetEvidenceSource.Msdb, BackupSetEvidenceKind.Backup, msdb,
                    BackupSetTestData.Command(work, header).Completion, BackupSetAssessment.NotApplicable, At.AddSeconds(1)),
            ],
        };
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, (await BackupSetTestData.RegisterAsync(fixture, command)).Code);
        var factory = new BackupExecutionContractSqlServerTests.Factory(fixture);
        var reader = new LogAssessmentCandidateReader(factory);
        var snapshot = await reader.ReadAsync(new LogAssessmentRequest(
            work.Snapshot.Identity.DatabaseId, Guid.NewGuid(), "synthetic", PlanId, VersionId, ConfigId,
            new(header.DatabaseGuid.Value, header.FamilyGuid.Value), LogLocationScope.Local,
            LogRequestIntent.Manual, At, 60, new("synthetic-sql", Guid.NewGuid(), true, false, null, 30),
            new(), LogCapabilityState.Supported, LogResourceAvailability.Available,
            new(10, 1000, 100), new(0, 0, 1000), new(10, 1000, 100), new(0, 0, 1000),
            true, null, []));

        var headerCandidate = snapshot.Candidates.Single(x => x.Actual.Source == LogObservationSource.BackupHeader);
        var msdbCandidate = snapshot.Candidates.Single(x => x.Actual.Source == LogObservationSource.Msdb);
        Assert.Null(headerCandidate.Actual.UseCompression);
        Assert.True(msdbCandidate.Actual.UseCompression);
        Assert.Equal(At, headerCandidate.Actual.ObservedAtUtc);
        Assert.Equal(At.AddSeconds(1), msdbCandidate.Actual.ObservedAtUtc);
    }

    [Fact]
    public async Task W04ExistingGenerationCannotBeReusedForDifferentSourceIdentity()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        var factory = new BackupExecutionContractSqlServerTests.Factory(fixture);
        var store = new LogAssessmentCommitStore(factory, TimeProvider.System);
        var original = FullCandidate();
        var prepared = await PrepareCandidatesAsync(store, Guid.NewGuid(), original);
        Assert.Equal(LogAssessmentCommitCode.Succeeded, (await store.CommitAsync(prepared)).Code);

        var otherIdentity = new BackupDatabaseIdentity(Guid.NewGuid(), Guid.NewGuid());
        var changedCandidate = original with
        {
            Actual = original.Actual with { Database = otherIdentity },
        };
        var changed = await PrepareCandidatesAsync(store, Guid.NewGuid(), changedCandidate);
        Assert.Equal(LogAssessmentCommitCode.Conflict, (await store.CommitAsync(changed)).Code);
    }

    [Fact]
    public async Task C17ConcurrentSamePayloadOneRowAndSameGenerationOneIdentity()
    {
        await using var fixture = new PlatformDatabaseSqlServerFixture();
        await fixture.InitializeAsync();
        var factory = new BackupExecutionContractSqlServerTests.Factory(fixture);
        var first = new LogAssessmentCommitStore(factory, TimeProvider.System);
        var second = new LogAssessmentCommitStore(factory, TimeProvider.System);
        var shared = FullCandidate();
        var prepared = await PrepareCandidatesAsync(first, Guid.NewGuid(), shared);
        var other = await PrepareCandidatesAsync(second, Guid.NewGuid(), shared);

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

    private static async Task<PreparedLogAssessment> PrepareAsync(ILogAssessmentCommitStore store, Guid mutationId,
        LogAssessmentCandidate? extra = null) =>
        await PrepareCandidatesAsync(store, mutationId,
            extra is null ? [FullCandidate()] : [FullCandidate(), extra]);

    private static async Task<PreparedLogAssessment> PrepareCandidatesAsync(ILogAssessmentCommitStore store,
        Guid mutationId, params LogAssessmentCandidate[] candidates)
    {
        var request = new LogAssessmentRequest(DatabaseId, mutationId, "synthetic", PlanId, VersionId, ConfigId,
            new(DatabaseGuid, FamilyGuid), LogLocationScope.Local, LogRequestIntent.Manual, At, 60,
            new("synthetic-sql", Guid.NewGuid(), true, false, null, 30), new(), LogCapabilityState.Supported,
            LogResourceAvailability.Available, new(10, 1000, 100), new(0, 0, 1000),
            new(10, 1000, 100), new(0, 0, 1000), true, null, candidates);
        var service = new LogAssessmentCompositionService(new Reader(), store);
        return await service.PrepareAsync(request);
    }

    private static LogAssessmentCandidate FullCandidate(Guid? generationId = null) => new(Guid.NewGuid(), new()
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
        Generation = new(generationId ?? Guid.NewGuid(), LogGenerationEvidenceKind.RecoveryFork),
        ObservedAtUtc = At.AddMinutes(-1),
        Source = LogObservationSource.Platform,
        Status = LogEvidenceStatus.Complete,
    }, LogLocationScope.Local, LogLocationCopyState.Available, false, new(true, false), null, null, Guid.NewGuid());

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
