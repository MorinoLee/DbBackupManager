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
        var adapter = new BackupFileStorageAdapter([protocol], new BackupArtifactDeletionGuard(factory, TimeProvider.System));
        var physical = await adapter.DeleteAsync(new(endpoint, file.Path, file.LengthBytes, null, 30,
            new(plan.Task.TaskId, null, file.Id, BackupArtifactPathRole.RegisteredFile, token, file.RowVersion)));
        Assert.Equal(BackupFileStorageOutcome.ConfirmedFailed, physical.Outcome);
        Assert.Equal(0, protocol.Opens);
    }

    [Theory]
    [InlineData(BackupTaskStage.Transfer, BackupArtifactPathRole.RemotePartial)]
    [InlineData(BackupTaskStage.Cleanup, BackupArtifactPathRole.RemoteOnlySource)]
    public async Task PlanPartRetryAndRemoteOnlyCleanupAreBlockedAtOrchestrationAndPhysicalBoundary(
        BackupTaskStage stage, BackupArtifactPathRole role)
    {
        var legacy = await BackupSetTestData.WorkAsync(database);
        var plan = await ExecutionContractTestData.PlanAsync(database, legacy);
        var forged = plan with
        {
            Lease = new(plan.Lease.TaskId, plan.Lease.LeaseToken, BackupLeasePurpose.Execution,
            stage, plan.Attempt.Id, plan.Lease.ExpiresAtUtc, plan.Lease.RowVersion)
        };
        var spy = new DeleteSpy();
        var executor = new BackupRemoteStageExecutor(spy, spy, spy, spy, spy, spy);
        var result = await executor.ExecuteAsync(forged, CancellationToken.None);
        Assert.Equal(BackupStageOutcome.Indeterminate, result.Outcome);
        Assert.Equal("plan.copy.protected", result.ErrorCode);
        Assert.Equal(0, spy.Deletes);
        Assert.Equal(0, spy.Writes);
        var protocol = new ProtocolSpy();
        var factory = new BackupExecutionContractSqlServerTests.Factory(database);
        var adapter = new BackupFileStorageAdapter([protocol], new BackupArtifactDeletionGuard(factory, TimeProvider.System));
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
        var guard = new BackupArtifactDeletionGuard(factory, TimeProvider.System);
        var request = new BackupFileDeleteRequest(Endpoint(work), file.Path, file.LengthBytes, null, 30,
            new(work.Task.TaskId, null, file.Id, BackupArtifactPathRole.RegisteredFile, token, file.RowVersion));
        Assert.Equal("artifact.owner_unknown", (await guard.EvaluateAsync(request)).ReasonCode);
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
            new BackupArtifactDeletionGuard(new BackupExecutionContractSqlServerTests.Factory(database), TimeProvider.System));
        var request = new BackupFileDeleteRequest(Endpoint(work), work.Attempt.WorkerSourceFilePath, 4096, null, 30);
        Assert.Equal(BackupFileStorageOutcome.ConfirmedFailed, (await guarded.DeleteAsync(request)).Outcome);
        var unguarded = new BackupFileStorageAdapter([spy]);
        Assert.Equal(BackupFileStorageOutcome.ConfirmedFailed,
            (await unguarded.DeleteAsync(new(request.Endpoint, request.Path, 4096, null, 30,
                new(work.Task.TaskId, work.Attempt.Id, null, BackupArtifactPathRole.RemoteOnlySource, work.Lease.LeaseToken, work.Lease.RowVersion)))).Outcome);
        Assert.Equal(0, spy.Opens);
    }

    [Fact]
    public async Task PlanExecutionAndAllLegacyRecoveryMutationEntrypointsRemainClosed()
    {
        var legacy = await BackupSetTestData.WorkAsync(database);
        var plan = await ExecutionContractTestData.PlanAsync(database, legacy);
        var store = new BackupTaskExecutionStore(new BackupExecutionContractSqlServerTests.Factory(database));
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(BackupExecutionContractCode.StageNotOpen, await new BackupPlanTaskRunner().RunTaskOnceAsync(new(plan.Task.TaskId, Guid.NewGuid())));
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch,
            (await store.RenewLeaseAsync(plan.Lease, now, now.AddMinutes(5))).Code);
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch,
            (await store.RefreshLeaseWorkItemAsync(plan.Task.TaskId, plan.Lease.LeaseToken, plan.Lease.Purpose, now)).Code);
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch,
            (await store.MarkBackupInvocationStartedAsync(plan.Lease, plan.Attempt.RowVersion, now)).Code);
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch,
            (await store.CommitStageAsync(plan.Lease, new(Guid.NewGuid(), BackupStageOutcome.Succeeded, now))).Code);
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch,
            (await store.RetryFailedAsync(new(plan.Task.TaskId, Guid.NewGuid(), now))).Code);
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch,
            (await store.ExpireExecutionLeaseAsync(new(plan.Task.TaskId, Guid.NewGuid(), now, "synthetic", "合成过期"))).Code);
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch,
            (await store.AcquireReconciliationLeaseAsync(new(plan.Task.TaskId, Guid.NewGuid(), "合成核对", now, now.AddMinutes(5)))).Code);
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch,
            (await store.CommitReconciliationAsync(plan.Lease, new(Guid.NewGuid(), BackupReconciliationOutcome.Failed, now, ErrorCode: "synthetic", ErrorMessage: "合成失败"))).Code);
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch,
            (await store.ConfirmNeedsAttentionAsync(new(plan.Task.TaskId, Guid.NewGuid(), now, BackupReconciliationOutcome.Failed))).Code);
        Assert.DoesNotContain(plan.Task.TaskId, await store.FindExpiredExecutionTaskIdsAsync(now.AddDays(1), 100));
        Assert.DoesNotContain(plan.Task.TaskId, await store.FindReconciliationCandidateTaskIdsAsync(now.AddDays(1), 100));
        await using var db = database.CreateContext();
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
    private sealed class DeleteSpy : IBackupFileDeletionExecutor, IBackupFileStorageProbe, IBackupSourceProbe,
        IBackupDirectoryPreparer, IBackupFileTransferExecutor, IBackupExecutionGuard
    {
        public int Deletes, Writes;
        public Task<BackupFileStorageResult<BackupFileMutationReceipt>> DeleteAsync(BackupFileDeleteRequest request, CancellationToken token = default)
        { Deletes++; throw new InvalidOperationException("保护请求不得删除"); }
        public Task<BackupFileStorageResult<BackupFileMetadata>> InspectAsync(BackupFileEndpointInput endpoint, string path, CancellationToken token = default) =>
            Task.FromResult(BackupFileStorageResult.Succeeded(new BackupFileMetadata(true, true, 4096, "synthetic-id")));
        public Task<BackupFileProbeResult> InspectAsync(BackupTaskSnapshotModel snapshot, BackupAttemptModel attempt, CancellationToken token) =>
            throw new InvalidOperationException("保护请求不得访问源文件");
        public Task<BackupFileStorageResult<BackupFileMutationReceipt>> PrepareParentAsync(BackupDirectoryPreparationRequest request, CancellationToken token = default)
        { Writes++; throw new InvalidOperationException("保护请求不得准备目录"); }
        public Task<BackupFileStorageResult<BackupFileTransferReceipt>> TransferAsync(BackupFileTransferRequest request, CancellationToken token = default)
        { Writes++; throw new InvalidOperationException("保护请求不得传输"); }
        public Task<BackupFileStorageResult<BackupFileMutationReceipt>> RenameAsync(BackupFileRenameRequest request, CancellationToken token = default)
        { Writes++; throw new InvalidOperationException("保护请求不得重命名"); }
        public Task<bool> IsEnabledAsync(BackupTaskSnapshotModel snapshot, CancellationToken token) => Task.FromResult(true);
    }
}
