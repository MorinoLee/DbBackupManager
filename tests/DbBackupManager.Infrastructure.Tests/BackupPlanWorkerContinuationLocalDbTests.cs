using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Tests;

/// <summary>
/// 恢复后的计划任务必须由 Worker 继续执行原 Attempt 的后续阶段，而不是停留在 Idle。
/// 单一计划库在恢复后只会留下本场景任务，因此使用独立平台库避免其他场景的任务干扰选择。
/// </summary>
public sealed class BackupPlanWorkerContinuationLocalDbTests
{
    [Fact]
    public async Task WorkerResumesRecoveredFullAtPostSqlStage()
    {
        var isolated = new PlatformDatabaseSqlServerFixture();
        try
        {
            await isolated.InitializeAsync();
            await using var test = await PlanExecutionTestEnvironment.CreateAsync(isolated, BackupStorageMode.LocalAndRemote);
            var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
            test.AfterBackupResponse = () => test.ExpireLeaseAsync(taskId);
            Assert.Equal(BackupExecutionContractCode.LeaseLost, await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
            test.AfterBackupResponse = null;
            Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
            await using (var db = isolated.CreateContext())
            {
                var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
                Assert.Equal(BackupTaskStatus.Pending, task.Status);
                Assert.Equal(BackupTaskStage.VerifyLocal, task.CurrentStage);
                Assert.Equal(1, await db.BackupAttempts.CountAsync(x => x.TaskId == taskId));
                Assert.NotNull((await db.BackupInvocationAuthorizations.SingleAsync(x => x.TaskId == taskId)).TerminalObservedAtUtc);
            }

            Assert.Equal(1, test.Invocations);
            var queue = new BackupPlanWorkerStore(new BackupExecutionContractSqlServerTests.Factory(isolated));
            var selected = await queue.SelectAsync(DateTimeOffset.UtcNow);
            Assert.Equal(BackupPlanWorkerSelectionKind.Execute, selected.Kind);
            Assert.Equal(taskId, selected.TaskId);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task WorkerResumesRecoveredDifferentialAtPostSqlStage()
    {
        var isolated = new PlatformDatabaseSqlServerFixture();
        try
        {
            await isolated.InitializeAsync();
            await using var test = await PlanExecutionTestEnvironment.CreateAsync(isolated, BackupStorageMode.LocalAndRemote);
            var full = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
            Assert.True(BackupExecutionContractCode.Succeeded == await test.Runner.RunTaskOnceAsync(new(full, Guid.NewGuid())),
                await test.DiagnosticAsync(full));
            var differential = await test.CreateTaskAsync(BackupRunPurpose.PlanDifferential);
            test.AfterBackupResponse = () => test.ExpireLeaseAsync(differential);
            Assert.Equal(BackupExecutionContractCode.LeaseLost, await test.Runner.RunTaskOnceAsync(new(differential, Guid.NewGuid())));
            test.AfterBackupResponse = null;
            Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Reconciler.ReconcilePlanTaskOnceAsync(new(differential, Guid.NewGuid())));
            await using (var db = isolated.CreateContext())
            {
                var task = await db.BackupTasks.SingleAsync(x => x.Id == differential);
                Assert.Equal(BackupTaskStatus.Pending, task.Status);
                Assert.Equal(BackupTaskStage.VerifyLocal, task.CurrentStage);
            }

            var queue = new BackupPlanWorkerStore(new BackupExecutionContractSqlServerTests.Factory(isolated));
            var selected = await queue.SelectAsync(DateTimeOffset.UtcNow);
            Assert.Equal(BackupPlanWorkerSelectionKind.Execute, selected.Kind);
            Assert.Equal(differential, selected.TaskId);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }
}
