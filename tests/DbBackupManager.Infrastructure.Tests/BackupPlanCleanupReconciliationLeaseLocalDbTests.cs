using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Tests;

/// <summary>
/// A01 清理核对交接的边界对照：等待协调锁期间租约换主必须拒绝，并且不得写状态、历史或通知；
/// 真正到期的边界由独立复审反例覆盖。
/// </summary>
public sealed class BackupPlanCleanupReconciliationLeaseLocalDbTests(PlatformDatabaseSqlServerFixture platform)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Fact]
    public async Task LeaseTakenOverDuringCoordinationWaitIsRejectedWithoutWritingState()
    {
        var fault = new PlanExecutionCommitFault("ApplyAsync", "ValidateCopy");
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.RemoteOnly, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        await test.ExpireLeaseAsync(taskId);
        var clock = new ControllableClock(DateTimeOffset.UtcNow);
        var store = new BackupPlanExecutionStore(new BackupExecutionContractSqlServerTests.Factory(platform), clock);
        var opened = await store.OpenAsync(taskId, Guid.NewGuid(), true, Guid.NewGuid(), "合成只读对照", clock.UtcNow,
            clock.UtcNow.AddMinutes(2));
        Assert.Equal(BackupExecutionContractCode.Succeeded, opened.Code);
        var replacementToken = Guid.NewGuid();
        await using (var db = platform.CreateContext())
        {
            // 租约已经换主：旧 Token 不能再写状态、历史或通知。
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [BackupTasks] SET [LeaseToken] = {replacementToken} WHERE [Id] = {taskId}");
        }

        var result = await store.ReleaseDeferredCleanupForExecutionAsync(opened.Value!.Work.Lease, CancellationToken.None);
        Assert.Equal(BackupExecutionContractCode.LeaseLost, result);
        await using var finalDb = platform.CreateContext();
        var task = await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId);
        Assert.Equal(BackupTaskStatus.NeedsAttention, task.Status);
        Assert.Equal(BackupTaskStage.Cleanup, task.CurrentStage);
        Assert.Equal(replacementToken, task.LeaseToken);
        Assert.False(await finalDb.BackupTaskStateChanges.AnyAsync(x => x.MutationId == opened.Value.Work.Lease.LeaseToken));
        Assert.False(await finalDb.NotificationOutbox.AnyAsync(x => x.MutationId == opened.Value.Work.Lease.LeaseToken));
    }

    private sealed class ControllableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
