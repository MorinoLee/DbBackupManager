using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.FileStorage;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupExecutionSafetyRegressionSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = database.CreateContext();
        await db.BackupFileStateChanges.ExecuteDeleteAsync();
        await db.BackupFiles.ExecuteDeleteAsync();
        await db.BackupSetEvidence.ExecuteDeleteAsync();
        await db.BackupSets.ExecuteDeleteAsync();
        await database.ClearBackupTaskDataAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyUnresolvedCallMustRemainBlockedAfterOldConfirmationOrRecovery(bool recoverSuccess)
    {
        var work = await BackupSetTestData.WorkAsync(database);
        await using (var db = database.CreateContext())
        {
            var task = await db.BackupTasks.AsTracking().SingleAsync(x => x.Id == work.Task.TaskId);
            var attempt = await db.BackupAttempts.AsTracking().SingleAsync(x => x.Id == work.Attempt.Id);
            var now = DateTimeOffset.UtcNow;
            attempt.MarkBackupRunning(now);
            await db.SaveChangesAsync();
            attempt.RecordBackupIndeterminate(now, "synthetic_unknown");
            task.RecordIndeterminateResult(BackupStorageMode.LocalOnly, work.Lease.LeaseToken, now,
                "synthetic_unknown", "合成升级前未终止调用");
            await db.SaveChangesAsync();
        }
        var next = await BackupSetTestData.WorkAsync(database, work.Snapshot.Identity.DatabaseId);
        var factory = new BackupExecutionContractSqlServerTests.Factory(database);
        var coordinator = new BackupInvocationAuthorizationStore(factory, TimeProvider.System);
        var command = await ExecutionContractTestData.AuthorizationAsync(database, next);
        Assert.Equal(BackupExecutionContractCode.DatabaseBlocked, (await coordinator.AuthorizeAsync(next.Lease, command)).Code);
        Assert.DoesNotContain(
            typeof(IBackupTaskExecutionStore).GetMethods().Select(method => method.Name),
            name => name is "AcquireReconciliationLeaseAsync" or "ConfirmNeedsAttentionAsync");
        var after = await coordinator.AuthorizeAsync(next.Lease, command);
        Assert.True(after.Code == BackupExecutionContractCode.DatabaseBlocked,
            $"Before=DatabaseBlocked; RecoverySuccess={recoverSuccess}; After={after.Code}; No original termination evidence was provided.");
        await using var verified = database.CreateContext();
        Assert.Equal(BackupTaskStatus.NeedsAttention, (await verified.BackupTasks.SingleAsync(x => x.Id == work.Task.TaskId)).Status);
        Assert.Equal(BackupInvocationStatus.Prepared, (await verified.BackupAttempts.SingleAsync(x => x.Id == next.Attempt.Id)).BackupInvocationStatus);
    }

    [Fact]
    public async Task PlanSourceDeletionStaysRefused()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var factory = new BackupExecutionContractSqlServerTests.Factory(database);
        var source = work.Snapshot.WorkerSourceEndpoint;
        var endpoint = new BackupFileEndpointInput(source.Protocol, source.Host, source.Port, source.BasePath,
            source.CredentialReferenceId, source.SftpHostKeyFingerprint);
        var guard = new BackupArtifactDeletionGuard(factory);
        var request = new BackupFileDeleteRequest(endpoint, work.Attempt.WorkerSourceFilePath, 4096, null, 30,
            new(work.Task.TaskId, work.Attempt.Id, null, BackupArtifactPathRole.RemoteOnlySource,
                work.Lease.LeaseToken, work.Lease.RowVersion));
        var decision = await guard.EvaluateAsync(request);
        Assert.False(decision.Allowed);
        Assert.Equal("plan.copy.protected", decision.ReasonCode);
        var protocol = new DeleteProtocol();
        var deleted = await new BackupFileStorageAdapter([protocol], guard).DeleteAsync(request);
        Assert.Equal(BackupFileStorageOutcome.ConfirmedFailed, deleted.Outcome);
        Assert.Equal(0, protocol.Opens);
        Assert.Equal(0, protocol.Deletes);
    }

    [Fact]
    public async Task UnresolvedPlanFactsStayWithoutLegacyRecoveryWrites()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        await using (var db = database.CreateContext())
        {
            var task = await db.BackupTasks.AsTracking().SingleAsync(x => x.Id == work.Task.TaskId);
            var attempt = await db.BackupAttempts.AsTracking().SingleAsync(x => x.Id == work.Attempt.Id);
            var now = DateTimeOffset.UtcNow;
            attempt.MarkBackupRunning(now);
            await db.SaveChangesAsync();
            attempt.RecordBackupIndeterminate(now, "synthetic_unknown");
            task.RecordIndeterminateResult(BackupStorageMode.LocalOnly, work.Lease.LeaseToken, now,
                "synthetic_unknown", "合成升级前未终止调用");
            await db.SaveChangesAsync();
        }
        await using var verified = database.CreateContext();
        Assert.Equal(BackupTaskStatus.NeedsAttention, (await verified.BackupTasks.SingleAsync(x => x.Id == work.Task.TaskId)).Status);
        Assert.Equal(BackupInvocationStatus.Indeterminate, (await verified.BackupAttempts.SingleAsync()).BackupInvocationStatus);
        Assert.Equal(0, await verified.BackupInvocationAuthorizations.CountAsync());
        Assert.Equal(0, await verified.BackupPlanExecutionOperations.CountAsync());
        Assert.Equal(0, await verified.BackupFiles.CountAsync());
    }

    private sealed class DeleteProtocol : IFileStorageProtocolSessionFactory, IFileStorageProtocolSession
    {
        private bool _exists = true;
        public int Opens, Deletes;
        public FileTransferProtocol Protocol => FileTransferProtocol.Smb;
        public ValueTask<IFileStorageProtocolSession> OpenAsync(BackupFileEndpointInput endpoint, CancellationToken token)
        { Opens++; return ValueTask.FromResult<IFileStorageProtocolSession>(this); }
        public ValueTask<BackupFileMetadata> InspectAsync(string path, CancellationToken token) =>
            ValueTask.FromResult(new BackupFileMetadata(_exists, _exists, _exists ? 4096 : null, null));
        public ValueTask DeleteAsync(string path, CancellationToken token) { Deletes++; _exists = false; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask PrepareParentDirectoryAsync(string path, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<Stream> CreateNewAsync(string path, CancellationToken token) => throw new NotSupportedException();
        public ValueTask RenameNoReplaceAsync(string source, string target, CancellationToken token) => throw new NotSupportedException();
    }
}
