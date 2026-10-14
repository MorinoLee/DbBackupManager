using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Notifications;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Tests;

/// <summary>PR #16～#23 回溯审计保留的固定反例：独立随机平台库、真实 LocalDB 与现有受保护文件夹具。</summary>
public sealed class BackupPlanAuditRegressionLocalDbTests
{
    [Theory]
    [InlineData(BackupRunPurpose.PlanFull, BackupStorageMode.LocalOnly)]
    [InlineData(BackupRunPurpose.PlanFull, BackupStorageMode.LocalAndRemote)]
    [InlineData(BackupRunPurpose.PlanDifferential, BackupStorageMode.LocalOnly)]
    [InlineData(BackupRunPurpose.PlanDifferential, BackupStorageMode.LocalAndRemote)]
    public async Task RecoveredTaskReachesTerminalWithoutAnotherBackup(BackupRunPurpose purpose, BackupStorageMode mode)
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, mode);
            if (purpose == BackupRunPurpose.PlanDifferential)
            {
                var full = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
                Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Runner.RunTaskOnceAsync(new(full, Guid.NewGuid())));
            }

            var taskId = await test.CreateTaskAsync(purpose);
            test.AfterBackupResponse = () => test.ExpireLeaseAsync(taskId);
            Assert.Equal(BackupExecutionContractCode.LeaseLost, await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
            test.AfterBackupResponse = null;
            var invocations = test.Invocations;
            Guid originalAttempt;
            await using (var db = platform.CreateContext())
                originalAttempt = (await db.BackupTasks.SingleAsync(x => x.Id == taskId)).CurrentBackupAttemptId!.Value;

            Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
            var queue = new BackupPlanWorkerStore(new BackupExecutionContractSqlServerTests.Factory(platform));
            var selected = await queue.SelectAsync(DateTimeOffset.UtcNow);
            Assert.Equal(taskId, selected.TaskId);
            Assert.Equal(BackupPlanWorkerSelectionKind.Execute, selected.Kind);
            Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
            await test.AssertCompletedAsync(taskId, purpose == BackupRunPurpose.PlanFull ? BackupType.Full : BackupType.Differential);
            await using var finalDb = platform.CreateContext();
            Assert.Equal(originalAttempt, (await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId)).CurrentBackupAttemptId);
            Assert.Equal(1, await finalDb.BackupAttempts.CountAsync(x => x.TaskId == taskId));
            Assert.Equal(invocations, test.Invocations);
        }
        finally { await platform.DisposeAsync(); }
    }

    [Fact]
    public async Task RemoteOnlyCleanupRecoveryReleasesLeaseAndAllowsProgress()
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            var fault = new PlanExecutionCommitFault("ApplyAsync", "ValidateCopy");
            await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.RemoteOnly, fault);
            var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
            await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
            Assert.True(fault.Fired);
            await using (var db = platform.CreateContext())
            {
                var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
                Assert.Equal(BackupTaskStage.Cleanup, task.CurrentStage);
                Assert.Equal(BackupTaskStatus.Running, task.Status);
                Assert.Single(await db.BackupFiles.Where(x => x.TaskId == taskId && x.Location == BackupFileLocation.Remote && x.Status == BackupFileStatus.Available).ToListAsync());
            }

            await test.ExpireLeaseAsync(taskId);
            Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
            await using var finalDb = platform.CreateContext();
            var recovered = await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId);
            Assert.True(recovered.Status is BackupTaskStatus.Pending or BackupTaskStatus.Succeeded,
                $"核对返回成功后仍无法推进：{recovered.Status}/{recovered.CurrentStage}; lease={recovered.LeasePurpose}");
            Assert.Null(recovered.LeaseToken);
            Assert.Equal(1, test.Invocations);
        }
        finally { await platform.DisposeAsync(); }
    }

    [Fact]
    public async Task DisabledOldestFullDoesNotStarveAnotherDatabase()
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            await using var disabled = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
            await using var healthy = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
            var oldTask = await disabled.CreateTaskAsync(BackupRunPurpose.PlanFull);
            var nextTask = await healthy.CreateTaskAsync(BackupRunPurpose.PlanFull);
            await using (var db = platform.CreateContext())
            {
                var snapshot = await db.BackupTaskSnapshots.SingleAsync(x => x.TaskId == oldTask);
                var credential = await db.CredentialReferences.AsTracking().SingleAsync(x => x.Id == snapshot.SqlCredentialReferenceId);
                credential.SetEnabled(false);
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [BackupTasks] SET [CreatedAtUtc] = {DateTimeOffset.UtcNow.AddDays(-1)} WHERE [Id] = {oldTask}");
            }

            var queue = new BackupPlanWorkerStore(new BackupExecutionContractSqlServerTests.Factory(platform));
            var selected = await queue.SelectAsync(DateTimeOffset.UtcNow);
            Assert.Equal(BackupPlanWorkerSelectionKind.Execute, selected.Kind);
            if (selected.TaskId == oldTask)
            {
                Assert.Equal(BackupExecutionContractCode.Conflict, await disabled.Runner.RunTaskOnceAsync(new(oldTask, Guid.NewGuid())));
                selected = await queue.SelectAsync(DateTimeOffset.UtcNow);
            }
            Assert.Equal(nextTask, selected.TaskId);
            Assert.Equal(BackupExecutionContractCode.Succeeded, await healthy.Runner.RunTaskOnceAsync(new(nextTask, Guid.NewGuid())));
            await healthy.AssertCompletedAsync(nextTask, BackupType.Full);
            Assert.Equal(0, disabled.Invocations);
        }
        finally { await platform.DisposeAsync(); }
    }

    [Fact]
    public async Task ScheduledBaselineRejectionWritesFailureOutbox()
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
            var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanDifferential);
            await using (var db = platform.CreateContext())
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [BackupTasks] SET [TriggerType] = 'Scheduled', [ScheduledSlotAtUtc] = {DateTimeOffset.UtcNow.AddMinutes(-1)} WHERE [Id] = {taskId}");

            var queue = new BackupPlanWorkerStore(new BackupExecutionContractSqlServerTests.Factory(platform));
            await queue.SelectAsync(DateTimeOffset.UtcNow);
            await using var finalDb = platform.CreateContext();
            var task = await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId);
            Assert.Equal(BackupTaskStatus.Failed, task.Status);
            Assert.Equal("baseline_required", task.ErrorCode);
            Assert.Equal(0, test.Invocations);
            Assert.True(await finalDb.NotificationOutbox.AnyAsync(x => x.TaskId == taskId && x.Type == NotificationType.TaskFailed),
                "无基线定时 DIFF 已提交明确失败，但失败通知没有原子入队。");
        }
        finally { await platform.DisposeAsync(); }
    }

    [Theory]
    [InlineData(BackupStorageMode.LocalOnly)]
    [InlineData(BackupStorageMode.LocalAndRemote)]
    public async Task TemporaryCopyOnlyFullCompletesWithoutBecomingManagedBaseline(BackupStorageMode mode)
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, mode);
            var taskId = await test.CreateTaskAsync(BackupRunPurpose.AdHocCopyOnlyFull);
            var code = await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid()));
            Assert.Equal(1, test.Invocations);
            await using var finalDb = platform.CreateContext();
            var task = await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId);
            Assert.True(task.Status == BackupTaskStatus.Succeeded, $"code={code}; {await test.DiagnosticAsync(taskId)}");
            Assert.Equal(BackupExecutionContractCode.Succeeded, code);
            var set = await finalDb.BackupSets.SingleAsync(x => x.TaskId == taskId);
            Assert.True(set.Metadata.IsCopyOnly.Value);
            Assert.Null(await test.Store.FindQualifiedFullAsync(test.DatabaseId, set.Metadata.BackupSetGuid.Value!.Value));
            Assert.Equal(1, test.Invocations);
            Assert.Equal(mode == BackupStorageMode.LocalOnly ? 1 : 2,
                await finalDb.BackupFiles.CountAsync(x => x.TaskId == taskId && x.Status == BackupFileStatus.Available));
        }
        finally { await platform.DisposeAsync(); }
    }
}
