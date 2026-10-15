using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupPlanExplicitClaimLocalDbTests(PlatformDatabaseSqlServerFixture platform)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Theory]
    [InlineData(BackupRunPurpose.PlanLog, BackupStorageMode.LocalOnly)]
    [InlineData(BackupRunPurpose.PlanLog, BackupStorageMode.RemoteOnly)]
    public async Task ClosedPurposeAndModeAreRejectedBeforeClaimAndAttempt(BackupRunPurpose purpose, BackupStorageMode mode)
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, mode);
        var taskId = await test.CreateTaskAsync(purpose);
        Assert.Equal(BackupExecutionContractCode.StageNotOpen, await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        await using var db = platform.CreateContext();
        Assert.Equal(BackupTaskStatus.Pending, (await db.BackupTasks.SingleAsync(x => x.Id == taskId)).Status);
        Assert.False(await db.BackupAttempts.AnyAsync(x => x.TaskId == taskId));
        Assert.False(await db.BackupPlanExecutionOperations.AnyAsync(x => x.TaskId == taskId));
        Assert.Equal(0, test.Invocations);
        Assert.Equal(0, test.PlanWrites);
    }

    [Fact]
    public async Task IndependentClaimsHaveOneWinnerAndReplayNeverCreatesAnotherAttemptOrHistory()
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        var other = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        var now = DateTimeOffset.UtcNow;
        var mutation = Guid.NewGuid();
        var factory = new BackupExecutionContractSqlServerTests.Factory(platform);
        var first = new BackupPlanExecutionStore(factory, TimeProvider.System);
        var second = new BackupPlanExecutionStore(factory, TimeProvider.System);
        var decisions = await Task.WhenAll(first.OpenAsync(taskId, mutation, false, Guid.NewGuid(), "合成协调器甲", now, now.AddMinutes(2)),
            second.OpenAsync(taskId, Guid.NewGuid(), false, Guid.NewGuid(), "合成协调器乙", now, now.AddMinutes(2)));
        Assert.Single(decisions, x => x.Code == BackupExecutionContractCode.Succeeded);
        Assert.Single(decisions, x => x.Code == BackupExecutionContractCode.Conflict);
        var winner = decisions.Single(x => x.Code == BackupExecutionContractCode.Succeeded).Value!;
        Guid savedMutation;
        await using (var db = platform.CreateContext())
            savedMutation = (await db.BackupTaskStateChanges.SingleAsync(x => x.TaskId == taskId && x.ReasonCode == "plan.execution.claimed")).MutationId;
        Assert.Equal(BackupExecutionContractCode.AlreadyApplied,
            (await first.OpenAsync(taskId, savedMutation, false, Guid.NewGuid(), "重启后重放", now, now.AddMinutes(2))).Code);
        Assert.Equal(BackupExecutionContractCode.Conflict,
            (await first.OpenAsync(other, savedMutation, false, Guid.NewGuid(), "跨任务冲突", now, now.AddMinutes(2))).Code);
        await using var finalDb = platform.CreateContext();
        Assert.Equal(1, await finalDb.BackupAttempts.CountAsync(x => x.TaskId == taskId));
        Assert.Equal(1, await finalDb.BackupTaskStateChanges.CountAsync(x => x.TaskId == taskId));
        Assert.False(await finalDb.BackupAttempts.AnyAsync(x => x.TaskId == other));
        Assert.Equal(winner.Work.Attempt.Id, (await finalDb.BackupAttempts.SingleAsync(x => x.TaskId == taskId)).Id);
    }

    [Fact]
    public async Task TwoScopesCannotBothClaimWhileAnotherRunningLeaseOrInvocationIsOpen()
    {
        await using var first = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
        await using var second = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
        var left = await first.CreateTaskAsync(BackupRunPurpose.PlanFull);
        var right = await second.CreateTaskAsync(BackupRunPurpose.PlanFull);
        var factory = new BackupExecutionContractSqlServerTests.Factory(platform);
        var now = DateTimeOffset.UtcNow;
        var storeA = new BackupPlanExecutionStore(factory, TimeProvider.System);
        var storeB = new BackupPlanExecutionStore(factory, TimeProvider.System);
        var decisions = await Task.WhenAll(
            storeA.OpenAsync(left, Guid.NewGuid(), false, Guid.NewGuid(), "合成协调器甲", now, now.AddMinutes(2)),
            storeB.OpenAsync(right, Guid.NewGuid(), false, Guid.NewGuid(), "合成协调器乙", now, now.AddMinutes(2)));

        Assert.Single(decisions, item => item.Code == BackupExecutionContractCode.Succeeded);
        Assert.Single(decisions, item => item.Code == BackupExecutionContractCode.Conflict);
        await using var db = platform.CreateContext();
        Assert.Equal(1, await db.BackupTasks.CountAsync(item =>
            (item.Id == left || item.Id == right) && item.Status == BackupTaskStatus.Running && item.LeaseExpiresAtUtc > now));
    }

    [Fact]
    public async Task DisabledSnapshotCredentialIsRejectedBeforeClaimWithoutReplacingConfiguration()
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        await using (var db = platform.CreateContext())
        {
            var snapshot = await db.BackupTaskSnapshots.SingleAsync(x => x.TaskId == taskId);
            var credential = await db.CredentialReferences.AsTracking().SingleAsync(x => x.Id == snapshot.SqlCredentialReferenceId);
            credential.SetEnabled(false);
            await db.SaveChangesAsync();
        }
        Assert.Equal(BackupExecutionContractCode.Conflict, await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        await using var finalDb = platform.CreateContext();
        Assert.False(await finalDb.BackupAttempts.AnyAsync(x => x.TaskId == taskId));
        Assert.Equal(0, test.Invocations);
    }

    [Fact]
    public async Task FailedAtomicClaimLeavesNoAttemptOperationOrClaimHistory()
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, null, new ClaimFailure());
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        int initialEvents;
        await using (var db = platform.CreateContext()) initialEvents = await db.TaskEvents.CountAsync(x => x.TaskId == taskId);
        await Assert.ThrowsAsync<IOException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        await using var finalDb = platform.CreateContext();
        var task = await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId);
        Assert.Equal(BackupTaskStatus.Pending, task.Status);
        Assert.Null(task.CurrentBackupAttemptId);
        Assert.Null(task.LeaseToken);
        Assert.False(await finalDb.BackupAttempts.AnyAsync(x => x.TaskId == taskId));
        Assert.False(await finalDb.BackupPlanExecutionOperations.AnyAsync(x => x.TaskId == taskId));
        Assert.False(await finalDb.BackupTaskStateChanges.AnyAsync(x => x.TaskId == taskId));
        Assert.Equal(initialEvents, await finalDb.TaskEvents.CountAsync(x => x.TaskId == taskId));
        Assert.Equal(0, test.Invocations);
    }
    private sealed class ClaimFailure : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<BackupAttempt>().Any(x => x.State == EntityState.Added))
                throw new IOException("合成认领保存失败");
            return ValueTask.FromResult(result);
        }
    }
}
