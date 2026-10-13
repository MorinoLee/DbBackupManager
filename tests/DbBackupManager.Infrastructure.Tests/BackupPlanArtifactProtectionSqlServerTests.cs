using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.FileStorage;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupPlanArtifactProtectionSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ClearBackupTaskDataAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(false, "Available")]
    [InlineData(true, "Available")]
    [InlineData(false, "Pending")]
    [InlineData(true, "Pending")]
    [InlineData(false, "Expired")]
    [InlineData(true, "Expired")]
    [InlineData(false, "Failed")]
    [InlineData(true, "Failed")]
    public async Task RetentionAndExactFileDeletionNeverTouchPlanCopiesIncludingClaimReplay(bool remote, string status)
    {
        var legacy = await BackupSetTestData.WorkAsync(database);
        var plan = await ExecutionContractTestData.PlanAsync(database, legacy);
        var now = DateTimeOffset.UtcNow;
        await using var db = database.CreateContext();
        var file = BackupFile.CreateLocal(Guid.NewGuid(), plan.Task.TaskId, plan.Attempt.Id,
            plan.Snapshot.Identity.DatabaseId, plan.Snapshot.Identity.ServerId, FileTransferProtocol.Smb,
            plan.Attempt.WorkerSourceFilePath, 4096, now.AddDays(-40), 7);
        if (remote)
        {
            var target = new StorageTarget(Guid.NewGuid(), $"合成目标-{Guid.NewGuid():N}",
                new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", plan.Snapshot.WorkerSourceEndpoint.CredentialReferenceId, null));
            db.Add(target);
            file = BackupFile.CreateRemote(file.Id, file.TaskId, file.AttemptId, file.DatabaseId, target.Id,
                FileTransferProtocol.Smb, file.Path, file.LengthBytes, file.ValidatedAtUtc, 7);
        }
        var token = Guid.NewGuid();
        if (status != "Available")
            file.ClaimDeletion(token, "合成旧领取", now.AddHours(-2), status == "Pending" ? now.AddMinutes(5) : now.AddHours(-1));
        if (status == "Failed") file.RecordDeletionFailure(token, now.AddMinutes(-90), "synthetic_failure", now.AddMinutes(-60));
        var mutation = Guid.NewGuid();
        if (status is "Pending" or "Expired")
            db.Add(new BackupFileStateChange(mutation, file.Id, file.TaskId, BackupFileStatus.Available,
                BackupFileStatus.DeletePending, "file.delete_claimed", now.AddHours(-2)));
        db.Add(file);
        await db.SaveChangesAsync();
        var factory = new BackupExecutionContractSqlServerTests.Factory(database);
        var store = new BackupFileRetentionStore(factory);
        var claim = new ClaimBackupFileDeletionCommand(token, mutation, "合成删除", now, now.AddMinutes(5));
        Assert.Equal(BackupTaskStoreResultCode.Protected, (await store.ClaimFileAsync(file.Id, claim)).Code);
        Assert.Equal(BackupTaskStoreResultCode.NotFound, (await store.ClaimNextAsync(claim with { MutationId = Guid.NewGuid() })).Code);
        Assert.Equal(BackupTaskStoreResultCode.Protected, (await store.RefreshWorkItemAsync(file.Id, token, now)).Code);
        var endpoint = Endpoint(plan);
        var work = new BackupFileRetentionWorkItem(file.Id, plan.Task.TaskId, file.DatabaseId, file.Location,
            file.StorageTargetId, file.DatabaseServerId, file.Protocol, file.Path, file.LengthBytes, file.Status,
            token, now.AddMinutes(5), file.RowVersion, endpoint);
        var io = new DeleteSpy();
        var runner = new BackupFileRetentionRunner(store, io, io, TimeProvider.System, BackupFileRetentionOptions.Default);
        Assert.False(await runner.RunOnceAsync());
        await runner.ProcessClaimedAsync(work);
        Assert.Equal(BackupTaskStoreResultCode.Protected,
            (await store.CommitAsync(work, new(Guid.NewGuid(), BackupFileRetentionOutcome.Deleted, now))).Code);
        Assert.Equal(0, io.Deletes);
        Assert.Equal(status is "Pending" or "Expired" ? 1 : 0, await db.BackupFileStateChanges.CountAsync());
        Assert.Equal(status == "Available" ? BackupFileStatus.Available : status == "Failed" ? BackupFileStatus.DeleteFailed : BackupFileStatus.DeletePending,
            (await db.BackupFiles.AsNoTracking().SingleAsync()).Status);
        var protocol = new ProtocolSpy();
        var adapter = new BackupFileStorageAdapter([protocol], new BackupArtifactDeletionGuard(factory));
        var physical = await adapter.DeleteAsync(new(endpoint, file.Path, file.LengthBytes, null, 30,
            new(plan.Task.TaskId, null, file.Id, BackupArtifactPathRole.RegisteredFile, token, file.RowVersion)));
        Assert.Equal(BackupFileStorageOutcome.ConfirmedFailed, physical.Outcome);
        Assert.Equal(0, protocol.Opens);
    }

    [Theory]
    [InlineData(BackupArtifactPathRole.RemotePartial)]
    [InlineData(BackupArtifactPathRole.RemoteOnlySource)]
    public async Task PlanPartAndRemoteOnlyPhysicalDeletionStayRefused(BackupArtifactPathRole role)
    {
        var legacy = await BackupSetTestData.WorkAsync(database);
        var plan = await ExecutionContractTestData.PlanAsync(database, legacy);
        var protocol = new ProtocolSpy();
        var factory = new BackupExecutionContractSqlServerTests.Factory(database);
        var adapter = new BackupFileStorageAdapter([protocol], new BackupArtifactDeletionGuard(factory));
        var path = role == BackupArtifactPathRole.RemotePartial ? plan.Attempt.WorkerSourceFilePath + ".part" : plan.Attempt.WorkerSourceFilePath;
        Assert.Equal(BackupFileStorageOutcome.ConfirmedFailed,
            (await adapter.DeleteAsync(new(Endpoint(plan), path, 4096, null, 30,
                new(plan.Task.TaskId, plan.Attempt.Id, null, role, plan.Lease.LeaseToken, plan.Lease.RowVersion)))).Outcome);
        Assert.Equal(0, protocol.Opens);
    }

    [Fact]
    public async Task ContradictoryLegacyServerOwnershipCannotReachPhysicalDelete()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var other = await BackupSetTestData.WorkAsync(database);
        await using var db = database.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var file = BackupFile.CreateLocal(Guid.NewGuid(), work.Task.TaskId, work.Attempt.Id,
            work.Snapshot.Identity.DatabaseId, other.Snapshot.Identity.ServerId, FileTransferProtocol.Smb,
            work.Attempt.WorkerSourceFilePath, 4096, now.AddDays(-40), 7);
        var token = Guid.NewGuid();
        file.ClaimDeletion(token, "合成错误归属", now, now.AddMinutes(5));
        db.Add(file);
        await db.SaveChangesAsync();
        var factory = new BackupExecutionContractSqlServerTests.Factory(database);
        var guard = new BackupArtifactDeletionGuard(factory);
        var request = new BackupFileDeleteRequest(Endpoint(work), file.Path, file.LengthBytes, null, 30,
            new(work.Task.TaskId, null, file.Id, BackupArtifactPathRole.RegisteredFile, token, file.RowVersion));
        Assert.Equal("plan.copy.protected", (await guard.EvaluateAsync(request)).ReasonCode);
        var spy = new ProtocolSpy();
        Assert.Equal(BackupFileStorageOutcome.ConfirmedFailed,
            (await new BackupFileStorageAdapter([spy], guard).DeleteAsync(request)).Outcome);
        Assert.Equal(0, spy.Opens);
    }

    [Fact]
    public async Task UnknownOwnershipAndMissingPlatformGuardCannotBypassPhysicalProtection()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var spy = new ProtocolSpy();
        var guarded = new BackupFileStorageAdapter([spy],
            new BackupArtifactDeletionGuard(new BackupExecutionContractSqlServerTests.Factory(database)));
        var request = new BackupFileDeleteRequest(Endpoint(work), work.Attempt.WorkerSourceFilePath, 4096, null, 30);
        Assert.Equal(BackupFileStorageOutcome.ConfirmedFailed, (await guarded.DeleteAsync(request)).Outcome);
        var unguarded = new BackupFileStorageAdapter([spy]);
        Assert.Equal(BackupFileStorageOutcome.ConfirmedFailed,
            (await unguarded.DeleteAsync(new(request.Endpoint, request.Path, 4096, null, 30,
                new(work.Task.TaskId, work.Attempt.Id, null, BackupArtifactPathRole.RemoteOnlySource, work.Lease.LeaseToken, work.Lease.RowVersion)))).Outcome);
        Assert.Equal(0, spy.Opens);
    }

    [Fact]
    public async Task AllLegacyPlanExecutionAndRecoveryMutationEntrypointsRemainClosed()
    {
        var legacy = await BackupSetTestData.WorkAsync(database);
        var plan = await ExecutionContractTestData.PlanAsync(database, legacy);
        Assert.Equal(
            ["RequestCancellationAsync"],
            typeof(IBackupTaskExecutionStore).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal).ToArray());
        await using var db = database.CreateContext();
        Assert.Equal(BackupTaskStatus.Pending, (await db.BackupTasks.SingleAsync(x => x.Id == plan.Task.TaskId)).Status);
        Assert.Equal(0, await db.BackupInvocationAuthorizations.CountAsync());
        Assert.Equal(0, await db.BackupPlanExecutionOperations.CountAsync());
    }

    private static BackupFileEndpointInput Endpoint(BackupExecutionWorkItem work) => new(work.Snapshot.WorkerSourceEndpoint.Protocol,
        work.Snapshot.WorkerSourceEndpoint.Host, work.Snapshot.WorkerSourceEndpoint.Port, work.Snapshot.WorkerSourceEndpoint.BasePath,
        work.Snapshot.WorkerSourceEndpoint.CredentialReferenceId, work.Snapshot.WorkerSourceEndpoint.SftpHostKeyFingerprint);
    private sealed class ProtocolSpy : IFileStorageProtocolSessionFactory
    {
        public int Opens;
        public FileTransferProtocol Protocol => FileTransferProtocol.Smb;
        public ValueTask<IFileStorageProtocolSession> OpenAsync(BackupFileEndpointInput endpoint, CancellationToken token)
        { Opens++; throw new InvalidOperationException("保护请求不能打开物理协议"); }
    }
    private sealed class DeleteSpy : IBackupFileDeletionExecutor, IBackupFileStorageProbe
    {
        public int Deletes;
        public Task<BackupFileStorageResult<BackupFileMutationReceipt>> DeleteAsync(BackupFileDeleteRequest request, CancellationToken token = default)
        { Deletes++; throw new InvalidOperationException("保护请求不得删除"); }
        public Task<BackupFileStorageResult<BackupFileMetadata>> InspectAsync(BackupFileEndpointInput endpoint, string path, CancellationToken token = default) =>
            Task.FromResult(BackupFileStorageResult.Succeeded(new BackupFileMetadata(true, true, 4096, "synthetic-id")));
    }
}
