using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupPlanRecoverySafetyLocalDbTests(PlatformDatabaseSqlServerFixture platform)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Theory]
    [InlineData(BackupTaskStage.Transfer, 1, false)]
    [InlineData(BackupTaskStage.Transfer, 3, false)]
    [InlineData(BackupTaskStage.Transfer, 3, true)]
    [InlineData(BackupTaskStage.ValidateCopy, 1, false)]
    public async Task CancellationCommittedBeforeWriteReservationPreventsNewRemoteWrites(BackupTaskStage cancelledStage,
        int cancelAtRead, bool renewAfterCancellation)
    {
        var fault = new PlanExecutionCommitFault("unused") { Armed = false };
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalAndRemote, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        var cancelled = false;
        var stageReads = 0;
        fault.AfterResponseAsync = async (method, result) =>
        {
            if (cancelled || method != nameof(IBackupPlanExecutionStore.RefreshAsync) || result is not BackupPlanExecutionSession session
                || session.Work.Lease.Stage != cancelledStage || ++stageReads < cancelAtRead) return;
            cancelled = true;
            await using (var db = platform.CreateContext())
            {
                var kind = cancelledStage == BackupTaskStage.Transfer ? BackupExecutionOperationKind.Transfer : BackupExecutionOperationKind.ValidateCopy;
                Assert.False(await db.BackupPlanExecutionOperations.AnyAsync(x => x.TaskId == taskId && x.Kind == kind));
                var task = await db.BackupTasks.AsTracking().SingleAsync(x => x.Id == taskId);
                task.RequestCancellation(DateTimeOffset.UtcNow, BackupStorageMode.LocalAndRemote);
                await db.SaveChangesAsync();
            }
            if (renewAfterCancellation)
                Assert.Equal(BackupExecutionContractCode.Succeeded,
                    await test.Store.RenewAsync(taskId, session.Work.Lease.LeaseToken, DateTimeOffset.UtcNow.AddMinutes(3)));
        };
        Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        Assert.True(cancelled);
        string localPath;
        string? partialPath;
        await using (var db = platform.CreateContext())
        {
            var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
            Assert.Equal(BackupTaskStatus.NeedsAttention, task.Status);
            Assert.Equal(cancelledStage, task.CurrentStage);
            Assert.Equal("plan.cancelled_after_sql", task.ErrorCode);
            Assert.NotNull(task.CancellationRequestedAtUtc);
            var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId);
            Assert.Equal(BackupInvocationStatus.Succeeded, attempt.BackupInvocationStatus);
            Assert.NotNull(attempt.LocalVerifiedAtUtc);
            partialPath = attempt.RemotePartialFilePath;
            var set = await db.BackupSets.SingleAsync(x => x.TaskId == taskId);
            Assert.True(set.SqlSuccessObserved);
            Assert.True(BackupPlanExecutionFactRules.MetadataPassed(set, await db.BackupSetEvidence.Where(x => x.TaskId == taskId).ToArrayAsync()));
            var local = await db.BackupFiles.SingleAsync(x => x.TaskId == taskId);
            Assert.Equal(BackupFileStatus.Available, local.Status);
            Assert.Equal(BackupFileLocation.Local, local.Location);
            Assert.Equal(set.Id, local.BackupSetId);
            localPath = local.Path;
            Assert.False(await db.BackupPlanExecutionOperations.AnyAsync(x => x.TaskId == taskId && x.Kind == BackupExecutionOperationKind.ValidateCopy));
            if (cancelledStage == BackupTaskStage.Transfer)
                Assert.False(await db.BackupPlanExecutionOperations.AnyAsync(x => x.TaskId == taskId && x.Kind == BackupExecutionOperationKind.Transfer));
        }
        Assert.True(File.Exists(localPath));
        Assert.Equal(1, test.Invocations);
        Assert.Equal(cancelledStage == BackupTaskStage.Transfer ? 0 : 1, test.PlanWrites);
        Assert.Equal(cancelledStage == BackupTaskStage.ValidateCopy, File.Exists(partialPath));
        Assert.Equal(0, test.PlanDeletions);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ActiveOrPermissionUnknownRecoveryCanLaterReleaseOriginalDatabaseBlock(bool lackPermission, bool loseFrozenResponse)
    {
        var fault = new PlanExecutionCommitFault(nameof(IBackupExecutionOperationStore.FreezeAsync), "Recovery") { Armed = loseFrozenResponse };
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, fault);
        if (lackPermission) await test.UseRestrictedTerminationVisibilityAsync();
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        Guid blockedTaskId = Guid.Empty, attemptId = Guid.Empty;
        DateTimeOffset? originalSuccessTime = null;
        FrozenBackupExecutionOperation? firstObservation = null;
        var sqlMetadataReads = 0;
        test.AfterBackupResponse = async () =>
        {
            // 实际 BACKUP 已完成，原连接仍存在；只能保存成功，不能据此解除授权。
            await test.ExpireLeaseAsync(taskId);
            var request = new ReconcileBackupPlanTaskRequest(taskId, Guid.NewGuid());
            if (loseFrozenResponse)
            {
                await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Reconciler.ReconcilePlanTaskOnceAsync(request));
                Assert.True(fault.Fired);
                await test.ExpireLeaseAsync(taskId);
                request = new(taskId, Guid.NewGuid());
            }
            Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Reconciler.ReconcilePlanTaskOnceAsync(request));
            Assert.Equal(1, test.TerminationReads); // Frozen 回放不另取终止读数。
            sqlMetadataReads = test.MetadataReads;
            var saved = await AssertWaitingAsync(taskId, lackPermission
                ? "sql.recovery_termination_permission_denied" : "sql.recovery_original_call_active");
            attemptId = saved.AttemptId;
            originalSuccessTime = saved.FinishedAtUtc;
            firstObservation = saved.Observation;
            Assert.True(firstObservation.Facts.SqlSuccessObserved);
            Assert.False(firstObservation.Facts.OriginalCallTerminated);
            Assert.Contains(firstObservation.Observations, x => x.Source == BackupExecutionObservationSource.Termination
                && x.Facts.ReasonCode == (lackPermission ? "termination.permission_denied" : "termination.original_session_present"));
            Assert.Equal(BackupExecutionContractCode.AlreadyApplied, await test.Reconciler.ReconcilePlanTaskOnceAsync(request));
            Assert.Equal(1, test.TerminationReads);
            blockedTaskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
            await test.Runner.RunTaskOnceAsync(new(blockedTaskId, Guid.NewGuid()));
            await using var db = platform.CreateContext();
            Assert.Equal("sql.database_blocked", (await db.BackupTasks.SingleAsync(x => x.Id == blockedTaskId)).ErrorCode);
            Assert.Equal(1, test.Invocations);
        };
        Assert.Equal(BackupExecutionContractCode.LeaseLost, await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        test.AfterBackupResponse = null; // 原执行器已释放连接且不能再次消费原调用权。
        test.RestrictedTerminationProbe = null;
        Assert.Equal(BackupExecutionContractCode.Conflict, await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        var laterRequest = new ReconcileBackupPlanTaskRequest(taskId, Guid.NewGuid());
        Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Reconciler.ReconcilePlanTaskOnceAsync(laterRequest));
        Assert.Equal(2, test.TerminationReads);
        Assert.Equal(sqlMetadataReads, test.MetadataReads);
        Assert.Equal(1, test.Invocations);
        await using (var db = platform.CreateContext())
        {
            var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
            Assert.Equal(BackupTaskStatus.Pending, task.Status);
            Assert.Equal(BackupTaskStage.VerifyLocal, task.CurrentStage);
            var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId);
            Assert.Equal(attemptId, attempt.Id);
            Assert.Equal(originalSuccessTime, attempt.BackupFinishedAtUtc);
            var recovery = await db.BackupPlanExecutionOperations.Where(x => x.TaskId == taskId && x.Kind == BackupExecutionOperationKind.Recovery).OrderBy(x => x.Sequence).ToArrayAsync();
            Assert.Equal(2, recovery.Length);
            Assert.Equal(firstObservation!.Facts, recovery[0].Facts);
            Assert.True(recovery[1].Facts.OriginalCallTerminated);
            Assert.Equal(recovery[0].Facts.Metadata, recovery[1].Facts.Metadata);
            Assert.Equal(recovery[0].Facts.Completion, recovery[1].Facts.Completion);
            var authorization = await db.BackupInvocationAuthorizations.SingleAsync(x => x.TaskId == taskId);
            Assert.NotNull(authorization.TerminalObservedAtUtc);
            Assert.Equal(recovery[1].Id, authorization.TerminationEvidenceId);
        }
        Assert.Equal(BackupExecutionContractCode.AlreadyApplied, await test.Reconciler.ReconcilePlanTaskOnceAsync(laterRequest));
        Assert.Equal(2, test.TerminationReads);
        await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid()));
        await test.AssertCompletedAsync(taskId, BackupType.Full);
        Assert.Equal(1, test.Invocations);
        // 阻塞解除后只读交还原 Prepared Attempt，再显式执行同库另一任务。
        await test.Reconciler.ReconcilePlanTaskOnceAsync(new(blockedTaskId, Guid.NewGuid()));
        await test.Runner.RunTaskOnceAsync(new(blockedTaskId, Guid.NewGuid()));
        await test.AssertCompletedAsync(blockedTaskId, BackupType.Full);
        Assert.Equal(2, test.Invocations);
        Assert.Equal(0, test.PlanWrites);
        Assert.Equal(0, test.PlanDeletions);
    }

    private async Task<(Guid AttemptId, DateTimeOffset? FinishedAtUtc, FrozenBackupExecutionOperation Observation)> AssertWaitingAsync(
        Guid taskId, string expectedReason)
    {
        await using var db = platform.CreateContext();
        var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
        Assert.Equal(BackupTaskStatus.NeedsAttention, task.Status);
        Assert.Equal(BackupTaskStage.Backup, task.CurrentStage);
        Assert.Equal(expectedReason, task.ErrorCode);
        var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId);
        Assert.Equal(BackupInvocationStatus.Succeeded, attempt.BackupInvocationStatus);
        Assert.Null((await db.BackupInvocationAuthorizations.SingleAsync(x => x.TaskId == taskId)).TerminalObservedAtUtc);
        var operation = await db.BackupPlanExecutionOperations.SingleAsync(x => x.TaskId == taskId && x.Kind == BackupExecutionOperationKind.Recovery);
        return (attempt.Id, attempt.BackupFinishedAtUtc, await BackupExecutionOperationStore.DetachAsync(db, operation, CancellationToken.None));
    }
}
