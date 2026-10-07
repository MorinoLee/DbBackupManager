using DbBackupManager.Application.BackupSets;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.Tests;

public sealed class BackupSetRegistrationServiceTests
{
    [Fact]
    public async Task NewCopyWithoutSetIsRejectedBeforePersistence()
    {
        var port = new RecordingStore();
        var service = new BackupSetRegistrationService(port);
        var lease = Lease();
        var file = File(lease.TaskId, lease.BackupAttemptId);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RegisterCopyAsync(lease, file));
        Assert.False(port.Called);
        Assert.Null(file.BackupSetId);
    }

    [Fact]
    public async Task CopyWithASetFromAnotherAttemptIsRejectedBeforePersistence()
    {
        var port = new RecordingStore();
        var service = new BackupSetRegistrationService(port);
        var lease = Lease();
        var file = File(lease.TaskId, Guid.NewGuid());
        file.AssociateBackupSet(Guid.NewGuid());
        await Assert.ThrowsAsync<ArgumentException>(() => service.RegisterCopyAsync(lease, file));
        Assert.False(port.Called);
    }

    [Fact]
    public async Task AssociatedCopyUsesTheNewPort()
    {
        var port = new RecordingStore();
        var service = new BackupSetRegistrationService(port);
        var lease = Lease();
        var file = File(lease.TaskId, lease.BackupAttemptId);
        file.AssociateBackupSet(Guid.NewGuid());
        var result = await service.RegisterCopyAsync(lease, file);
        Assert.True(port.Called);
        Assert.Same(file, result.Value);
    }

    private static LeaseHandle Lease() => new(Guid.NewGuid(), Guid.NewGuid(), BackupLeasePurpose.Execution,
        BackupTaskStage.VerifyLocal, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1), [1]);
    private static BackupFile File(Guid taskId, Guid attemptId) => BackupFile.CreateLocal(Guid.NewGuid(),
        taskId, attemptId, Guid.NewGuid(), Guid.NewGuid(), FileTransferProtocol.Smb,
        @"\\synthetic-host\synthetic-share\attempt.bak", 1024, DateTimeOffset.UtcNow, 7);

    private sealed class RecordingStore : IBackupSetRegistrationStore
    {
        public bool Called { get; private set; }
        public Task<BackupTaskStoreResult<BackupSetRegistrationResult>> RegisterAsync(
            RegisterBackupSetCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupTaskStoreResult<BackupFile>> RegisterCopyAsync(LeaseHandle lease, BackupFile file,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(new BackupTaskStoreResult<BackupFile>(BackupTaskStoreResultCode.Succeeded, file));
        }
    }
}
