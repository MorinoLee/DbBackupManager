using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupPlanLeaseRenewalLocalDbTests(PlatformDatabaseSqlServerFixture platform)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Fact]
    public async Task RenewalBetweenReadAndEachCommitReplaysOriginalCommandWithoutExternalIo()
    {
        var fault = new PlanExecutionCommitFault("不丢失响应");
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        var now = DateTimeOffset.UtcNow;
        var opened = await test.Store.OpenAsync(taskId, Guid.NewGuid(), false, Guid.NewGuid(), "合成续租协调器", now, now.AddMinutes(2));
        var session = opened.Value!;
        var readsBeforeRenewal = 0;
        var renewals = 0;
        var staleRejections = 0;
        var expires = now.AddMinutes(3);
        fault.AfterResponseAsync = async (method, result) =>
        {
            if (result is BackupExecutionContractResult<FrozenBackupExecutionOperation> { Code: BackupExecutionContractCode.LeaseLost }
                || result is BackupExecutionContractCode.LeaseLost) staleRejections++;
            if (readsBeforeRenewal == 0 || method != nameof(IBackupPlanExecutionStore.RefreshAsync) || result is not BackupPlanExecutionSession snapshot) return;
            if (--readsBeforeRenewal != 0) return;
            Assert.Equal(BackupExecutionContractCode.Succeeded,
                await test.Store.RenewAsync(taskId, snapshot.Work.Lease.LeaseToken, expires));
            expires = expires.AddSeconds(1);
            renewals++;
        };
        var operations = new BackupExecutionOperationStore(new BackupExecutionContractSqlServerTests.Factory(platform), TimeProvider.System);
        var flow = new BackupPlanOperationFlow(test.Store, fault.Wrap<IBackupExecutionOperationStore>(operations), TimeProvider.System);
        readsBeforeRenewal = 2;
        var reserved = await flow.ReserveAsync(session, BackupExecutionOperationKind.Admission, false, CancellationToken.None);
        readsBeforeRenewal = 1;
        var frozen = await flow.FreezeAsync(session, reserved, new()
        {
            Outcome = BackupExecutionOutcome.ConfirmedFailed,
            SqlOutcomeSource = BackupSqlOutcomeSource.NotInvoked,
            ReasonCode = "sql.synthetic_rejected"
        }, [], CancellationToken.None);
        readsBeforeRenewal = 1;
        Assert.Equal(BackupExecutionContractCode.Succeeded, await flow.ApplyAsync(session, frozen, null, CancellationToken.None));
        Assert.Equal(3, renewals);
        Assert.Equal(3, staleRejections);
        await using var db = platform.CreateContext();
        Assert.Equal(BackupTaskStatus.Failed, (await db.BackupTasks.SingleAsync(x => x.Id == taskId)).Status);
        Assert.Equal(BackupExecutionOperationState.Applied, (await db.BackupPlanExecutionOperations.SingleAsync(x => x.Id == reserved.Identity.MutationId)).State);
        Assert.Equal(1, await db.BackupPlanExecutionOperations.CountAsync(x => x.TaskId == taskId && x.Kind == BackupExecutionOperationKind.Admission));
        Assert.Equal(1, await db.BackupTaskStateChanges.CountAsync(x => x.MutationId == reserved.Identity.MutationId));
        Assert.Equal(0, test.Invocations);
        Assert.Equal(0, test.PlanWrites);
    }

    [Fact]
    public async Task GenuineLeaseExpiryBetweenReadAndCommitDoesNotReplayOrCreateAnOperation()
    {
        var fault = new PlanExecutionCommitFault("不丢失响应");
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        var now = DateTimeOffset.UtcNow;
        var session = (await test.Store.OpenAsync(taskId, Guid.NewGuid(), false, Guid.NewGuid(), "合成失租协调器", now, now.AddMinutes(2))).Value!;
        var expired = false;
        fault.AfterResponseAsync = async (method, result) =>
        {
            if (expired || method != nameof(IBackupPlanExecutionStore.RefreshAsync) || result is not BackupPlanExecutionSession) return;
            expired = true;
            await test.ExpireLeaseAsync(taskId);
        };
        var operations = new BackupExecutionOperationStore(new BackupExecutionContractSqlServerTests.Factory(platform), TimeProvider.System);
        var flow = new BackupPlanOperationFlow(test.Store, operations, TimeProvider.System);
        await Assert.ThrowsAsync<PlanExecutionLeaseLostException>(() => flow.ReserveAsync(session, BackupExecutionOperationKind.Admission, false, CancellationToken.None));
        await using var db = platform.CreateContext();
        Assert.False(await db.BackupPlanExecutionOperations.AnyAsync(x => x.TaskId == taskId && x.Kind == BackupExecutionOperationKind.Admission));
        Assert.Equal(0, test.Invocations);
        Assert.Equal(0, test.PlanWrites);
    }

    [Fact]
    public async Task IndependentRenewalContextsSerializeWithoutLosingTheLongerLease()
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        var now = DateTimeOffset.UtcNow;
        var token = Guid.NewGuid();
        await test.Store.OpenAsync(taskId, Guid.NewGuid(), false, token, "合成续租竞争", now, now.AddMinutes(2));
        var factory = new BackupExecutionContractSqlServerTests.Factory(platform);
        var left = new BackupPlanExecutionStore(factory, TimeProvider.System);
        var right = new BackupPlanExecutionStore(factory, TimeProvider.System);
        var result = await Task.WhenAll(left.RenewAsync(taskId, token, now.AddMinutes(3)), right.RenewAsync(taskId, token, now.AddMinutes(4)));
        Assert.All(result, code => Assert.Contains(code, new[] { BackupExecutionContractCode.Succeeded, BackupExecutionContractCode.AlreadyApplied }));
        await using var db = platform.CreateContext();
        Assert.Equal(now.AddMinutes(4), (await db.BackupTasks.SingleAsync(x => x.Id == taskId)).LeaseExpiresAtUtc);
        Assert.Equal(1, await db.BackupAttempts.CountAsync(x => x.TaskId == taskId));
    }
}
