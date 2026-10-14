using System.Data.Common;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DbBackupManager.Infrastructure.Tests;

/// <summary>用真实 SQL 协调锁等待验证续租和换主，不以拦截器伪造续租结果。</summary>
public sealed class BackupPlanCleanupRenewalIndependentLocalDbTests(PlatformDatabaseSqlServerFixture platform)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CoordinationWaitAcceptsRealRenewalAndRejectsReplacement(bool renew)
    {
        var fault = new PlanExecutionCommitFault("ApplyAsync", "ValidateCopy");
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.RemoteOnly, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        await test.ExpireLeaseAsync(taskId);
        var boundary = new ObserveCoordinationCommand();
        var factory = new BackupExecutionContractSqlServerTests.Factory(platform, boundary);
        var store = new BackupPlanExecutionStore(factory, TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        var opened = await store.OpenAsync(taskId, Guid.NewGuid(), true, Guid.NewGuid(), "合成并发复审", now, now.AddMinutes(2));
        Assert.Equal(BackupExecutionContractCode.Succeeded, opened.Code);
        var session = opened.Value!;
        string connectionString;
        int histories, audits, notices;
        await using (var db = platform.CreateContext())
        {
            connectionString = db.Database.GetConnectionString()!;
            histories = await db.BackupTaskStateChanges.CountAsync();
            audits = await db.AuditRecords.CountAsync();
            notices = await db.NotificationOutbox.CountAsync();
        }

        await using var coordination = new SqlConnection(connectionString);
        await coordination.OpenAsync();
        var resource = PlanRetentionLock.Name(test.DatabaseId);
        Assert.True(await LockAsync(coordination, resource, true) >= 0);
        var lockHeld = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<BackupExecutionContractCode>? pending = null;
        var replacement = Guid.NewGuid();
        var writes = test.PlanWrites;
        var deletions = test.PlanDeletions;
        try
        {
            boundary.Armed = true;
            var flow = new BackupPlanOperationFlow(store, new BackupExecutionOperationStore(factory, TimeProvider.System), TimeProvider.System);
            pending = flow.CommitAsync(session,
                next => store.ReleaseDeferredCleanupForExecutionAsync(next.Work.Lease, timeout.Token), x => x, timeout.Token);
            var waiter = await boundary.Entered.Task.WaitAsync(timeout.Token);
            await AssertWaitingAsync(coordination, waiter, timeout.Token);
            Assert.False(pending.IsCompleted);
            if (renew)
            {
                var expires = DateTimeOffset.UtcNow.AddMinutes(5);
                Assert.Equal(BackupExecutionContractCode.Succeeded,
                    await store.RenewAsync(taskId, session.Work.Lease.LeaseToken, expires, timeout.Token));
                var refreshed = await store.RefreshAsync(taskId, session.Work.Lease.LeaseToken, timeout.Token);
                Assert.NotNull(refreshed);
                Assert.Equal(expires, refreshed.Work.Lease.ExpiresAtUtc);
                Assert.False(session.Work.Lease.RowVersion.SequenceEqual(refreshed.Work.Lease.RowVersion));
            }
            else
            {
                // 仅在本次随机测试库注入换主；这不宣称验证了生产认领器的换主过程。
                await using var db = platform.CreateContext();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE [BackupTasks] SET [LeaseToken] = {replacement} WHERE [Id] = {taskId}", timeout.Token);
            }
            await LockAsync(coordination, resource, false);
            lockHeld = false;
            Assert.Equal(renew ? BackupExecutionContractCode.Succeeded : BackupExecutionContractCode.LeaseLost,
                await pending.WaitAsync(timeout.Token));
        }
        finally
        {
            if (lockHeld) await LockAsync(coordination, resource, false);
            if (pending is { IsCompleted: false })
            {
                await timeout.CancelAsync();
                try { await pending; }
                catch (OperationCanceledException) { }
            }
        }

        Assert.Equal(1, test.Invocations);
        Assert.Equal(writes, test.PlanWrites);
        Assert.Equal(deletions, test.PlanDeletions);
        await using (var finalDb = platform.CreateContext())
        {
            var final = await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId);
            Assert.Equal(renew ? BackupTaskStatus.Pending : BackupTaskStatus.NeedsAttention, final.Status);
            Assert.Equal(BackupTaskStage.Cleanup, final.CurrentStage);
            Assert.Equal(session.Work.Attempt.Id, final.CurrentBackupAttemptId);
            Assert.Equal(renew ? (Guid?)null : replacement, final.LeaseToken);
            Assert.Equal(1, await finalDb.BackupAttempts.CountAsync(x => x.TaskId == taskId));
            Assert.Equal(histories + (renew ? 1 : 0), await finalDb.BackupTaskStateChanges.CountAsync());
            Assert.Equal(audits + (renew ? 1 : 0), await finalDb.AuditRecords.CountAsync());
            Assert.Equal(notices, await finalDb.NotificationOutbox.CountAsync());
        }
        if (renew)
        {
            var queue = new BackupPlanWorkerStore(factory);
            Assert.Equal(taskId, (await queue.SelectAsync(DateTimeOffset.UtcNow)).TaskId);
            Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
            await test.AssertCompletedAsync(taskId, BackupType.Full);
            Assert.Equal(1, test.Invocations);
            Assert.Equal(writes, test.PlanWrites);
            await using var completedDb = platform.CreateContext();
            var completed = await completedDb.BackupTasks.SingleAsync(x => x.Id == taskId);
            Assert.Equal(session.Work.Attempt.Id, completed.CurrentBackupAttemptId);
            Assert.Equal(1, await completedDb.BackupAttempts.CountAsync(x => x.TaskId == taskId));
        }
    }

    private static async Task<int> LockAsync(SqlConnection connection, string resource, bool acquire)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = acquire
            ? "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource=@resource, @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0; SELECT @result;"
            : "DECLARE @result int; EXEC @result = sys.sp_releaseapplock @Resource=@resource, @LockOwner='Session'; SELECT @result;";
        command.Parameters.AddWithValue("@resource", resource);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertWaitingAsync(SqlConnection connection, int sessionId, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE request_session_id=@session AND resource_type='APPLICATION' AND request_status='WAIT';";
            query.Parameters.AddWithValue("@session", sessionId);
            if ((int)(await query.ExecuteScalarAsync(token))! > 0) return;
            await Task.Delay(20, token);
        }
        Assert.Fail("未在 SQL 锁视图中观察到真实协调锁等待，不能登记该并发证据。");
    }

    private sealed class ObserveCoordinationCommand : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource<int> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("sp_getapplock", StringComparison.Ordinal))
                Entered.TrySetResult(((SqlConnection)command.Connection!).ServerProcessId);
            return ValueTask.FromResult(result);
        }
    }
}
