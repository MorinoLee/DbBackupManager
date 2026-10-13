using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.Tests;

public sealed class BackupTaskStoreContractTests
{
    [Fact]
    public void LeaseHandleCopiesRowVersionInBothDirections()
    {
        byte[] source = [1, 2, 3];
        var handle = new LeaseHandle(
            Guid.NewGuid(),
            Guid.NewGuid(),
            BackupLeasePurpose.Execution,
            BackupTaskStage.Backup,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1),
            source);

        source[0] = 9;
        var firstRead = handle.RowVersion;
        firstRead[1] = 9;

        Assert.Equal([1, 2, 3], handle.RowVersion);
    }

    [Fact]
    public void AttemptModelCopiesRowVersionInBothDirections()
    {
        byte[] source = [4, 5, 6];
        var model = new BackupAttemptModel(
            Guid.NewGuid(),
            1,
            BackupInvocationStatus.Prepared,
            "synthetic-local-path",
            "synthetic-source-path",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            source);

        source[0] = 9;
        var firstRead = model.RowVersion;
        firstRead[1] = 9;

        Assert.Equal([4, 5, 6], model.RowVersion);
    }
}
