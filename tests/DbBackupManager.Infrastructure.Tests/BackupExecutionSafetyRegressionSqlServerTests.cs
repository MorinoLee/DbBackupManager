using System.Data.Common;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.FileStorage;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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
        Guid policyId;
        await using (var db = database.CreateContext())
        {
            var task = await db.BackupTasks.AsTracking().SingleAsync(x => x.Id == work.Task.TaskId);
            var attempt = await db.BackupAttempts.AsTracking().SingleAsync(x => x.Id == work.Attempt.Id);
            policyId = task.PolicyId!.Value;
            var now = DateTimeOffset.UtcNow;
            attempt.MarkBackupRunning(now);
            await db.SaveChangesAsync();
            attempt.RecordBackupIndeterminate(now, "synthetic_unknown");
            task.RecordIndeterminateResult(BackupStorageMode.LocalOnly, work.Lease.LeaseToken, now,
                "synthetic_unknown", "合成升级前未终止调用");
            await db.SaveChangesAsync();
        }
        var next = await BackupSetTestData.WorkAsync(database, policyId);
        var factory = new BackupExecutionContractSqlServerTests.Factory(database);
        var coordinator = new BackupInvocationAuthorizationStore(factory, TimeProvider.System);
        var command = await ExecutionContractTestData.AuthorizationAsync(database, next);
        Assert.Equal(BackupExecutionContractCode.DatabaseBlocked, (await coordinator.AuthorizeAsync(next.Lease, command)).Code);
        var store = new BackupTaskExecutionStore(factory);
        if (recoverSuccess)
        {
            var now = DateTimeOffset.UtcNow;
            var lease = await store.AcquireReconciliationLeaseAsync(new(work.Task.TaskId, Guid.NewGuid(), "合成核对", now, now.AddMinutes(5)));
            Assert.Equal(BackupTaskStoreResultCode.Succeeded, lease.Code);
            var commit = new ReconciliationCommitCommand(Guid.NewGuid(), BackupReconciliationOutcome.Succeeded, DateTimeOffset.UtcNow);
            var recovery = store.CommitReconciliationAsync(lease.Value!.Lease, commit);
            var competing = coordinator.AuthorizeAsync(next.Lease, command);
            await Task.WhenAll(recovery, competing);
            Assert.Equal(BackupTaskStoreResultCode.Succeeded, (await recovery).Code);
            Assert.Equal(BackupExecutionContractCode.DatabaseBlocked, (await competing).Code);
            Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, (await store.CommitReconciliationAsync(lease.Value.Lease, commit)).Code);
        }
        else
        {
            var confirm = new ConfirmNeedsAttentionCommand(work.Task.TaskId, Guid.NewGuid(), DateTimeOffset.UtcNow,
                BackupReconciliationOutcome.Failed, "synthetic_failed", "合成人工确认");
            var recovery = store.ConfirmNeedsAttentionAsync(confirm);
            var competing = coordinator.AuthorizeAsync(next.Lease, command);
            await Task.WhenAll(recovery, competing);
            Assert.Equal(BackupTaskStoreResultCode.Succeeded, (await recovery).Code);
            Assert.Equal(BackupExecutionContractCode.DatabaseBlocked, (await competing).Code);
            Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, (await store.ConfirmNeedsAttentionAsync(confirm)).Code);
        }
        var after = await coordinator.AuthorizeAsync(next.Lease, command);
        Assert.True(after.Code == BackupExecutionContractCode.DatabaseBlocked,
            $"Before=DatabaseBlocked; RecoverySuccess={recoverSuccess}; After={after.Code}; No original termination evidence was provided.");
        await using var verified = database.CreateContext();
        var block = Assert.Single(await verified.BackupInvocationAuthorizations.ToArrayAsync());
        Assert.Equal(work.Attempt.Id, block.AttemptId);
        Assert.Equal(BackupInvocationBindingState.Unknown, block.Binding.State);
        Assert.Null(block.TerminalObservedAtUtc);
        Assert.Equal(BackupExecutionOperationState.Reserved, Assert.Single(await verified.BackupPlanExecutionOperations.ToArrayAsync()).State);
        Assert.Equal(BackupInvocationStatus.Prepared, (await verified.BackupAttempts.SingleAsync(x => x.Id == next.Attempt.Id)).BackupInvocationStatus);
    }

    [Theory]
    [InlineData("associated")]
    [InlineData("conflicting")]
    [InlineData("plain")]
    [InlineData("unregistered")]
    public async Task CleanupChecksRegisteredSourceProtectionAndKeepsOrdinaryLegacyDeletion(string registration)
    {
        var template = await BackupSetTestData.WorkAsync(database);
        Guid policyId;
        await using (var db = database.CreateContext())
        {
            var previous = await db.BackupPolicies.AsTracking().SingleAsync(x => x.DatabaseId == template.Snapshot.Identity.DatabaseId);
            previous.SetEnabled(false);
            await db.SaveChangesAsync();
            var target = new StorageTarget(Guid.NewGuid(), $"合成目标-{Guid.NewGuid():N}",
                new(FileTransferProtocol.Smb, "synthetic-target", null, "synthetic-share",
                    template.Snapshot.WorkerSourceEndpoint.CredentialReferenceId, null));
            var policy = new BackupPolicy(Guid.NewGuid(), $"合成策略-{Guid.NewGuid():N}", template.Snapshot.Identity.DatabaseId,
                new(BackupStorageMode.RemoteOnly, target.Id, BackupScheduleType.Daily, new(2, 0), BackupWeekdays.None,
                    "UTC", null, 7, true, true, false, 120, 60, 180), isEnabled: true, nowUtc: DateTimeOffset.UtcNow);
            db.AddRange(target, policy);
            await db.SaveChangesAsync();
            policyId = policy.Id;
        }
        var work = await BackupSetTestData.WorkAsync(database, policyId);
        var factory = new BackupExecutionContractSqlServerTests.Factory(database);
        var store = new BackupTaskExecutionStore(factory);
        var grant = await store.MarkBackupInvocationStartedAsync(work.Lease, work.Attempt.RowVersion, DateTimeOffset.UtcNow);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, grant.Code);
        var lease = grant.Value!;
        foreach (var stage in new[] { BackupTaskStage.Backup, BackupTaskStage.VerifyLocal, BackupTaskStage.Transfer, BackupTaskStage.ValidateCopy })
        {
            Assert.Equal(stage, lease.Stage);
            var result = await store.CommitStageAsync(lease, new(Guid.NewGuid(), BackupStageOutcome.Succeeded,
                DateTimeOffset.UtcNow, SourceLengthBytes: stage == BackupTaskStage.VerifyLocal ? 4096 : null));
            Assert.Equal(BackupTaskStoreResultCode.Succeeded, result.Code);
            lease = result.Value!.Lease!;
        }
        Assert.Equal(BackupTaskStage.Cleanup, lease.Stage);
        var command = BackupSetTestData.Command(work) with { Lease = lease };
        var registered = await BackupSetTestData.RegisterAsync(database, command);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, registered.Code);
        Guid? fileId = null;
        if (registration != "unregistered")
        {
            var serverId = registration == "conflicting"
                ? (await BackupSetTestData.WorkAsync(database)).Snapshot.Identity.ServerId : work.Snapshot.Identity.ServerId;
            await using var db = database.CreateContext();
            var file = BackupFile.CreateLocal(Guid.NewGuid(), work.Task.TaskId, work.Attempt.Id,
                work.Snapshot.Identity.DatabaseId, serverId, FileTransferProtocol.Smb,
                work.Attempt.WorkerSourceFilePath, 4096, DateTimeOffset.UtcNow, 7);
            if (registration == "associated") file.AssociateBackupSet(command.BackupSetId);
            db.Add(file);
            await db.SaveChangesAsync();
            fileId = file.Id;
        }
        var protectedSource = registration is "associated" or "conflicting";
        if (protectedSource)
            Assert.Equal(BackupTaskStoreResultCode.Protected, (await new BackupFileRetentionStore(factory).ClaimFileAsync(fileId!.Value,
                new(Guid.NewGuid(), Guid.NewGuid(), "合成复审", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5)))).Code);
        var source = work.Snapshot.WorkerSourceEndpoint;
        var endpoint = new BackupFileEndpointInput(source.Protocol, source.Host, source.Port, source.BasePath,
            source.CredentialReferenceId, source.SftpHostKeyFingerprint);
        var guard = new BackupArtifactDeletionGuard(factory, TimeProvider.System);
        var request = new BackupFileDeleteRequest(endpoint, work.Attempt.WorkerSourceFilePath, 4096, null, 30,
            new(work.Task.TaskId, work.Attempt.Id, null, BackupArtifactPathRole.RemoteOnlySource, lease.LeaseToken, lease.RowVersion));
        var decision = await guard.EvaluateAsync(request);
        Assert.Equal(!protectedSource, decision.Allowed);
        var protocol = new DeleteProtocol();
        var deleted = await new BackupFileStorageAdapter([protocol], guard).DeleteAsync(request);
        Assert.Equal(protectedSource ? BackupFileStorageOutcome.ConfirmedFailed : BackupFileStorageOutcome.Succeeded, deleted.Outcome);
        Assert.Equal(protectedSource ? 0 : 1, protocol.Opens);
        Assert.Equal(protectedSource ? 0 : 1, protocol.Deletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyBlockAndRecoveryFactsRollBackTogetherAfterDatabaseWriteFailure(bool recoverSuccess)
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
        var failure = new FailAfterBlockWrite();
        var store = new BackupTaskExecutionStore(new BackupExecutionContractSqlServerTests.Factory(database, failure));
        if (recoverSuccess)
        {
            var now = DateTimeOffset.UtcNow;
            var acquired = await store.AcquireReconciliationLeaseAsync(new(work.Task.TaskId, Guid.NewGuid(), "合成核对", now, now.AddMinutes(5)));
            Assert.Equal(BackupTaskStoreResultCode.Succeeded, acquired.Code);
            Assert.Equal(BackupTaskStoreResultCode.ConcurrencyConflict, (await store.CommitReconciliationAsync(acquired.Value!.Lease,
                new(Guid.NewGuid(), BackupReconciliationOutcome.Succeeded, DateTimeOffset.UtcNow))).Code);
        }
        else
            Assert.Equal(BackupTaskStoreResultCode.ConcurrencyConflict, (await store.ConfirmNeedsAttentionAsync(new(work.Task.TaskId,
                Guid.NewGuid(), DateTimeOffset.UtcNow, BackupReconciliationOutcome.Failed, "synthetic_failed", "合成人工确认"))).Code);
        Assert.True(failure.WroteBlock);
        await using var verified = database.CreateContext();
        Assert.Equal(BackupTaskStatus.NeedsAttention, (await verified.BackupTasks.SingleAsync(x => x.Id == work.Task.TaskId)).Status);
        Assert.Equal(BackupInvocationStatus.Indeterminate, (await verified.BackupAttempts.SingleAsync()).BackupInvocationStatus);
        Assert.Equal(0, await verified.BackupInvocationAuthorizations.CountAsync());
        Assert.Equal(0, await verified.BackupPlanExecutionOperations.CountAsync());
        Assert.Equal(0, await verified.BackupFiles.CountAsync());
    }

    private sealed class FailAfterBlockWrite : DbCommandInterceptor
    {
        public bool WroteBlock { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO [BackupInvocationAuthorizations]", StringComparison.Ordinal))
            {
                WroteBlock = true;
                await result.DisposeAsync();
                throw new IOException("合成授权写入后保存失败");
            }
            return result;
        }
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
