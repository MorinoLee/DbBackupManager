using DbBackupManager.Application.BackupSets;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.TargetSql;
using DbBackupManager.Infrastructure.Tests.TargetSql;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupMetadataReconciliationSqlServerTests(PlatformDatabaseSqlServerFixture database, ITestOutputHelper output)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Fact]
    public async Task RealAdapterRegistersFullAndDiffThenKeepsDependencyWhenExternalFullChangesActiveBaseline()
    {
        await using var sql = new LocalDbBackupMetadataProofScope(output);
        await sql.InitializeAsync();
        var factory = new TrackingFactory(database);
        var reader = new SqlClientTargetSqlBackupMetadataReader(new MetadataTestCredentialResolver(),
            new MetadataTestSessionFactory(() => { factory.AssertDisposed(); return sql.NewMetadataConnection(); }));
        var service = new BackupMetadataReconciliationService(reader, new BackupMetadataReconciliationLookup(factory), new BackupSetRegistrationStore(factory));
        var full = await sql.BackupAsync("managed", Domain.BackupPlans.BackupRunPurpose.PlanFull);
        var fullWork = await BackupSetTestData.WorkAsync(database);
        await BindFile(fullWork, full);
        var preparedFull = await service.PrepareAsync(BoundRequest(fullWork, full));
        Assert.Equal(DifferentialBaselineConclusion.Verified, preparedFull.ActiveBaseline.Conclusion);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, (await service.CommitAsync(preparedFull)).Code);
        Guid policy;
        await using (var context = database.CreateContext())
            policy = await context.BackupTasks.Where(x => x.Id == fullWork.Task.TaskId).Select(x => x.PolicyId).SingleAsync();
        await sql.ChangeAsync();
        var diff = await sql.BackupAsync("diff", Domain.BackupPlans.BackupRunPurpose.PlanDifferential);
        var diffWork = await BackupSetTestData.WorkAsync(database, policy);
        await BindFile(diffWork, diff);
        var request = BoundRequest(diffWork, diff);
        var prepared = await service.PrepareAsync(request);
        Assert.Equal(preparedFull.Command.BackupSetId, prepared.Command.BaseBackupSetId);
        Assert.Equal(DifferentialBaselineConclusion.Verified, prepared.HistoricalDependency.Conclusion);
        Assert.Equal(DifferentialBaselineConclusion.Verified, prepared.ActiveBaseline.Conclusion);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, (await service.CommitAsync(prepared)).Code);
        await sql.ChangeAsync();
        var external = await sql.BackupAsync("external", Domain.BackupPlans.BackupRunPurpose.PlanFull, external: true);
        var next = await service.PrepareAsync(request with { MutationId = Guid.NewGuid(), ObservedAtUtc = DateTimeOffset.UtcNow });
        Assert.Equal(preparedFull.Command.BackupSetId, next.Command.BaseBackupSetId);
        Assert.Equal(DifferentialBaselineConclusion.Verified, next.HistoricalDependency.Conclusion);
        Assert.Equal(DifferentialBaselineConclusion.Unmanaged, next.ActiveBaseline.Conclusion);
        Assert.Equal(DifferentialBaselineReason.ExternalFullObserved, next.ActiveBaseline.ReasonCode);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, (await service.CommitAsync(next)).Code);
        var replay = await service.PrepareAsync(request);
        Assert.Null(replay.Evidence);
        Assert.Equal(DifferentialBaselineConclusion.Verified, replay.ActiveBaseline.Conclusion); // 原观察，不伪装为刷新后的当前健康。
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, (await service.CommitAsync(replay)).Code);
        factory.AssertDisposed();
        await using var verify = database.CreateContext();
        Assert.Equal(2, await verify.BackupSets.CountAsync(x => x.DatabaseId == request.DatabaseId));
        Assert.False(await verify.BackupSets.AnyAsync(x => x.Metadata.BackupSetGuid.Value == external.Header.BackupSetGuid));
        var stored = await verify.BackupSets.SingleAsync(x => x.Id == prepared.Command.BackupSetId);
        Assert.Equal(preparedFull.Command.BackupSetId, stored.BaseBackupSetId);
        Assert.Equal(DifferentialBaselineConclusion.Verified, stored.Assessment.Conclusion);
        Assert.Equal(2, stored.ReconciliationCount);

        async Task BindFile(BackupExecutionWorkItem work, ProofBackup file)
        {
            // 旧策略夹具生成任务；仅绑定本次合成快照与临时文件，不更改生产任务模型。
            await using var context = database.CreateContext();
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [BackupTaskSnapshots] SET [DatabaseName]={sql.DatabaseName} WHERE [TaskId]={work.Task.TaskId}");
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [BackupAttempts] SET [LocalSqlFilePath]={file.Path} WHERE [Id]={work.Attempt.Id}");
        }
        BackupMetadataReconciliationRequest BoundRequest(BackupExecutionWorkItem work, ProofBackup file) =>
            Request(work, file.Adapted.Header.Metadata) with { File = new(work.Attempt.Id, sql.DatabaseName, file.Path, 60) };
    }

    [Fact]
    public async Task ReadsWithoutPlatformContextThenRegistersVerifiedDependencyAndReplaysSameMutation()
    {
        var fullWork = await BackupSetTestData.WorkAsync(database);
        var full = BackupSetTestData.Command(fullWork);
        await BackupSetTestData.RegisterAsync(database, full);
        Guid policy;
        await using (var db = database.CreateContext())
            policy = await db.BackupTasks.Where(x => x.Id == fullWork.Task.TaskId).Select(x => x.PolicyId).SingleAsync();
        var work = await BackupSetTestData.WorkAsync(database, policy);
        var metadata = full.Metadata with
        {
            Type = BackupMetadata.Known(BackupType.Differential),
            BackupSetGuid = BackupMetadata.Known(Guid.NewGuid()),
            DifferentialBaseGuid = full.Metadata.BackupSetGuid,
            DifferentialBaseLsn = full.Metadata.CheckpointLsn,
            DatabaseBackupLsn = full.Metadata.CheckpointLsn
        };
        var factory = new TrackingFactory(database);
        var reader = new CheckingReader(factory, Evidence(metadata, full.Metadata));
        var service = new BackupMetadataReconciliationService(reader, new BackupMetadataReconciliationLookup(factory), new BackupSetRegistrationStore(factory));
        var request = Request(work, metadata);
        var prepared = await service.PrepareAsync(request);
        Assert.Equal(full.BackupSetId, prepared.Command.BaseBackupSetId);
        Assert.Equal(DifferentialBaselineConclusion.Verified, prepared.HistoricalDependency.Conclusion);
        Assert.Equal(DifferentialBaselineConclusion.Verified, prepared.ActiveBaseline.Conclusion);
        factory.AssertDisposed();
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => service.CommitAsync(prepared)));
        Assert.Single(results, x => x.Code == BackupTaskStoreResultCode.Succeeded);
        Assert.Equal(2, results.Count(x => x.Code == BackupTaskStoreResultCode.AlreadyApplied));
        Assert.Equal(1, reader.Calls);
        factory.AssertDisposed();
        var recovered = await service.PrepareAsync(request with { ObservedAtUtc = request.ObservedAtUtc.AddMinutes(1) });
        Assert.Null(recovered.Evidence);
        Assert.Equal(prepared.Command.MutationId, recovered.Command.MutationId);
        Assert.Equal(prepared.Command.ObservedAtUtc, recovered.Command.ObservedAtUtc);
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, (await service.CommitAsync(recovered)).Code);
        Assert.Equal(1, reader.Calls);
        await Assert.ThrowsAsync<ArgumentException>(() => service.PrepareAsync(request with { SqlSuccessObserved = false }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PrepareAsync(request with
        { PlatformCompletedAtUtc = request.PlatformCompletedAtUtc!.Value.AddSeconds(1) }));
        await using var verify = database.CreateContext();
        var stored = await verify.BackupSets.SingleAsync(x => x.Id == prepared.Command.BackupSetId);
        Assert.Equal(full.BackupSetId, stored.BaseBackupSetId);
        Assert.Equal(1, stored.ReconciliationCount);
        Assert.True(stored.SqlSuccessObserved);
        Assert.Equal(prepared.Command.Observations.Count + 2,
            await verify.BackupSetEvidence.CountAsync(x => x.MutationId == prepared.Command.MutationId));
    }

    [Fact]
    public async Task ReadFailurePersistsSqlSuccessButLostLeaseRejectsNewRegistration()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var factory = new TrackingFactory(database);
        var reader = new CheckingReader(factory, SqlClientTargetSqlBackupMetadataReader.Failed(TargetSqlFailureCode.AuthorizationDenied));
        var service = new BackupMetadataReconciliationService(reader, new BackupMetadataReconciliationLookup(factory), new BackupSetRegistrationStore(factory));
        var request = Request(work, new());
        var prepared = await service.PrepareAsync(request);
        Assert.Equal(DifferentialBaselineConclusion.Unknown, prepared.HistoricalDependency.Conclusion);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, (await service.CommitAsync(prepared)).Code);
        await using (var db = database.CreateContext())
        {
            var stored = await db.BackupSets.SingleAsync(x => x.Id == prepared.Command.BackupSetId);
            Assert.True(stored.SqlSuccessObserved);
            Assert.Equal(DifferentialBaselineReason.PermissionDenied, stored.Assessment.ReasonCode);
            Assert.Equal(prepared.Command.Completion, stored.Completion);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [BackupTasks] SET [LeaseToken]={Guid.NewGuid()} WHERE [Id]={work.Task.TaskId}");
        }
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, (await service.CommitAsync(prepared)).Code);
        var recovered = await service.PrepareAsync(request);
        Assert.Null(recovered.Evidence);
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, (await service.CommitAsync(recovered)).Code);
        Assert.Equal(1, reader.Calls);
        var newMutation = prepared with { Command = prepared.Command with { MutationId = Guid.NewGuid() } };
        Assert.Equal(BackupTaskStoreResultCode.LeaseLost, (await service.CommitAsync(newMutation)).Code);
        factory.AssertDisposed();
    }

    [Fact]
    public async Task SourceConflictPersistsBothReadingsWithoutConfirmingContradictoryGuid()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var metadata = BackupSetTestData.Full();
        var evidence = Evidence(metadata, metadata);
        var history = metadata with { BackupSetGuid = BackupMetadata.Known(Guid.NewGuid()) };
        evidence = evidence with { History = evidence.History with { Metadata = history } };
        var factory = new TrackingFactory(database);
        var service = new BackupMetadataReconciliationService(new CheckingReader(factory, evidence),
            new BackupMetadataReconciliationLookup(factory), new BackupSetRegistrationStore(factory));
        var prepared = await service.PrepareAsync(Request(work, metadata));
        Assert.True((await service.CommitAsync(prepared)).Value!.HasConflict);
        await using var verify = database.CreateContext();
        var stored = await verify.BackupSets.SingleAsync(x => x.Id == prepared.Command.BackupSetId);
        Assert.Null(stored.Metadata.BackupSetGuid.Value);
        Assert.Equal(DifferentialBaselineConclusion.Mismatch, stored.Assessment.Conclusion);
        var observations = await verify.BackupSetEvidence.Where(x => x.MutationId == prepared.Command.MutationId).ToArrayAsync();
        Assert.Contains(observations, x => x.Source == BackupSetEvidenceSource.BackupHeader && x.Metadata == metadata);
        Assert.Contains(observations, x => x.Source == BackupSetEvidenceSource.Msdb && x.Metadata == history);
    }

    private static BackupMetadataReconciliationRequest Request(BackupExecutionWorkItem work, BackupSetMetadata metadata) => new(
        work.Lease, Guid.NewGuid(), work.Snapshot.Identity.DatabaseId, Guid.NewGuid(), DateTimeOffset.UtcNow,
        new(metadata.DatabaseGuid.Value, metadata.FamilyGuid.Value), Connection(work.Snapshot.SqlTarget),
        new(work.Attempt.Id, work.Snapshot.Identity.DatabaseName, work.Attempt.LocalSqlFilePath, 60), true, DateTimeOffset.UtcNow);
    private static TargetSqlConnectionInput Connection(BackupSqlTargetModel target) =>
        new(target.ConnectionAddress, target.SqlCredentialReferenceId, target.EncryptConnection, target.TrustServerCertificate,
            target.CertificateTrustReason, target.ConnectionTimeoutSeconds, target.AllowLegacyTls, target.LegacyTlsReason);
    private static TargetSqlBackupMetadataEvidence Evidence(BackupSetMetadata metadata, BackupSetMetadata full) => new(
        new(BackupSetEvidenceSource.BackupHeader, metadata, BaselineEvidenceStatus.Complete, []),
        new(BackupSetEvidenceSource.Msdb, metadata, BaselineEvidenceStatus.Complete, []),
        new(new(new(full.DatabaseGuid.Value, full.FamilyGuid.Value), full.RecoveryForkId.Value,
            [new(full.BackupSetGuid.Value, full.CheckpointLsn.Value)]), null, []),
        [new(BackupSetEvidenceSource.Msdb, full, BaselineEvidenceStatus.Complete, [])], null, null);
    private sealed class TrackingFactory(PlatformDatabaseSqlServerFixture database) : IDbContextFactory<PlatformDbContext>
    {
        public List<PlatformDbContext> Contexts { get; } = [];
        public PlatformDbContext CreateDbContext()
        {
            var context = database.CreateContext();
            lock (Contexts) Contexts.Add(context);
            return context;
        }
        public void AssertDisposed()
        {
            foreach (var context in Contexts)
                Assert.Throws<ObjectDisposedException>(() => context.Database.GetDbConnection());
        }
    }
    private sealed class CheckingReader(TrackingFactory factory, TargetSqlBackupMetadataEvidence evidence) : ITargetSqlBackupMetadataReader
    {
        public int Calls { get; private set; }
        public async Task<TargetSqlBackupMetadataEvidence> ReadAsync(TargetSqlConnectionInput connection,
            TargetSqlBackupMetadataRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            factory.AssertDisposed(); // 前置短查询已释放 Context；外部 I/O 不持有平台事务。
            var before = factory.Contexts.Count;
            await Task.Yield();
            Assert.Equal(before, factory.Contexts.Count);
            factory.AssertDisposed();
            return evidence;
        }
    }
}
