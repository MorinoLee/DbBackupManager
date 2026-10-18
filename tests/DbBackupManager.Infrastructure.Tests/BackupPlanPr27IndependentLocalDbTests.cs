using System.Data.Common;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Notifications;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DbBackupManager.Infrastructure.Tests;

/// <summary>PR #27 的独立边界，仅访问随机 LocalDB 库和测试临时目录。</summary>
public sealed class BackupPlanPr27IndependentLocalDbTests
{
    [Fact]
    public async Task ConfirmedClosureCommitResponseLossReplaysUnderProductionSqlRetryStrategy()
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            var fault = new PlanExecutionCommitFault(nameof(IBackupInvocationAuthorizationStore.RecordTerminationAsync)) { Armed = false };
            await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, fault);
            var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
            fault.AfterResponseAsync = (method, _) => method == nameof(IBackupInvocationAuthorizationStore.AuthorizeAsync)
                ? throw new LostPlanCommitResponseException() : Task.CompletedTask;
            await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
            Assert.Equal(0, test.Invocations);
            fault.AfterResponseAsync = null;
            fault.Armed = true;
            await test.ExpireLeaseAsync(taskId);
            await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
            Assert.True(fault.Fired);

            LeaseHandle lease;
            Guid mutation;
            DateTimeOffset observed;
            string connectionString;
            await using (var db = platform.CreateContext())
            {
                var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
                Assert.Equal(BackupTaskStatus.NeedsAttention, task.Status);
                lease = task.ToLeaseHandle();
                var authorization = await db.BackupInvocationAuthorizations.SingleAsync(x => x.TaskId == taskId);
                mutation = authorization.TerminationMutationId!.Value;
                observed = authorization.TerminalObservedAtUtc!.Value;
                connectionString = db.Database.GetConnectionString()!;
            }
            var lostResponse = new LostConfirmedClosureResponse(taskId);
            var factory = new RetryingFactory(connectionString, lostResponse);
            await using (var context = factory.CreateDbContext())
                Assert.Equal("SqlServerRetryingExecutionStrategy", context.Database.CreateExecutionStrategy().GetType().Name);
            var store = new BackupPlanExecutionStore(factory, TimeProvider.System);
            var result = await store.CloseUnknownAsync(lease, mutation, "sql.recovery_result_unknown_closed", observed);
            Assert.True(lostResponse.Fired);
            Assert.Equal(BackupExecutionContractCode.AlreadyApplied, result);
            Assert.Equal(BackupExecutionContractCode.AlreadyApplied,
                await store.CloseUnknownAsync(lease, mutation, "sql.recovery_result_unknown_closed", observed));
            await using var final = platform.CreateContext();
            Assert.Equal(BackupTaskStatus.ClosedUnknown, (await final.BackupTasks.SingleAsync(x => x.Id == taskId)).Status);
            Assert.Single(await final.BackupTaskStateChanges.Where(x => x.TaskId == taskId && x.ReasonCode == "plan.recovery.close_unknown").ToArrayAsync());
            Assert.Equal(1, await final.NotificationOutbox.CountAsync(x => x.TaskId == taskId && x.Type == NotificationType.TaskIndeterminate));
            Assert.Equal(0, await final.NotificationOutbox.CountAsync(x => x.TaskId == taskId && x.Type == NotificationType.TaskFailed));
            Assert.Equal(0, test.Invocations);
        }
        finally { await platform.DisposeAsync(); }
    }

    [Fact]
    public async Task MigrationDownAndUpWorksAndClosedUnknownBlocksDownWithoutDataLoss()
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            string last;
            string previous;
            await using (var db = platform.CreateContext())
            {
                var migrations = db.Database.GetMigrations().ToArray();
                last = migrations[^1];
                previous = migrations[^2];
                Assert.EndsWith("AddClosedUnknownTaskStatus", last, StringComparison.Ordinal);
                await db.GetService<IMigrator>().MigrateAsync(previous);
            }
            await using (var db = platform.CreateContext())
            {
                Assert.DoesNotContain(last, await db.Database.GetAppliedMigrationsAsync());
                await db.GetService<IMigrator>().MigrateAsync(last);
            }
            var fault = new PlanExecutionCommitFault(nameof(IBackupInvocationAuthorizationStore.AuthorizeAsync));
            await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, fault);
            var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
            await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
            await test.ExpireLeaseAsync(taskId);
            Assert.Equal(BackupExecutionContractCode.Succeeded,
                await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
            await using (var db = platform.CreateContext())
            {
                Assert.Equal(BackupTaskStatus.ClosedUnknown, (await db.BackupTasks.SingleAsync(x => x.Id == taskId)).Status);
                var error = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync(previous));
                Assert.Equal(51012, error.Number);
            }
            await using var final = platform.CreateContext();
            Assert.Contains(last, await final.Database.GetAppliedMigrationsAsync());
            Assert.Equal(BackupTaskStatus.ClosedUnknown, (await final.BackupTasks.SingleAsync(x => x.Id == taskId)).Status);
            Assert.Equal(0, test.Invocations);
        }
        finally { await platform.DisposeAsync(); }
    }

    private sealed class RetryingFactory(string connectionString, IInterceptor interceptor) : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                sql.UseCompatibilityLevel(PlatformDatabaseServiceCollectionExtensions.CompatibilityLevel);
                sql.EnableRetryOnFailure(3);
            }).AddInterceptors(interceptor).Options);
    }

    private sealed class LostConfirmedClosureResponse(Guid taskId) : DbTransactionInterceptor
    {
        public bool Fired { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context is PlatformDbContext db
                && db.ChangeTracker.Entries<BackupTask>().Any(x => x.Entity.Id == taskId && x.Entity.Status == BackupTaskStatus.ClosedUnknown))
            {
                Fired = true;
                throw new TimeoutException("独立反例：COMMIT 已确认完成后丢失收口响应。");
            }
            return Task.CompletedTask;
        }
    }
}
