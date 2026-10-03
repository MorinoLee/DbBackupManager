using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.Tests;

public sealed class BackupFileRetentionContractTests
{
    [Fact]
    public void WorkItemsCopyRowVersionAndRejectEmptyIdentity()
    {
        byte[] source = [1, 2, 3];
        var endpoint = new BackupFileEndpointInput(
            FileTransferProtocol.Smb,
            "synthetic-host",
            null,
            "share",
            Guid.NewGuid(),
            null);
        var work = new BackupFileRetentionWorkItem(
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
            DateTimeOffset.UtcNow.AddMinutes(1),
            source,
            endpoint);
        var health = new BackupFileHealthWorkItem(
            Guid.NewGuid(),
            Guid.NewGuid(),
            @"\\SYNTHETIC-HOST\SHARE\BACKUP.BAK",
            4096,
            source,
            endpoint);

        source[0] = 9;
        work.RowVersion[1] = 9;
        health.RowVersion[1] = 9;

        Assert.Equal([1, 2, 3], work.RowVersion);
        Assert.Equal([1, 2, 3], health.RowVersion);
        Assert.Throws<ArgumentException>(() => new BackupFileRetentionWorkItem(
            Guid.Empty,
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
            DateTimeOffset.UtcNow.AddMinutes(1),
            [1],
            endpoint));
    }

    [Fact]
    public void RetentionOptionsRejectInvalidLeaseAndTimeout()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new BackupFileRetentionOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), 60).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new BackupFileRetentionOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60), 0).Validate());
        BackupFileRetentionOptions.Default.Validate();
        var endpoint = new BackupFileEndpointInput(
            FileTransferProtocol.Smb,
            "synthetic-host",
            null,
            "share",
            Guid.NewGuid(),
            null);
        Assert.Throws<ArgumentException>(() => new BackupFileRetentionWorkItem(
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
            new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.FromHours(8)),
            [1],
            endpoint));
    }
}
