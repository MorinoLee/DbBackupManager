using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.BackupExecution;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupRemoteRunnerSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>, IAsyncLifetime
{
    private readonly FakeSql _sql = new();
    private readonly FakeRemoteFiles _remote = new();

    public Task InitializeAsync() => database.ClearBackupTaskDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task LocalAndRemoteSuccessRegistersLocalAndRemoteFiles()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store, BackupStorageMode.LocalAndRemote);

        Assert.True(await Runner(store, factory).RunOnceAsync(CancellationToken.None));

        await using var db = database.CreateContext();
        var task = await db.BackupTasks.AsNoTracking().SingleAsync(x => x.Id == seed.TaskId);
        var files = await db.BackupFiles.AsNoTracking().Where(x => x.TaskId == seed.TaskId).ToListAsync();
        Assert.Equal(BackupTaskStatus.Succeeded, task.Status);
        Assert.Equal(2, files.Count);
        Assert.Contains(files, file => file.Location == BackupFileLocation.Local);
        Assert.Contains(files, file => file.Location == BackupFileLocation.Remote && file.LengthBytes == 4096);
        Assert.Equal(1, _remote.Transfers);
        Assert.Equal(1, _remote.Renames);
        Assert.Equal(0, _remote.Deletes);
    }

    [Fact]
    public async Task RemoteOnlySuccessCleansSourceAndRegistersRemoteFileOnly()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store, BackupStorageMode.RemoteOnly);

        Assert.True(await Runner(store, factory).RunOnceAsync(CancellationToken.None));

        await using var db = database.CreateContext();
        var task = await db.BackupTasks.AsNoTracking().SingleAsync(x => x.Id == seed.TaskId);
        var files = await db.BackupFiles.AsNoTracking().Where(x => x.TaskId == seed.TaskId).ToListAsync();
        Assert.Equal(BackupTaskStatus.Succeeded, task.Status);
        Assert.Single(files);
        Assert.Equal(BackupFileLocation.Remote, files[0].Location);
        Assert.Equal(1, _remote.Transfers);
        Assert.Equal(1, _remote.Renames);
        Assert.Equal(1, _remote.Deletes);
        var attempt = await db.BackupAttempts.AsNoTracking().SingleAsync(x => x.TaskId == seed.TaskId);
        Assert.NotNull(attempt.LocalCleanupCompletedAtUtc);
    }

    [Fact]
    public async Task IncompletePartIsClearedBeforeTransfer()
    {
        _remote.IncompletePart = true;
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        await SeedAsync(store, BackupStorageMode.LocalAndRemote);

        Assert.True(await Runner(store, factory).RunOnceAsync(CancellationToken.None));

        await using var db = database.CreateContext();
        Assert.Equal(BackupTaskStatus.Succeeded, (await db.BackupTasks.AsNoTracking().SingleAsync()).Status);
        Assert.True(_remote.IncompletePartCleared);
        Assert.Equal(1, _remote.Deletes);
        Assert.Equal(1, _remote.Transfers);
    }

    [Fact]
    public async Task RemoteDirectoryPreparationFailureDoesNotCreatePartialOrRegisterRemoteFile()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store, BackupStorageMode.LocalAndRemote);
        _remote.DirectoryPreparationFails = true;

        Assert.True(await Runner(store, factory).RunOnceAsync(CancellationToken.None));

        var task = await store.FindTaskAsync(seed.TaskId);
        Assert.Equal(BackupTaskStatus.Failed, task!.Status);
        Assert.Equal(nameof(BackupFileStorageFailureCode.ConnectionInterrupted), task.ErrorCode);
        Assert.Equal(1, _remote.DirectoryPreparations);
        Assert.Equal(0, _remote.Transfers);
        await using var db = database.CreateContext();
        Assert.False(await db.BackupFiles.AnyAsync(
            x => x.TaskId == seed.TaskId && x.Location == BackupFileLocation.Remote));
    }

    [Fact]
    public async Task RemoteCopyConflictStaysNeedsAttentionWithoutRegisteringRemoteFile()
    {
        _remote.Conflict = true;
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store, BackupStorageMode.LocalAndRemote);

        Assert.True(await Runner(store, factory).RunOnceAsync(CancellationToken.None));

        await using var db = database.CreateContext();
        var task = await db.BackupTasks.AsNoTracking().SingleAsync(x => x.Id == seed.TaskId);
        Assert.Equal(BackupTaskStatus.NeedsAttention, task.Status);
        Assert.Equal(BackupTaskStage.Transfer, task.CurrentStage);
        Assert.False(await db.BackupFiles.AnyAsync(x => x.TaskId == seed.TaskId && x.Location == BackupFileLocation.Remote));
        Assert.Equal(0, _remote.Transfers);
        Assert.Equal(0, _remote.Deletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteProbeFaultBeforeMutationDoesNotRequireReconciliation(bool cancelled)
    {
        _remote.FaultOnProbe = true;
        _remote.ThrowCancellation = cancelled;
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store, BackupStorageMode.LocalAndRemote);

        if (cancelled)
        {
            _remote.BeforeProbeFault = async () =>
                Assert.True((await store.RequestCancellationAsync(new(
                    seed.TaskId, Guid.NewGuid(), DateTimeOffset.UtcNow,
                    seed.Actor.AdminUserId, seed.Actor.SecurityStamp))).IsSucceeded);
        }

        Assert.True(await Runner(store, factory).RunOnceAsync(CancellationToken.None));

        var task = await store.FindTaskAsync(seed.TaskId);
        Assert.Equal(cancelled ? BackupTaskStatus.Cancelled : BackupTaskStatus.Failed, task!.Status);
        Assert.Equal(BackupTaskStage.Transfer, task.CurrentStage);
        Assert.Equal(0, _remote.Transfers);
        Assert.Equal(0, _remote.Renames);
        Assert.Equal(0, _remote.Deletes);
        await using var db = database.CreateContext();
        Assert.False(await db.BackupFiles.AnyAsync(
            x => x.TaskId == seed.TaskId && x.Location == BackupFileLocation.Remote));
    }

    [Theory]
    [InlineData(BackupTaskStage.Transfer, false)]
    [InlineData(BackupTaskStage.Transfer, true)]
    [InlineData(BackupTaskStage.ValidateCopy, false)]
    [InlineData(BackupTaskStage.ValidateCopy, true)]
    [InlineData(BackupTaskStage.Cleanup, false)]
    [InlineData(BackupTaskStage.Cleanup, true)]
    public async Task RemoteMutationFaultPreservesUncertainStageAndDoesNotRepeatWrites(
        BackupTaskStage stage,
        bool cancelled)
    {
        _remote.FaultAfterMutationStage = stage;
        _remote.ThrowCancellation = cancelled;
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store, BackupStorageMode.RemoteOnly);
        var runner = Runner(store, factory);

        Assert.True(await runner.RunOnceAsync(CancellationToken.None));
        Assert.False(await runner.RunOnceAsync(CancellationToken.None));

        var task = await store.FindTaskAsync(seed.TaskId);
        Assert.Equal(BackupTaskStatus.NeedsAttention, task!.Status);
        Assert.Equal(stage, task.CurrentStage);
        Assert.Equal(cancelled ? "execution_cancelled" : "stage_adapter_failed", task.ErrorCode);
        Assert.Equal(1, _remote.Transfers);
        Assert.Equal(stage == BackupTaskStage.Transfer ? 0 : 1, _remote.Renames);
        Assert.Equal(stage == BackupTaskStage.Cleanup ? 1 : 0, _remote.Deletes);
        await using var db = database.CreateContext();
        var attempt = await db.BackupAttempts.AsNoTracking().SingleAsync(x => x.TaskId == seed.TaskId);
        Assert.Null(attempt.LocalCleanupCompletedAtUtc);
        Assert.Equal(stage == BackupTaskStage.Cleanup,
            await db.BackupFiles.AnyAsync(x => x.TaskId == seed.TaskId && x.Location == BackupFileLocation.Remote));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompletePartDeletionFaultRequiresReconciliationBeforeTransfer(bool cancelled)
    {
        _remote.IncompletePart = true;
        _remote.FaultAfterMutationStage = BackupTaskStage.Cleanup;
        _remote.ThrowCancellation = cancelled;
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store, BackupStorageMode.LocalAndRemote);

        Assert.True(await Runner(store, factory).RunOnceAsync(CancellationToken.None));

        var task = await store.FindTaskAsync(seed.TaskId);
        Assert.Equal(BackupTaskStatus.NeedsAttention, task!.Status);
        Assert.Equal(BackupTaskStage.Transfer, task.CurrentStage);
        Assert.Equal(cancelled ? "execution_cancelled" : "stage_adapter_failed", task.ErrorCode);
        Assert.True(_remote.IncompletePartCleared);
        Assert.Equal(1, _remote.Deletes);
        Assert.Equal(0, _remote.Transfers);
        Assert.Equal(0, _remote.Renames);
        await using var db = database.CreateContext();
        Assert.False(await db.BackupFiles.AnyAsync(
            x => x.TaskId == seed.TaskId && x.Location == BackupFileLocation.Remote));
    }

    [Fact]
    public async Task ReadOnlyRecoveryCompletesTransferWhenPartialIsStable()
    {
        _remote.TransferOutcome = BackupFileStorageOutcome.Indeterminate;
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var seed = await SeedAsync(store, BackupStorageMode.LocalAndRemote);
        Assert.True(await Runner(store, factory).RunOnceAsync(CancellationToken.None));

        await using (var db = database.CreateContext())
        {
            var pending = await db.BackupTasks.AsNoTracking().SingleAsync(x => x.Id == seed.TaskId);
            Assert.Equal(BackupTaskStatus.NeedsAttention, pending.Status);
            Assert.Equal(BackupTaskStage.Transfer, pending.CurrentStage);
        }

        _remote.TransferOutcome = BackupFileStorageOutcome.Succeeded;
        var recovery = Recovery(store);
        Assert.True(await recovery.ReconcileTaskOnceAsync(seed.TaskId));
        Assert.True(await Runner(store, factory).RunOnceAsync(CancellationToken.None));

        await using var done = database.CreateContext();
        var task = await done.BackupTasks.AsNoTracking().SingleAsync(x => x.Id == seed.TaskId);
        Assert.Equal(BackupTaskStatus.Succeeded, task.Status);
        Assert.True(await done.BackupFiles.AnyAsync(x => x.TaskId == seed.TaskId && x.Location == BackupFileLocation.Remote));
        Assert.Equal(1, _remote.Transfers);
        Assert.Equal(1, _remote.Renames);
    }

    private BackupTaskRunner Runner(
        IBackupTaskExecutionStore store,
        IDbContextFactory<PlatformDbContext> factory) =>
        new(
            store,
            _sql,
            _sql,
            _sql,
            _remote,
            _remote,
            _remote,
            _remote,
            new BackupExecutionGuard(factory),
            TimeProvider.System,
            new(TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(60)));

    private BackupTaskRecovery Recovery(IBackupTaskExecutionStore store) =>
        new(
            store,
            new SqlSmbBackupReconciliationEvidenceProbe(
                _sql,
                _sql,
                _sql,
                _remote,
                TimeProvider.System,
                new(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(1), 100)),
            TimeProvider.System,
            new(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(1), 100));

    private async Task<(Guid TaskId, Guid PolicyId, AdminSession Actor)> SeedAsync(
        BackupTaskExecutionStore store,
        BackupStorageMode storageMode)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var actor = new AdminSession(Guid.NewGuid(), $"admin-{suffix}", $"stamp-{suffix}");
        var file = new CredentialReference(
            Guid.NewGuid(),
            $"smb-{suffix}",
            CredentialKind.SmbPassword,
            "synthetic",
            "synthetic-cipher",
            "dp-smb-password-v1");
        var sql = new CredentialReference(
            Guid.NewGuid(),
            $"sql-{suffix}",
            CredentialKind.SqlPassword,
            "synthetic",
            "synthetic-cipher",
            "dp-sql-password-v1");
        var remote = new CredentialReference(
            Guid.NewGuid(),
            $"remote-{suffix}",
            CredentialKind.SmbPassword,
            "synthetic-remote",
            "synthetic-cipher",
            "dp-smb-password-v1");
        var server = new DatabaseServer(
            Guid.NewGuid(),
            $"server-{suffix}",
            @"D:\Synthetic",
            new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", file.Id, null));
        var instance = new DatabaseInstance(
            Guid.NewGuid(),
            server.Id,
            "instance",
            $"synthetic-{suffix}",
            sql.Id,
            true,
            false,
            null,
            15);
        instance.RecordConnectionSucceeded(DateTimeOffset.UtcNow, "15.0.synthetic", null, null);
        var managed = new ManagedDatabase(Guid.NewGuid(), instance.Id, $"db-{suffix}", false, true, DateTimeOffset.UtcNow);
        managed.SetManaged(true);
        var target = new StorageTarget(
            Guid.NewGuid(),
            $"target-{suffix}",
            new FileEndpointSettings(
                FileTransferProtocol.Smb,
                "synthetic-remote-host",
                null,
                "synthetic-remote-share",
                remote.Id,
                null));
        var policy = new BackupPolicy(
            Guid.NewGuid(),
            $"policy-{suffix}",
            managed.Id,
            new(
                storageMode,
                target.Id,
                BackupScheduleType.Daily,
                TimeOnly.MinValue,
                BackupWeekdays.None,
                "UTC",
                storageMode == BackupStorageMode.RemoteOnly ? null : 7,
                14,
                true,
                false,
                true,
                120,
                60,
                60),
            true,
            true);
        await using var db = database.CreateContext();
        db.AddRange(file, sql, remote, server, instance, managed, target, policy, new AdminUser(
            actor.AdminUserId,
            actor.Username,
            actor.Username.ToUpperInvariant(),
            "synthetic-hash",
            actor.SecurityStamp));
        await db.SaveChangesAsync();
        var taskId = Guid.NewGuid();
        Assert.True((await store.CreateTaskAsync(
            new(taskId, policy.Id, BackupTaskTriggerType.Manual, null, taskId, DateTimeOffset.UtcNow, actor.AdminUserId, actor.SecurityStamp))).IsSucceeded);
        return (taskId, policy.Id, actor);
    }

    private sealed class FakeSql : ITargetSqlBackupExecutor, ITargetSqlReadOnlyProbe,
        ITargetSqlBackupEvidenceProbe, IBackupSourceProbe
    {
        public int Backups;

        public async Task<TargetSqlResult<TargetSqlFullBackupCompletion>> ExecuteFullBackupAsync(
            TargetSqlConnectionInput connection,
            TargetSqlFullBackupRequest request,
            CancellationToken token = default)
        {
            Interlocked.Increment(ref Backups);
            await Task.CompletedTask;
            return TargetSqlResult.Succeeded(new TargetSqlFullBackupCompletion(true, true, false));
        }

        public Task<TargetSqlResult<TargetSqlBackupVerification>> VerifyBackupAsync(
            TargetSqlConnectionInput connection,
            TargetSqlBackupVerificationRequest request,
            CancellationToken token = default) =>
            Task.FromResult(TargetSqlResult.Succeeded(new TargetSqlBackupVerification(true)));

        public Task<BackupFileProbeResult> InspectAsync(
            BackupTaskSnapshotModel snapshot,
            BackupAttemptModel attempt,
            CancellationToken token)
        {
            SmbBackupSourceProbe.ValidatePath(snapshot, attempt);
            return Task.FromResult(new BackupFileProbeResult(true, Backups > 0, Backups > 0 ? 4096 : null));
        }

        public Task<TargetSqlResult<TargetSqlBackupIdentity>> InspectBackupIdentityAsync(
            TargetSqlConnectionInput connection,
            TargetSqlBackupIdentityRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(TargetSqlResult.Succeeded(
                new TargetSqlBackupIdentity(TargetSqlBackupIdentityStatus.CompletedMatching)));

        public Task<TargetSqlResult<TargetSqlServerInfo>> ProbeServerAsync(
            TargetSqlConnectionInput connection,
            CancellationToken token = default) => throw new NotSupportedException();

        public Task<TargetSqlResult<TargetSqlDatabaseCatalog>> DiscoverDatabasesAsync(
            TargetSqlConnectionInput connection,
            CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class FakeRemoteFiles : IBackupFileStorageProbe, IBackupDirectoryPreparer,
        IBackupFileTransferExecutor, IBackupFileDeletionExecutor
    {
        private readonly Dictionary<string, long> _files = new(StringComparer.Ordinal);

        public int Transfers;
        public int Renames;
        public int Deletes;
        public int DirectoryPreparations;
        public bool Conflict;
        public bool IncompletePart;
        public bool IncompletePartCleared;
        public bool DirectoryPreparationFails;
        public bool FaultOnProbe;
        public Func<Task>? BeforeProbeFault;
        public BackupTaskStage? FaultAfterMutationStage;
        public bool ThrowCancellation;
        public BackupFileStorageOutcome TransferOutcome = BackupFileStorageOutcome.Succeeded;

        public Task<BackupFileStorageResult<BackupFileMutationReceipt>> PrepareParentAsync(
            BackupDirectoryPreparationRequest request,
            CancellationToken cancellationToken = default)
        {
            DirectoryPreparations++;
            return Task.FromResult(DirectoryPreparationFails
                ? BackupFileStorageResult.ConfirmedFailure<BackupFileMutationReceipt>(
                    BackupFileStorageFailureCode.ConnectionInterrupted,
                    BackupFileStorageFailurePhase.DirectoryPrepare)
                : BackupFileStorageResult.Succeeded(BackupFileMutationReceipt.Instance));
        }

        public async Task<BackupFileStorageResult<BackupFileMetadata>> InspectAsync(
            BackupFileEndpointInput endpoint,
            string path,
            CancellationToken cancellationToken = default)
        {
            if (FaultOnProbe)
            {
                if (BeforeProbeFault is not null) await BeforeProbeFault();
                ThrowAdapterFault();
            }
            if (Conflict)
            {
                return BackupFileStorageResult.Succeeded(
                    path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                        ? new BackupFileMetadata(true, true, 4096)
                        : new BackupFileMetadata(true, true, 12));
            }

            if (IncompletePart && path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !_files.ContainsKey(path))
            {
                return BackupFileStorageResult.Succeeded(new BackupFileMetadata(true, true, 100));
            }

            return _files.TryGetValue(path, out var length)
                ? BackupFileStorageResult.Succeeded(new BackupFileMetadata(true, true, length))
                : BackupFileStorageResult.Succeeded(new BackupFileMetadata(false, false, null));
        }

        public Task<BackupFileStorageResult<BackupFileTransferReceipt>> TransferAsync(
            BackupFileTransferRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Transfers);
            _files[request.TargetPartialPath] = request.ExpectedLengthBytes;
            if (FaultAfterMutationStage == BackupTaskStage.Transfer) ThrowAdapterFault();
            return Task.FromResult(TransferOutcome == BackupFileStorageOutcome.Succeeded
                ? BackupFileStorageResult.Succeeded(new BackupFileTransferReceipt(request.ExpectedLengthBytes))
                : BackupFileStorageResult.Indeterminate<BackupFileTransferReceipt>(
                    BackupFileStorageFailureCode.ConnectionInterrupted,
                    BackupFileStorageFailurePhase.DataTransfer));
        }

        public Task<BackupFileStorageResult<BackupFileMutationReceipt>> RenameAsync(
            BackupFileRenameRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Renames);
            if (!_files.Remove(request.PartialPath, out var length) || length != request.ExpectedLengthBytes)
            {
                return Task.FromResult(BackupFileStorageResult.ConfirmedFailure<BackupFileMutationReceipt>(
                    BackupFileStorageFailureCode.FileNotFound,
                    BackupFileStorageFailurePhase.Rename));
            }

            _files[request.FinalPath] = length;
            if (FaultAfterMutationStage == BackupTaskStage.ValidateCopy) ThrowAdapterFault();
            return Task.FromResult(BackupFileStorageResult.Succeeded(BackupFileMutationReceipt.Instance));
        }

        public Task<BackupFileStorageResult<BackupFileMutationReceipt>> DeleteAsync(
            BackupFileDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Deletes);
            if (IncompletePart && request.Path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                IncompletePart = false;
                IncompletePartCleared = true;
            }

            _files.Remove(request.Path);
            if (FaultAfterMutationStage == BackupTaskStage.Cleanup) ThrowAdapterFault();
            return Task.FromResult(BackupFileStorageResult.Succeeded(BackupFileMutationReceipt.Instance));
        }

        private void ThrowAdapterFault()
        {
            if (ThrowCancellation) throw new OperationCanceledException("synthetic");
            throw new InvalidOperationException("synthetic");
        }
    }
}
