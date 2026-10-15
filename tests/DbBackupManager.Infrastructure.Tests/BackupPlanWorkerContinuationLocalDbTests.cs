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

    [Theory]
    [InlineData("sql-credential", BackupStorageMode.LocalOnly)]
    [InlineData("source-credential", BackupStorageMode.LocalOnly)]
    [InlineData("instance", BackupStorageMode.LocalOnly)]
    [InlineData("server", BackupStorageMode.LocalOnly)]
    [InlineData("storage-target", BackupStorageMode.LocalAndRemote)]
    public async Task WorkerSkipsUnclaimableConfigurationWithoutStarvingOtherDatabases(string disabledKind, BackupStorageMode mode)
    {
        var isolated = new PlatformDatabaseSqlServerFixture();
        try
        {
            await isolated.InitializeAsync();
            await using var disabled = await PlanExecutionTestEnvironment.CreateAsync(isolated, mode);
            await using var healthy = await PlanExecutionTestEnvironment.CreateAsync(isolated, mode);
            var oldTask = await disabled.CreateTaskAsync(BackupRunPurpose.PlanFull);
            var nextTask = await healthy.CreateTaskAsync(BackupRunPurpose.PlanFull);
            await using (var db = isolated.CreateContext())
            {
                var snapshot = await db.BackupTaskSnapshots.AsTracking().SingleAsync(x => x.TaskId == oldTask);
                switch (disabledKind)
                {
                    case "sql-credential":
                        (await db.CredentialReferences.AsTracking().SingleAsync(x => x.Id == snapshot.SqlCredentialReferenceId)).SetEnabled(false);
                        break;
                    case "source-credential":
                        (await db.CredentialReferences.AsTracking().SingleAsync(x => x.Id == snapshot.SourceCredentialReferenceId)).SetEnabled(false);
                        break;
                    case "instance":
                        (await db.DatabaseInstances.AsTracking().SingleAsync(x => x.Id == snapshot.InstanceId)).SetEnabled(false);
                        break;
                    case "server":
                        (await db.DatabaseServers.AsTracking().SingleAsync(x => x.Id == snapshot.ServerId)).SetEnabled(false);
                        break;
                    default:
                        (await db.StorageTargets.AsTracking().SingleAsync(x => x.Id == snapshot.StorageTargetId)).SetEnabled(false);
                        break;
                }

                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE [BackupTasks] SET [CreatedAtUtc] = {DateTimeOffset.UtcNow.AddDays(-1)} WHERE [Id] = {oldTask}");
            }

            var queue = new BackupPlanWorkerStore(new BackupExecutionContractSqlServerTests.Factory(isolated));
            var selected = await queue.SelectAsync(DateTimeOffset.UtcNow);
            // 不可执行的旧任务必须留在原快照里等配置恢复，但队首不能再挡住同一夹具里的健康候选。
            Assert.Equal(BackupPlanWorkerSelectionKind.Execute, selected.Kind);
            Assert.Equal(nextTask, selected.TaskId);
            Assert.Equal(BackupExecutionContractCode.Succeeded, await healthy.Runner.RunTaskOnceAsync(new(nextTask, Guid.NewGuid())));
            await healthy.AssertCompletedAsync(nextTask, BackupType.Full);
            Assert.Equal(0, disabled.Invocations);
            await using var finalDb = isolated.CreateContext();
            var skipped = await finalDb.BackupTasks.SingleAsync(x => x.Id == oldTask);
            Assert.Equal(BackupTaskStatus.Pending, skipped.Status);
            Assert.Equal(BackupTaskStage.Backup, skipped.CurrentStage);
            Assert.Null(skipped.CurrentBackupAttemptId);
            Assert.Equal(1, await finalDb.BackupTaskSnapshots.CountAsync(x => x.TaskId == oldTask));
            Assert.False(await finalDb.BackupPlanExecutionOperations.AnyAsync(x => x.TaskId == oldTask));
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task WorkerStillSelectsRemoteOnlyTaskWithEnabledConfiguration()
    {
        var isolated = new PlatformDatabaseSqlServerFixture();
        try
        {
            await isolated.InitializeAsync();
            await using var test = await PlanExecutionTestEnvironment.CreateAsync(isolated, BackupStorageMode.RemoteOnly);
            var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
            await using (var db = isolated.CreateContext())
            {
                Assert.NotNull((await db.BackupTaskSnapshots.SingleAsync(x => x.TaskId == taskId)).StorageTargetId);
            }

            // 反向对照：RemoteOnly 的远端凭据与存储目标是另一套来源，不能被新的可用性过滤误伤。
            var queue = new BackupPlanWorkerStore(new BackupExecutionContractSqlServerTests.Factory(isolated));
            var selected = await queue.SelectAsync(DateTimeOffset.UtcNow);
            Assert.Equal(BackupPlanWorkerSelectionKind.Execute, selected.Kind);
            Assert.Equal(taskId, selected.TaskId);
            Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
            await test.AssertCompletedAsync(taskId, BackupType.Full);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task WorkerWaitsWhileUnterminatedInvocationKeepsAnotherDatabaseRunning()
    {
        var isolated = new PlatformDatabaseSqlServerFixture();
        try
        {
            await isolated.InitializeAsync();
            await using var blocked = await PlanExecutionTestEnvironment.CreateAsync(isolated, BackupStorageMode.LocalOnly);
            await using var healthy = await PlanExecutionTestEnvironment.CreateAsync(isolated, BackupStorageMode.LocalOnly);
            var blockedTask = await blocked.CreateTaskAsync(BackupRunPurpose.PlanFull);
            var healthyTask = await healthy.CreateTaskAsync(BackupRunPurpose.PlanFull);
            blocked.AfterBackupResponse = () => blocked.ExpireLeaseAsync(blockedTask);
            Assert.Equal(BackupExecutionContractCode.LeaseLost, await blocked.Runner.RunTaskOnceAsync(new(blockedTask, Guid.NewGuid())));
            blocked.AfterBackupResponse = null;
            var queue = new BackupPlanWorkerStore(new BackupExecutionContractSqlServerTests.Factory(isolated));
            // 失租后状态已进入待核对：本轮先走只读核对，不得直接放行另一个库的 BACKUP。
            var first = await queue.SelectAsync(DateTimeOffset.UtcNow);
            Assert.Equal(BackupPlanWorkerSelectionKind.Reconcile, first.Kind);
            Assert.Equal(blockedTask, first.TaskId);
            await using (var db = isolated.CreateContext())
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE [BackupTasks] SET [CreatedAtUtc] = {DateTimeOffset.UtcNow.AddDays(-1)} WHERE [Id] = {healthyTask}");
            }

            // 让被阻塞的调用进入活动执行租约但保持未终止：全局阻塞优先于健康候选。
            await using (var db = isolated.CreateContext())
            {
                var live = DateTimeOffset.UtcNow.AddMinutes(5);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE [BackupTasks] SET [Status] = 'Running', [LeasePurpose] = 'Execution', [LeaseToken] = {Guid.NewGuid()}, [LeaseOwner] = N'合成租约', [LeaseAcquiredAtUtc] = {DateTimeOffset.UtcNow}, [LeaseExpiresAtUtc] = {live}, [NextReconciliationAtUtc] = NULL WHERE [Id] = {blockedTask}");
                Assert.True(await db.BackupInvocationAuthorizations.AnyAsync(x => x.TaskId == blockedTask && x.TerminalObservedAtUtc == null));
            }

            var selected = await queue.SelectAsync(DateTimeOffset.UtcNow);
            Assert.Equal(BackupPlanWorkerSelectionKind.Wait, selected.Kind);
            Assert.Null(selected.TaskId);
            Assert.Equal(0, healthy.Invocations);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }
}
