using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Tests;

/// <summary>
/// 审计 A01 的验收边界：RemoteOnly 的 Cleanup 只读核对只释放租约或补齐终态，不删除文件；
/// 恢复后由执行路径按原 Attempt 继续同一条清理路径，任务最终终结，原备份事实和远端副本不被重建。
/// </summary>
public sealed class BackupPlanCleanupRecoveryLocalDbTests(PlatformDatabaseSqlServerFixture platform)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Fact]
    public async Task ReadOnlyReconciliationReleasesLeaseWithoutDeletingStagingAndExecutionCompletesCleanup()
    {
        var fault = new PlanExecutionCommitFault("ApplyAsync", "ValidateCopy");
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.RemoteOnly, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        Assert.True(fault.Fired);
        Guid attemptId;
        string localPath;
        long localLength;
        await using (var before = platform.CreateContext())
        {
            attemptId = (await before.BackupAttempts.SingleAsync(x => x.TaskId == taskId)).Id;
            var local = await before.BackupFiles.SingleAsync(x => x.TaskId == taskId && x.Location == BackupFileLocation.Local);
            localPath = local.Path;
            localLength = local.LengthBytes;
            Assert.Equal(BackupFileStatus.Available, local.Status);
            Assert.Equal(BackupTaskStage.Cleanup, (await before.BackupTasks.SingleAsync(x => x.Id == taskId)).CurrentStage);
        }

        await test.ExpireLeaseAsync(taskId);
        var deletionsBefore = test.PlanDeletions;
        var writesBefore = test.PlanWrites;
        Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
        // 核对不得删除文件，也不得改写原 Attempt 的清理事实。
        Assert.Equal(deletionsBefore, test.PlanDeletions);
        Assert.Equal(writesBefore, test.PlanWrites);
        Assert.True(File.Exists(localPath));
        await using (var db = platform.CreateContext())
        {
            var reviewed = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
            Assert.Equal(BackupTaskStatus.Pending, reviewed.Status);
            Assert.Equal(BackupTaskStage.Cleanup, reviewed.CurrentStage);
            Assert.Null(reviewed.LeaseToken);
            Assert.Equal(attemptId, reviewed.CurrentBackupAttemptId);
            Assert.Equal(1, await db.BackupAttempts.CountAsync(x => x.TaskId == taskId));
            var local = await db.BackupFiles.SingleAsync(x => x.TaskId == taskId && x.Location == BackupFileLocation.Local);
            Assert.Equal(BackupFileStatus.Available, local.Status);
            Assert.Null((await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId)).LocalCleanupCompletedAtUtc);
        }

        var queue = new BackupPlanWorkerStore(new BackupExecutionContractSqlServerTests.Factory(platform));
        var selected = await queue.SelectAsync(DateTimeOffset.UtcNow);
        Assert.Equal(BackupPlanWorkerSelectionKind.Execute, selected.Kind);
        Assert.Equal(taskId, selected.TaskId);
        var deletionsAfterExecution = test.PlanDeletions;
        Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        await using var finalDb = platform.CreateContext();
        var completed = await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId);
        Assert.Equal(BackupTaskStatus.Succeeded, completed.Status);
        Assert.Equal(attemptId, completed.CurrentBackupAttemptId);
        Assert.Null(completed.LeaseToken);
        // 只允许清理已验证可释放的本地暂存；受保护或被引用的暂存必须原样保留并记下原因。
        Assert.True(test.PlanDeletions == deletionsAfterExecution || test.PlanDeletions == deletionsAfterExecution + 1);
        var finalLocal = await finalDb.BackupFiles.SingleAsync(x => x.TaskId == taskId && x.Location == BackupFileLocation.Local);
        Assert.Equal(localLength, finalLocal.LengthBytes);
        Assert.Equal(test.PlanDeletions > deletionsAfterExecution ? BackupFileStatus.Deleted : BackupFileStatus.Available, finalLocal.Status);
        var remote = await finalDb.BackupFiles.SingleAsync(x => x.TaskId == taskId && x.Location == BackupFileLocation.Remote);
        Assert.Equal(BackupFileStatus.Available, remote.Status);
        // 收口必须留下精确原因：清理已确认或暂存按原因保留，不能只说“已完成”。
        var reasons = await finalDb.BackupTaskStateChanges.Where(x => x.TaskId == taskId).Select(x => x.ReasonCode).ToArrayAsync();
        // 收口必须留下精确原因：清理已确认、暂存按保护原因保留，或交回执行路径。
        Assert.Contains(reasons, reason => reason is "plan.staging.clean" or "plan.staging.retain"
            or "plan.staging.deferred" or "active_differential_baseline");
        Assert.Equal(1, await finalDb.BackupAttempts.CountAsync(x => x.TaskId == taskId));
        Assert.Equal(1, await finalDb.BackupSets.CountAsync(x => x.TaskId == taskId));
        Assert.Single(await finalDb.BackupPlanExecutionOperations
            .Where(x => x.TaskId == taskId && x.Kind == BackupExecutionOperationKind.ValidateCopy && x.State == BackupExecutionOperationState.Applied)
            .ToArrayAsync());
        Assert.Equal(1, test.Invocations);
        // 任务终结后不得再被选择执行。
        Assert.Equal(BackupPlanWorkerSelectionKind.Idle, (await queue.SelectAsync(DateTimeOffset.UtcNow)).Kind);
    }

    [Fact]
    public async Task LostCleanupCommitResponseKeepsCommittedCleanupWithoutRebuildingFacts()
    {
        var fault = new PlanExecutionCommitFault("CompleteRemoteCleanupAsync");
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.RemoteOnly, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        // 清理提交已经成功但响应丢失：不能把它当成失败重跑，也不能重建文件或 Attempt。
        await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        Assert.True(fault.Fired);
        Guid attemptId;
        int sets;
        int files;
        await using (var db = platform.CreateContext())
        {
            var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
            attemptId = task.CurrentBackupAttemptId!.Value;
            sets = await db.BackupSets.CountAsync(x => x.TaskId == taskId);
            files = await db.BackupFiles.CountAsync(x => x.TaskId == taskId);
            Assert.True(task.Status is BackupTaskStatus.Running or BackupTaskStatus.Succeeded,
                $"{task.Status}/{task.CurrentStage}");
        }

        if (await StatusAsync(platform, taskId) == BackupTaskStatus.Running)
        {
            await test.ExpireLeaseAsync(taskId);
        }

        await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid()));
        await using (var db = platform.CreateContext())
        {
            var reviewed = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
            Assert.Null(reviewed.LeaseToken);
            Assert.True(reviewed.Status is BackupTaskStatus.Pending or BackupTaskStatus.Succeeded,
                $"只读核对后任务必须可继续或已终结：{reviewed.Status}/{reviewed.CurrentStage}");
            Assert.Equal(attemptId, reviewed.CurrentBackupAttemptId);
        }

        if (await StatusAsync(platform, taskId) == BackupTaskStatus.Pending)
        {
            var queue = new BackupPlanWorkerStore(new BackupExecutionContractSqlServerTests.Factory(platform));
            Assert.Equal(taskId, (await queue.SelectAsync(DateTimeOffset.UtcNow)).TaskId);
            Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        }

        await test.AssertCompletedAsync(taskId, BackupType.Full);
        Assert.Equal(1, test.Invocations);
        await using var finalDb = platform.CreateContext();
        Assert.Equal(attemptId, (await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId)).CurrentBackupAttemptId);
        Assert.Equal(1, await finalDb.BackupAttempts.CountAsync(x => x.TaskId == taskId));
        Assert.Equal(sets, await finalDb.BackupSets.CountAsync(x => x.TaskId == taskId));
        Assert.Equal(files, await finalDb.BackupFiles.CountAsync(x => x.TaskId == taskId));
    }

    private static async Task<BackupTaskStatus> StatusAsync(PlatformDatabaseSqlServerFixture platform, Guid taskId)
    {
        await using var db = platform.CreateContext();
        return (await db.BackupTasks.SingleAsync(x => x.Id == taskId)).Status;
    }
}
