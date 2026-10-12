using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.Tests;

public sealed class PlanLocationRetentionRunnerTests
{
    [Fact]
    public async Task ProtectedRefreshSkipsPhysicalDelete()
    {
        var store = new Store(BackupTaskStoreResultCode.Protected, null);
        var deletion = new Deletion(BackupFileStorageOutcome.Succeeded);
        var runner = new PlanLocationRetentionRunner(store, new Probe(exists: true), deletion, TimeProvider.System);

        Assert.False(await runner.RunOnceAsync(Guid.NewGuid()));
        Assert.Equal(0, deletion.Calls);
        Assert.Equal(0, store.Commits);
    }

    [Fact]
    public async Task SuccessfulPhysicalDeleteIsCommittedByTheDeletionPermit()
    {
        var store = new Store(BackupTaskStoreResultCode.Succeeded, Work());
        var deletion = new Deletion(BackupFileStorageOutcome.Succeeded);
        var runner = new PlanLocationRetentionRunner(store, new Probe(exists: true), deletion, TimeProvider.System);

        Assert.True(await runner.RunOnceAsync(Guid.NewGuid()));
        Assert.Equal(1, deletion.Calls);
        Assert.Equal(0, store.Commits);
    }

    private static BackupFileRetentionWorkItem Work() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        BackupFileLocation.Local,
        null,
        Guid.NewGuid(),
        FileTransferProtocol.Smb,
        @"\\SYNTHETIC-HOST\SHARE\BACKUP.BAK",
        4096,
        BackupFileStatus.DeletePending,
        Guid.NewGuid(),
        DateTimeOffset.UtcNow.AddMinutes(2),
        [1, 2, 3],
        new(FileTransferProtocol.Smb, "synthetic-host", null, "share", Guid.NewGuid(), null));

    private sealed class Store(BackupTaskStoreResultCode refresh, BackupFileRetentionWorkItem? work) : IPlanLocationRetentionStore
    {
        public int Commits { get; private set; }

        public Task<IReadOnlyList<PlanLocationAssessment>> AssessAsync(Guid databaseId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlanLocationAssessment>>([]);

        public Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ClaimNextAsync(
            Guid databaseId, ClaimBackupFileDeletionCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.Succeeded, work ?? Work()));

        public Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> RefreshAsync(
            Guid fileId, Guid leaseToken, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupTaskStoreResult<BackupFileRetentionWorkItem>(refresh, refresh == BackupTaskStoreResultCode.Succeeded ? work : null));

        public Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> CommitAsync(
            BackupFileRetentionWorkItem item, BackupFileRetentionCommitCommand command, CancellationToken cancellationToken = default)
        {
            Commits++;
            return Task.FromResult(new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.Succeeded, item));
        }
    }

    private sealed class Probe(bool exists) : IBackupFileStorageProbe
    {
        public Task<BackupFileStorageResult<BackupFileMetadata>> InspectAsync(
            BackupFileEndpointInput endpoint, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(BackupFileStorageResult.Succeeded(exists
                ? new BackupFileMetadata(true, true, 4096, "synthetic-id")
                : new BackupFileMetadata(false, false, null)));
    }

    private sealed class Deletion(BackupFileStorageOutcome outcome) : IBackupFileDeletionExecutor
    {
        public int Calls { get; private set; }

        public Task<BackupFileStorageResult<BackupFileMutationReceipt>> DeleteAsync(
            BackupFileDeleteRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(outcome == BackupFileStorageOutcome.Succeeded
                ? BackupFileStorageResult.Succeeded(BackupFileMutationReceipt.Instance)
                : BackupFileStorageResult.ConfirmedFailure<BackupFileMutationReceipt>(
                    BackupFileStorageFailureCode.OperationRejected, BackupFileStorageFailurePhase.Delete));
        }
    }
}
