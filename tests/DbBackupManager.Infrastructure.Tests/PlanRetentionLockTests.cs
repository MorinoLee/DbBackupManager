using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Infrastructure.Persistence;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class PlanRetentionLockTests
{
    [Fact]
    public void CoordinationWaitCoversThePlanDeleteTimeout()
    {
        Assert.True(PlanRetentionLock.LockTimeoutMilliseconds >= 90_000);
        Assert.Equal("plan.retention.busy", PlanCopyDeletionReasons.Busy);
    }
}
