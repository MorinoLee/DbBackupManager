using System.Data.Common;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DbBackupManager.Infrastructure.Tests;

/// <summary>独立复审新增边界：真实暂存删除后的只读收口，以及协调等待期间的失租。</summary>
public sealed class BackupPlanFixIndependentReviewLocalDbTests(PlatformDatabaseSqlServerFixture platform)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Fact]
    public async Task ConfirmedDeletedStagingCanFinishReadOnlyAfterCredentialIsDisabled()
    {
        var failure = new FailTerminalTaskSaveAfterUnlink();
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.RemoteOnly, null, failure);
        var full = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Runner.RunTaskOnceAsync(new(full, Guid.NewGuid())));
        var differential = await test.CreateTaskAsync(BackupRunPurpose.PlanDifferential);
        failure.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => test.Runner.RunTaskOnceAsync(new(differential, Guid.NewGuid())));
        Assert.True(failure.Fired);
        Assert.Equal(1, test.PlanDeletions);
        await using (var db = platform.CreateContext())
        {
            var task = await db.BackupTasks.SingleAsync(x => x.Id == differential);
            Assert.Equal(BackupTaskStatus.NeedsAttention, task.Status);
            Assert.Equal(BackupTaskStage.Cleanup, task.CurrentStage);
            var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == differential);
            Assert.NotNull(attempt.LocalCleanupCompletedAtUtc);
            var local = await db.BackupFiles.SingleAsync(x => x.TaskId == differential && x.Location == BackupFileLocation.Local);
            Assert.Equal(BackupFileStatus.Deleted, local.Status);
            Assert.False(File.Exists(local.Path));
            var snapshot = await db.BackupTaskSnapshots.SingleAsync(x => x.TaskId == differential);
            (await db.CredentialReferences.AsTracking().SingleAsync(x => x.Id == snapshot.SourceCredentialReferenceId)).SetEnabled(false);
            await db.SaveChangesAsync();
        }

        var writes = test.PlanWrites;
        Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Reconciler.ReconcilePlanTaskOnceAsync(new(differential, Guid.NewGuid())));
        Assert.Equal(writes, test.PlanWrites);
        Assert.Equal(1, test.PlanDeletions);
        Assert.Equal(2, test.Invocations);
        await using var finalDb = platform.CreateContext();
        var completed = await finalDb.BackupTasks.SingleAsync(x => x.Id == differential);
        Assert.True(completed.Status == BackupTaskStatus.Succeeded,
            $"已确认删除的暂存仍需重新执行：{completed.Status}/{completed.CurrentStage}; 配置停用后不能再认领。");
        Assert.Null(completed.LeaseToken);
    }

    [Fact]
    public async Task CleanupReconciliationRejectsLeaseThatExpiresWhileAcquiringCoordinationLock()
    {
        var fault = new PlanExecutionCommitFault("ApplyAsync", "ValidateCopy");
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.RemoteOnly, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        await test.ExpireLeaseAsync(taskId);
        var clock = new ReviewClock(DateTimeOffset.UtcNow);
        var boundary = new AdvanceClockAtRetentionLock(clock);
        var store = new BackupPlanExecutionStore(new BackupExecutionContractSqlServerTests.Factory(platform, boundary), clock);
        var opened = await store.OpenAsync(taskId, Guid.NewGuid(), true, Guid.NewGuid(), "合成只读复审", clock.UtcNow,
            clock.UtcNow.AddSeconds(30));
        Assert.Equal(BackupExecutionContractCode.Succeeded, opened.Code);
        boundary.Armed = true;
        var result = await store.ReleaseDeferredCleanupForExecutionAsync(opened.Value!.Work.Lease);
        Assert.True(boundary.Fired);
        Assert.True(clock.UtcNow > opened.Value.Work.Lease.ExpiresAtUtc);
        Assert.Equal(BackupExecutionContractCode.LeaseLost, result);
    }

    [Fact]
    public async Task LosingLeaseAfterRemoteConfirmationBeforeCleanupReturnsLeaseLost()
    {
        var fault = new PlanExecutionCommitFault("不丢失响应");
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.RemoteOnly, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        var expired = false;
        fault.AfterResponseAsync = async (method, result) =>
        {
            if (expired || method != nameof(IBackupPlanExecutionStore.ApplyAsync)
                || result is not BackupExecutionContractCode.Succeeded) return;
            await using var db = platform.CreateContext();
            var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
            if (task.CurrentStage != BackupTaskStage.Cleanup) return;
            expired = true;
            await test.ExpireLeaseAsync(taskId);
        };
        var result = await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid()));
        Assert.True(expired);
        Assert.Equal(1, test.Invocations);
        await using var finalDb = platform.CreateContext();
        var unfinished = await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId);
        Assert.Equal(BackupTaskStatus.Running, unfinished.Status);
        Assert.Equal(BackupTaskStage.Cleanup, unfinished.CurrentStage);
        Assert.Equal(BackupExecutionContractCode.LeaseLost, result);
    }

    [Fact]
    public async Task DisabledOldestDifferentialDoesNotStarveAnotherDatabase()
    {
        var isolated = new PlatformDatabaseSqlServerFixture();
        try
        {
            await isolated.InitializeAsync();
            await using var disabled = await PlanExecutionTestEnvironment.CreateAsync(isolated, BackupStorageMode.LocalOnly);
            await using var healthy = await PlanExecutionTestEnvironment.CreateAsync(isolated, BackupStorageMode.LocalOnly);
            var leftFull = await disabled.CreateTaskAsync(BackupRunPurpose.PlanFull);
            var rightFull = await healthy.CreateTaskAsync(BackupRunPurpose.PlanFull);
            Assert.Equal(BackupExecutionContractCode.Succeeded, await disabled.Runner.RunTaskOnceAsync(new(leftFull, Guid.NewGuid())));
            Assert.Equal(BackupExecutionContractCode.Succeeded, await healthy.Runner.RunTaskOnceAsync(new(rightFull, Guid.NewGuid())));
            var oldTask = await disabled.CreateTaskAsync(BackupRunPurpose.PlanDifferential);
            var nextTask = await healthy.CreateTaskAsync(BackupRunPurpose.PlanDifferential);
            await using (var db = isolated.CreateContext())
            {
                var snapshot = await db.BackupTaskSnapshots.SingleAsync(x => x.TaskId == oldTask);
                (await db.CredentialReferences.AsTracking().SingleAsync(x => x.Id == snapshot.SqlCredentialReferenceId)).SetEnabled(false);
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [BackupTasks] SET [CreatedAtUtc] = {DateTimeOffset.UtcNow.AddDays(-1)} WHERE [Id] = {oldTask}");
            }

            var queue = new BackupPlanWorkerStore(new BackupExecutionContractSqlServerTests.Factory(isolated));
            var selected = await queue.SelectAsync(DateTimeOffset.UtcNow);
            Assert.Equal(BackupPlanWorkerSelectionKind.Execute, selected.Kind);
            if (selected.TaskId == oldTask)
            {
                Assert.Equal(BackupExecutionContractCode.Conflict, await disabled.Runner.RunTaskOnceAsync(new(oldTask, Guid.NewGuid())));
                selected = await queue.SelectAsync(DateTimeOffset.UtcNow);
            }
            Assert.Equal(nextTask, selected.TaskId);
            Assert.Equal(BackupExecutionContractCode.Succeeded, await healthy.Runner.RunTaskOnceAsync(new(nextTask, Guid.NewGuid())));
            await healthy.AssertCompletedAsync(nextTask, BackupType.Differential);
        }
        finally { await isolated.DisposeAsync(); }
    }

    private sealed class FailTerminalTaskSaveAfterUnlink : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Fired { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && !Fired
                && eventData.Context!.ChangeTracker.Entries<BackupTask>().Any(x => x.Entity.Status == BackupTaskStatus.Succeeded)
                && eventData.Context.ChangeTracker.Entries<BackupAttempt>().Any(x => x.Entity.LocalCleanupCompletedAtUtc is not null))
            {
                Fired = true;
                throw new IOException("合成故障：实际删除已经提交，任务收口之前中断。");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ReviewClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class AdvanceClockAtRetentionLock(ReviewClock clock) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Fired { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && !Fired && command.CommandText.Contains("sp_getapplock", StringComparison.Ordinal))
            {
                Fired = true;
                clock.UtcNow = clock.UtcNow.AddMinutes(1);
            }
            return ValueTask.FromResult(result);
        }
    }
}
