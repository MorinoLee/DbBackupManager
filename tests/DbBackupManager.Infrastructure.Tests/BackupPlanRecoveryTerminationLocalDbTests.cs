using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Notifications;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DbBackupManager.Infrastructure.Tests;

/// <summary>
/// E04-01 独立复审反例：把“原调用是否还能继续执行”与“备份结果是否成功”当作两项独立事实。
/// 终止探测不得依赖成功备份头/msdb；没有有效终止证据时未知继续阻塞。
/// </summary>
public sealed class BackupPlanRecoveryTerminationLocalDbTests
{
    [Fact]
    public async Task ClosedOriginalConnectionWithoutSuccessEvidenceClosesUnknownAndUnblocksAnotherDatabase()
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            var fault = new PlanExecutionCommitFault(nameof(IBackupInvocationAuthorizationStore.AuthorizeAsync));
            await using var failed = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, fault);
            await using var healthy = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
            var failedTask = await failed.CreateTaskAsync(BackupRunPurpose.PlanFull);

            // 授权事务已经提交，但 BACKUP 从未发出；异常展开时原连接关闭。
            await Assert.ThrowsAsync<LostPlanCommitResponseException>(
                () => failed.Runner.RunTaskOnceAsync(new(failedTask, Guid.NewGuid())));
            Assert.True(fault.Fired);
            Assert.Equal(0, failed.Invocations);
            Guid attemptId;
            await using (var db = platform.CreateContext())
            {
                var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == failedTask);
                attemptId = attempt.Id;
                Assert.Equal(BackupInvocationStatus.Running, attempt.BackupInvocationStatus);
                Assert.False(File.Exists(attempt.LocalSqlFilePath));
                Assert.False(await db.BackupSets.AnyAsync(x => x.TaskId == failedTask));
            }

            await failed.ExpireLeaseAsync(failedTask);
            var writes = failed.PlanWrites;
            Assert.Equal(BackupExecutionContractCode.Succeeded,
                await failed.Reconciler.ReconcilePlanTaskOnceAsync(new(failedTask, Guid.NewGuid())));
            Assert.Equal(writes, failed.PlanWrites);
            // 成功元数据不存在时终止探测仍必须执行，不能停在 sql.recovery_unknown。
            Assert.True(failed.MetadataReads >= 1);
            Assert.True(failed.TerminationReads >= 1);

            await using (var db = platform.CreateContext())
            {
                var closed = await db.BackupTasks.SingleAsync(x => x.Id == failedTask);
                Assert.Equal(BackupTaskStatus.ClosedUnknown, closed.Status);
                Assert.Equal("sql.recovery_result_unknown_closed", closed.ErrorCode);
                Assert.NotNull(closed.CompletedAtUtc);
                Assert.Null(closed.LeaseToken);
                Assert.Equal(attemptId, closed.CurrentBackupAttemptId);
                Assert.Equal(1, await db.BackupAttempts.CountAsync(x => x.TaskId == failedTask));
                var authorization = await db.BackupInvocationAuthorizations.SingleAsync(x => x.AttemptId == attemptId);
                Assert.Equal(BackupInvocationTerminationKind.RecoveredTerminated, authorization.TerminationKind);
                Assert.NotNull(authorization.TerminalObservedAtUtc);
                Assert.Equal(BackupInvocationBindingState.Known, authorization.Binding.State);
                Assert.NotNull(authorization.Binding.ConnectionId);
                Assert.False(await db.BackupSets.AnyAsync(x => x.TaskId == failedTask));
                Assert.False(await db.BackupFiles.AnyAsync(x => x.TaskId == failedTask));
                // 不是明确失败；未知结果单独通知。
                Assert.Equal(0, await db.NotificationOutbox.CountAsync(
                    x => x.TaskId == failedTask && x.Type == NotificationType.TaskFailed));
                Assert.Equal(1, await db.NotificationOutbox.CountAsync(
                    x => x.TaskId == failedTask && x.Type == NotificationType.TaskIndeterminate));
            }

            // 另一健康数据库能被正常选择和执行，旧调用不重发。
            var healthyTask = await healthy.CreateTaskAsync(BackupRunPurpose.PlanFull);
            var queue = new BackupPlanWorkerStore(new BackupExecutionContractSqlServerTests.Factory(platform));
            var selection = await queue.SelectAsync(DateTimeOffset.UtcNow);
            Assert.Equal(BackupPlanWorkerSelectionKind.Execute, selection.Kind);
            Assert.Equal(healthyTask, selection.TaskId);
            Assert.Equal(BackupExecutionContractCode.Succeeded,
                await healthy.Runner.RunTaskOnceAsync(new(healthyTask, Guid.NewGuid())));
            await healthy.AssertCompletedAsync(healthyTask, BackupType.Full);
            Assert.Equal(1, healthy.Invocations);

            // 同一数据库的新任务也不再被已收口的旧 Attempt 永久挡住，旧调用不重发。
            var sameDatabaseTask = await failed.CreateTaskAsync(BackupRunPurpose.PlanFull);
            Assert.Equal(BackupExecutionContractCode.Succeeded,
                await failed.Runner.RunTaskOnceAsync(new(sameDatabaseTask, Guid.NewGuid())));
            await failed.AssertCompletedAsync(sameDatabaseTask, BackupType.Full);
            Assert.Equal(1, failed.Invocations);
            await using (var db = platform.CreateContext())
                Assert.Equal(1, await db.BackupAttempts.CountAsync(x => x.TaskId == failedTask));

            // 终态重复核对不追加历史或通知。
            int history;
            int notifications;
            await using (var db = platform.CreateContext())
            {
                history = await db.BackupTaskStateChanges.CountAsync(x => x.TaskId == failedTask);
                notifications = await db.NotificationOutbox.CountAsync(x => x.TaskId == failedTask);
            }
            Assert.Equal(BackupExecutionContractCode.Conflict,
                await failed.Reconciler.ReconcilePlanTaskOnceAsync(new(failedTask, Guid.NewGuid())));
            await using (var db = platform.CreateContext())
            {
                Assert.Equal(history, await db.BackupTaskStateChanges.CountAsync(x => x.TaskId == failedTask));
                Assert.Equal(notifications, await db.NotificationOutbox.CountAsync(x => x.TaskId == failedTask));
            }
            Assert.Equal(0, failed.PlanDeletions);
        }
        finally { await platform.DisposeAsync(); }
    }

    [Fact]
    public async Task InvokedBackupWithoutSuccessEvidenceClosesUnknownAndKeepsResidualFile()
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
            var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
            // 真实 BACKUP 已执行成功，但成功响应在冻结前丢失；平台没有成功事实。
            test.AfterBackupResponse = () => throw new LostPlanCommitResponseException();
            await Assert.ThrowsAsync<LostPlanCommitResponseException>(
                () => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
            test.AfterBackupResponse = null;
            Assert.Equal(1, test.Invocations);
            string path;
            await using (var db = platform.CreateContext())
            {
                var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId);
                path = attempt.LocalSqlFilePath;
                Assert.True(File.Exists(path));
                Assert.Equal(BackupInvocationStatus.Running, attempt.BackupInvocationStatus);
                Assert.False(await db.BackupSets.AnyAsync(x => x.TaskId == taskId));
            }

            // 成功元数据读取失败：既不能证明成功，也不能从缺失文件/历史反推未执行。
            test.MetadataUnavailable = true;
            await test.ExpireLeaseAsync(taskId);
            Assert.Equal(BackupExecutionContractCode.Succeeded,
                await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
            Assert.Equal(1, test.Invocations);
            Assert.True(File.Exists(path));
            await using var finalDb = platform.CreateContext();
            var closed = await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId);
            Assert.Equal(BackupTaskStatus.ClosedUnknown, closed.Status);
            Assert.Equal("sql.recovery_result_unknown_closed", closed.ErrorCode);
            Assert.Equal(1, await finalDb.BackupAttempts.CountAsync(x => x.TaskId == taskId));
            Assert.False(await finalDb.BackupSets.AnyAsync(x => x.TaskId == taskId));
            // 未登记副本不被 Retention 删除，残留文件原样保留。
            Assert.False(await finalDb.BackupFiles.AnyAsync(x => x.TaskId == taskId));
            Assert.Equal(1, await finalDb.NotificationOutbox.CountAsync(
                x => x.TaskId == taskId && x.Type == NotificationType.TaskIndeterminate));
            Assert.Equal(0, await finalDb.NotificationOutbox.CountAsync(
                x => x.TaskId == taskId && x.Type == NotificationType.TaskFailed));
        }
        finally { await platform.DisposeAsync(); }
    }

    [Fact]
    public async Task ContradictorySuccessEvidenceWithTerminatedCallClosesUnknownWithoutBecomingSuccess()
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
            var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
            var prepared = await AuthorizeWithoutInvocationAsync(platform, test, taskId, knownIdentity: true);
            string path;
            await using (var db = platform.CreateContext())
                path = (await db.BackupAttempts.SingleAsync(x => x.Id == prepared.AttemptId)).LocalSqlFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 同一路径只有一条完整备份记录，但它是 COPY_ONLY，与计划期望的普通 FULL 矛盾。
            var external = await test.SqlAdapter().PrepareAsync(test.SqlInput,
                new(test.TargetDatabaseName, path, BackupRunPurpose.AdHocCopyOnlyFull, true, false, 60), Guid.NewGuid());
            Assert.True(external.IsSucceeded, external.Failure?.ToString());
            await using (var invocation = external.Value!)
            {
                var written = await invocation.InvokeOnceAsync(Guid.NewGuid());
                Assert.True(written.IsSucceeded, written.Failure?.ToString());
            }

            await test.ExpireLeaseAsync(taskId);
            Assert.Equal(BackupExecutionContractCode.Succeeded,
                await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
            await using var finalDb = platform.CreateContext();
            var closed = await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId);
            Assert.Equal(BackupTaskStatus.ClosedUnknown, closed.Status);
            Assert.Equal("sql.recovery_result_unknown_closed", closed.ErrorCode);
            Assert.False(await finalDb.BackupSets.AnyAsync(x => x.TaskId == taskId));
            Assert.True(File.Exists(path));
            var recovery = await finalDb.BackupPlanExecutionOperations.SingleAsync(x => x.TaskId == taskId
                && x.Kind == BackupExecutionOperationKind.Recovery && x.State == BackupExecutionOperationState.Frozen);
            Assert.False(recovery.Facts.SqlSuccessObserved);
            Assert.Equal("sql.recovery_metadata_conflict", recovery.Facts.ReasonCode);
            Assert.True(recovery.Facts.OriginalCallTerminated);
            Assert.Equal(1, await finalDb.NotificationOutbox.CountAsync(
                x => x.TaskId == taskId && x.Type == NotificationType.TaskIndeterminate));
            Assert.Equal(0, await finalDb.NotificationOutbox.CountAsync(
                x => x.TaskId == taskId && x.Type == NotificationType.TaskFailed));
        }
        finally { await platform.DisposeAsync(); }
    }

    [Theory]
    [InlineData(TargetSqlInvocationTerminationState.Active, "termination.original_session_present", "sql.recovery_original_call_active")]
    [InlineData(TargetSqlInvocationTerminationState.Unknown, "termination.permission_denied", "sql.recovery_termination_permission_denied")]
    [InlineData(TargetSqlInvocationTerminationState.Unknown, "termination.cancelled", "sql.recovery_termination_read_failed")]
    [InlineData(TargetSqlInvocationTerminationState.Unknown, "termination.read_failed", "sql.recovery_termination_read_failed")]
    public async Task NonTerminalOrUnreadableProbeKeepsBlockingWithoutClosing(
        TargetSqlInvocationTerminationState state, string probeReason, string expectedReason)
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            var fault = new PlanExecutionCommitFault(nameof(IBackupInvocationAuthorizationStore.AuthorizeAsync));
            await using var blocked = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, fault);
            await using var other = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
            var blockedTask = await blocked.CreateTaskAsync(BackupRunPurpose.PlanFull);
            await Assert.ThrowsAsync<LostPlanCommitResponseException>(
                () => blocked.Runner.RunTaskOnceAsync(new(blockedTask, Guid.NewGuid())));
            blocked.RestrictedTerminationProbe = new FixedTerminationProbe(state, probeReason);
            await blocked.ExpireLeaseAsync(blockedTask);
            Assert.Equal(BackupExecutionContractCode.Succeeded,
                await blocked.Reconciler.ReconcilePlanTaskOnceAsync(new(blockedTask, Guid.NewGuid())));
            await using (var db = platform.CreateContext())
            {
                var task = await db.BackupTasks.SingleAsync(x => x.Id == blockedTask);
                Assert.Equal(BackupTaskStatus.NeedsAttention, task.Status);
                Assert.Equal(expectedReason, task.ErrorCode);
                Assert.Null(task.CompletedAtUtc);
                Assert.Equal(1, await db.BackupAttempts.CountAsync(x => x.TaskId == blockedTask));
                Assert.Null((await db.BackupInvocationAuthorizations.SingleAsync(x => x.TaskId == blockedTask)).TerminalObservedAtUtc);
            }

            // 未知不能冒充终止：全局执行阻塞保留。
            var otherTask = await other.CreateTaskAsync(BackupRunPurpose.PlanFull);
            var now = DateTimeOffset.UtcNow;
            Assert.Equal(BackupExecutionContractCode.Conflict,
                (await other.Store.OpenAsync(otherTask, Guid.NewGuid(), false, Guid.NewGuid(), "合成认领", now, now.AddMinutes(2))).Code);
            var queue = new BackupPlanWorkerStore(new BackupExecutionContractSqlServerTests.Factory(platform));
            Assert.Equal(BackupPlanWorkerSelectionKind.Wait, (await queue.SelectAsync(DateTimeOffset.UtcNow)).Kind);
            Assert.Equal(0, other.Invocations);
            Assert.Equal(0, blocked.Invocations);
        }
        finally { await platform.DisposeAsync(); }
    }

    [Fact]
    public async Task RestrictedDmvVisibilityWithoutSuccessEvidenceKeepsBlocking()
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            var fault = new PlanExecutionCommitFault(nameof(IBackupInvocationAuthorizationStore.AuthorizeAsync));
            await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, fault);
            var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
            await Assert.ThrowsAsync<LostPlanCommitResponseException>(
                () => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
            Assert.Equal(0, test.Invocations);
            await test.UseRestrictedTerminationVisibilityAsync();
            await test.ExpireLeaseAsync(taskId);
            Assert.Equal(BackupExecutionContractCode.Succeeded,
                await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
            Assert.Equal(1, test.TerminationReads);
            await using var db = platform.CreateContext();
            var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
            Assert.Equal(BackupTaskStatus.NeedsAttention, task.Status);
            Assert.Equal("sql.recovery_termination_permission_denied", task.ErrorCode);
            var authorization = await db.BackupInvocationAuthorizations.SingleAsync(x => x.TaskId == taskId);
            Assert.Null(authorization.TerminalObservedAtUtc);
            // 非终止观察不冻结成终止事实，保留为可重试的预约，等待下一次显式核对重新探测。
            var recovery = await db.BackupPlanExecutionOperations.SingleAsync(
                x => x.TaskId == taskId && x.Kind == BackupExecutionOperationKind.Recovery);
            Assert.Equal(BackupExecutionOperationState.Reserved, recovery.State);
            Assert.False(recovery.Facts.OriginalCallTerminated);
            Assert.False(await db.BackupPlanExecutionObservations.AnyAsync(x => x.OperationId == recovery.Id
                && x.Source == BackupExecutionObservationSource.Termination));
        }
        finally { await platform.DisposeAsync(); }
    }

    [Fact]
    public async Task IncompleteOriginalBindingIsNeitherTerminatedNorClosed()
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
            var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
            var prepared = await AuthorizeWithoutInvocationAsync(platform, test, taskId, knownIdentity: false);
            await test.ExpireLeaseAsync(taskId);
            Assert.Equal(BackupExecutionContractCode.Succeeded,
                await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
            Assert.Equal(0, test.TerminationReads);
            await using var db = platform.CreateContext();
            var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
            Assert.Equal(BackupTaskStatus.NeedsAttention, task.Status);
            Assert.Equal("sql.recovery_original_binding_unknown", task.ErrorCode);
            Assert.Equal(prepared.AttemptId, task.CurrentBackupAttemptId);
            Assert.Null((await db.BackupInvocationAuthorizations.SingleAsync(x => x.TaskId == taskId)).TerminalObservedAtUtc);
        }
        finally { await platform.DisposeAsync(); }
    }

    [Fact]
    public async Task TerminationIdentityRequiresConnectionIdOrSessionAndLoginTime()
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
            var adapter = test.SqlAdapter();
            var permit = Guid.NewGuid();
            var prepared = await adapter.PrepareAsync(test.SqlInput,
                new(test.TargetDatabaseName, test.NewSqlPath(), BackupRunPurpose.PlanFull, true, false, 60), Guid.NewGuid());
            Assert.True(prepared.IsSucceeded, prepared.Failure?.ToString());
            await using var invocation = prepared.Value!;
            var binding = invocation.Binding;
            // 实际原 connection 仍存在时不得解除。
            Assert.Equal(TargetSqlInvocationTerminationState.Active,
                (await adapter.ReadAsync(test.SqlInput, permit, binding, 30)).State);
            // 同 session_id、不同 connection/login_time：不能误认成原会话。
            var recycled = new BackupInvocationBinding
            {
                State = BackupInvocationBindingState.Known,
                CallerIncarnationId = Guid.NewGuid(),
                SessionId = binding.SessionId,
                SessionEstablishedLocal = binding.SessionEstablishedLocal!.Value.AddMinutes(-5),
                ConnectionId = Guid.NewGuid()
            };
            Assert.Equal(TargetSqlInvocationTerminationState.Terminated,
                (await adapter.ReadAsync(test.SqlInput, permit, recycled, 30)).State);
            // session_id + login_time 同时匹配才算原会话：即使 connection_id 不同也按活动处理。
            var sameSession = recycled with
            {
                SessionEstablishedLocal = binding.SessionEstablishedLocal,
                ConnectionId = Guid.NewGuid()
            };
            Assert.Equal(TargetSqlInvocationTerminationState.Active,
                (await adapter.ReadAsync(test.SqlInput, permit, sameSession, 30)).State);
            await invocation.DisposeAsync();
            Assert.Equal(TargetSqlInvocationTerminationState.Terminated,
                (await adapter.ReadAsync(test.SqlInput, permit, binding, 30)).State);
        }
        finally { await platform.DisposeAsync(); }
    }

    [Fact]
    public async Task SuccessEvidenceWithLaterTerminationCompletesOriginalAttemptWithoutRereadingMetadata()
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
            var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
            test.AfterBackupResponse = () => throw new LostPlanCommitResponseException();
            await Assert.ThrowsAsync<LostPlanCommitResponseException>(
                () => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
            test.AfterBackupResponse = null;
            Assert.Equal(1, test.Invocations);
            Assert.Equal(BackupInvocationStatus.Running,
                (await ReadAttemptAsync(platform, taskId)).Status);
            test.RestrictedTerminationProbe = new FixedTerminationProbe(
                TargetSqlInvocationTerminationState.Active, "termination.original_session_present");
            await test.ExpireLeaseAsync(taskId);
            Assert.Equal(BackupExecutionContractCode.Succeeded,
                await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
            int metadataReads;
            Guid attemptId;
            DateTimeOffset? successTime;
            await using (var db = platform.CreateContext())
            {
                var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId);
                attemptId = attempt.Id;
                successTime = attempt.BackupFinishedAtUtc;
                Assert.NotNull(successTime);
                Assert.Equal(BackupInvocationStatus.Succeeded, attempt.BackupInvocationStatus);
                metadataReads = test.MetadataReads;
                var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
                Assert.Equal(BackupTaskStatus.NeedsAttention, task.Status);
                Assert.Equal("sql.recovery_original_call_active", task.ErrorCode);
            }

            // 原调用终止后仍沿原 Attempt 完成，不重新读取已经冻结的成功证据，也不重写完成时间。
            test.RestrictedTerminationProbe = new FixedTerminationProbe(
                TargetSqlInvocationTerminationState.Terminated,
                BackupInvocationTerminationEvidenceRules.OriginalConnectionClosed);
            await test.ExpireLeaseAsync(taskId);
            Assert.Equal(BackupExecutionContractCode.Succeeded,
                await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
            Assert.Equal(metadataReads, test.MetadataReads);
            await using (var db = platform.CreateContext())
            {
                var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
                Assert.Equal(BackupTaskStatus.Pending, task.Status);
                Assert.Equal(BackupTaskStage.VerifyLocal, task.CurrentStage);
                var attempt = await db.BackupAttempts.SingleAsync(x => x.Id == attemptId);
                Assert.Equal(successTime, attempt.BackupFinishedAtUtc);
                var authorization = await db.BackupInvocationAuthorizations.SingleAsync(x => x.AttemptId == attemptId);
                Assert.Equal(BackupInvocationTerminationKind.RecoveredTerminated, authorization.TerminationKind);
            }
            Assert.Equal(BackupExecutionContractCode.Succeeded,
                await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
            await test.AssertCompletedAsync(taskId, BackupType.Full);
            Assert.Equal(1, test.Invocations);
            await using var finalDb = platform.CreateContext();
            Assert.Equal(attemptId, (await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId)).CurrentBackupAttemptId);
            Assert.Equal(successTime, (await finalDb.BackupAttempts.SingleAsync(x => x.Id == attemptId)).BackupFinishedAtUtc);
        }
        finally { await platform.DisposeAsync(); }
    }

    [Theory]
    [InlineData(nameof(IBackupInvocationAuthorizationStore.RecordTerminationAsync))]
    [InlineData(nameof(IBackupPlanExecutionStore.CloseUnknownAsync))]
    public async Task LostTerminationOrClosureCommitResponseRemainsIdempotent(string lostMethod)
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            var fault = new PlanExecutionCommitFault(lostMethod) { Armed = false };
            await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, fault);
            var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
            var prepared = await AuthorizeWithoutInvocationAsync(platform, test, taskId, knownIdentity: true);
            await test.ExpireLeaseAsync(taskId);
            fault.Armed = true;
            await Assert.ThrowsAsync<LostPlanCommitResponseException>(
                () => test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
            Assert.True(fault.Fired);
            await test.ExpireLeaseAsync(taskId);
            var replay = await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid()));
            // 终止响应丢失时任务仍待核对，第二次核对收口；收口响应丢失时任务已经收口，重复核对返回冲突。
            Assert.True(replay is BackupExecutionContractCode.Succeeded or BackupExecutionContractCode.Conflict,
                replay.ToString());
            // 终止只提交一次；丢失响应后的再次核对不再重新探测。
            Assert.Equal(1, test.TerminationReads);
            await using (var db = platform.CreateContext())
            {
                var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
                Assert.Equal(BackupTaskStatus.ClosedUnknown, task.Status);
                Assert.Equal(prepared.AttemptId, task.CurrentBackupAttemptId);
                Assert.Equal(1, await db.BackupAttempts.CountAsync(x => x.TaskId == taskId));
                Assert.Equal(1, await db.NotificationOutbox.CountAsync(
                    x => x.TaskId == taskId && x.Type == NotificationType.TaskIndeterminate));
                Assert.Equal(0, await db.NotificationOutbox.CountAsync(
                    x => x.TaskId == taskId && x.Type == NotificationType.TaskFailed));
                Assert.Single(await db.BackupTaskStateChanges.Where(x => x.TaskId == taskId
                    && x.ReasonCode == "plan.recovery.close_unknown").ToArrayAsync());
            }
            int history;
            int notifications;
            await using (var db = platform.CreateContext())
            {
                history = await db.BackupTaskStateChanges.CountAsync(x => x.TaskId == taskId);
                notifications = await db.NotificationOutbox.CountAsync(x => x.TaskId == taskId);
            }
            Assert.Equal(BackupExecutionContractCode.Conflict,
                await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
            await using (var db = platform.CreateContext())
            {
                Assert.Equal(history, await db.BackupTaskStateChanges.CountAsync(x => x.TaskId == taskId));
                Assert.Equal(notifications, await db.NotificationOutbox.CountAsync(x => x.TaskId == taskId));
            }
            Assert.Equal(0, test.Invocations);
        }
        finally { await platform.DisposeAsync(); }
    }

    [Fact]
    public async Task FailureBeforeClosureCommitRetriesWithoutDuplicatingHistoryOrNotification()
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            var failure = new FailClosedUnknownSaveOnce();
            await using var test = await PlanExecutionTestEnvironment.CreateAsync(
                platform, BackupStorageMode.LocalOnly, null, failure);
            var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
            var prepared = await AuthorizeWithoutInvocationAsync(platform, test, taskId, knownIdentity: true);
            await test.ExpireLeaseAsync(taskId);
            await Assert.ThrowsAsync<IOException>(
                () => test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
            Assert.True(failure.Fired);
            await test.ExpireLeaseAsync(taskId);
            await using (var db = platform.CreateContext())
            {
                // 终止证据已经独立提交；收口事务失败后任务仍待核对。
                Assert.Equal(BackupTaskStatus.NeedsAttention,
                    (await db.BackupTasks.SingleAsync(x => x.Id == taskId)).Status);
                Assert.NotNull((await db.BackupInvocationAuthorizations.SingleAsync(x => x.AttemptId == prepared.AttemptId)).TerminalObservedAtUtc);
            }
            Assert.Equal(BackupExecutionContractCode.Succeeded,
                await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
            Assert.Equal(1, test.TerminationReads);
            await using var finalDb = platform.CreateContext();
            Assert.Equal(BackupTaskStatus.ClosedUnknown,
                (await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId)).Status);
            Assert.Single(await finalDb.BackupTaskStateChanges.Where(x => x.TaskId == taskId
                && x.ReasonCode == "plan.recovery.close_unknown").ToArrayAsync());
            Assert.Equal(1, await finalDb.NotificationOutbox.CountAsync(
                x => x.TaskId == taskId && x.Type == NotificationType.TaskIndeterminate));
        }
        finally { await platform.DisposeAsync(); }
    }

    [Fact]
    public async Task RecoveryTerminationWithoutSuccessEvidenceIsAcceptedButUnknownOrActiveFactsAreRejected()
    {
        var platform = new PlatformDatabaseSqlServerFixture();
        try
        {
            await platform.InitializeAsync();
            var work = await BackupSetTestData.WorkAsync(platform);
            var factory = new BackupExecutionContractSqlServerTests.Factory(platform);
            var coordinator = new BackupInvocationAuthorizationStore(factory, TimeProvider.System);
            var binding = new BackupInvocationBinding
            {
                State = BackupInvocationBindingState.Known,
                CallerIncarnationId = Guid.NewGuid(),
                SessionId = 4100,
                SessionEstablishedLocal = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Unspecified),
                ConnectionId = Guid.NewGuid()
            };
            var command = await ExecutionContractTestData.AuthorizationAsync(platform, work);
            var grant = await coordinator.AuthorizeAsync(work.Lease, command with { Binding = binding });
            Assert.Equal(BackupExecutionContractCode.Succeeded, grant.Code);
            var operations = new BackupExecutionOperationStore(factory, TimeProvider.System);
            var identity = ExecutionContractTestData.Identity(work) with
            {
                Kind = BackupExecutionOperationKind.Recovery,
                Sequence = 1
            };
            await operations.ReserveAsync(grant.Value!.Lease!, identity);
            var observedAt = DateTimeOffset.UtcNow;
            var facts = new BackupExecutionFacts
            {
                Outcome = BackupExecutionOutcome.Unknown,
                SqlOutcomeSource = BackupSqlOutcomeSource.RecoveredEvidence,
                EvidenceAtUtc = observedAt,
                OriginalCallTerminated = true,
                OriginalCallerCannotInvoke = true,
                ReasonCode = "sql.recovery_metadata_insufficient"
            };
            var termination = new BackupExecutionObservationInput(0, BackupExecutionObservationSource.Termination,
                Domain.BackupSets.BackupSetEvidenceKind.Backup,
                new()
                {
                    OriginalCallTerminated = true,
                    OriginalCallerCannotInvoke = true,
                    ReasonCode = BackupInvocationTerminationEvidenceRules.OriginalConnectionClosed
                });
            await operations.FreezeAsync(grant.Value.Lease!, new(identity, facts, [termination]));

            // 无成功备份头/msdb 也可以独立记录终止；但不能声称成功或明确失败。
            var accepted = new TerminateBackupInvocation(command.PermitId, identity.MutationId, identity.MutationId,
                BackupInvocationTerminationKind.RecoveredTerminated, observedAt, true, true);
            Assert.Equal(BackupExecutionContractCode.Succeeded,
                (await coordinator.RecordTerminationAsync(grant.Value.Lease!, accepted)).Code);
            Assert.Equal(BackupExecutionContractCode.AlreadyApplied,
                (await coordinator.RecordTerminationAsync(grant.Value.Lease!, accepted)).Code);
            Assert.Equal(BackupExecutionContractCode.Conflict,
                (await coordinator.RecordTerminationAsync(grant.Value.Lease!,
                    accepted with { MutationId = Guid.NewGuid() })).Code);
            var saved = await coordinator.ReadAsync(command.PermitId);
            Assert.Equal(BackupInvocationTerminationKind.RecoveredTerminated, saved.Value!.TerminationKind);
            Assert.Equal(identity.MutationId, saved.Value.TerminationMutationId);

            // 权限不足/读取未知的观察即使被伪造成终止事实，存储层也拒绝解除。
            var other = await BackupSetTestData.WorkAsync(platform, work.Snapshot.Identity.DatabaseId);
            var otherCommand = await ExecutionContractTestData.AuthorizationAsync(platform, other);
            var otherGrant = await coordinator.AuthorizeAsync(other.Lease, otherCommand with { Binding = binding });
            Assert.Equal(BackupExecutionContractCode.Succeeded, otherGrant.Code);
            var otherIdentity = ExecutionContractTestData.Identity(other) with { Kind = BackupExecutionOperationKind.Recovery };
            await operations.ReserveAsync(otherGrant.Value!.Lease!, otherIdentity);
            var forged = facts with { Outcome = BackupExecutionOutcome.Succeeded, SqlSuccessObserved = true };
            await operations.FreezeAsync(otherGrant.Value!.Lease!, new(otherIdentity, forged,
                [termination with { Facts = termination.Facts with { ReasonCode = "termination.permission_denied" } }]));
            Assert.Equal(BackupExecutionContractCode.Conflict,
                (await coordinator.RecordTerminationAsync(otherGrant.Value!.Lease!,
                    new(otherCommand.PermitId, otherIdentity.MutationId, otherIdentity.MutationId,
                        BackupInvocationTerminationKind.RecoveredTerminated, observedAt, true, true))).Code);
        }
        finally { await platform.DisposeAsync(); }
    }

    private static async Task<AuthorizedWithoutInvocation> AuthorizeWithoutInvocationAsync(
        PlatformDatabaseSqlServerFixture platform, PlanExecutionTestEnvironment test, Guid taskId, bool knownIdentity)
    {
        var now = DateTimeOffset.UtcNow;
        var opened = await test.Store.OpenAsync(taskId, Guid.NewGuid(), false, Guid.NewGuid(), "合成预授权",
            now, now.AddMinutes(2));
        Assert.Equal(BackupExecutionContractCode.Succeeded, opened.Code);
        var session = opened.Value!;
        var sql = session.Operations.Single(x => x.Identity.Kind == BackupExecutionOperationKind.SqlResult);
        Guid attemptId;
        byte[] rowVersion;
        await using (var db = platform.CreateContext())
        {
            var attempt = await db.BackupAttempts.AsTracking().SingleAsync(x => x.TaskId == taskId);
            attempt.BindSqlIdentity(Guid.NewGuid(), Guid.NewGuid());
            await db.SaveChangesAsync();
            attemptId = attempt.Id;
            rowVersion = attempt.RowVersion;
        }
        var binding = new BackupInvocationBinding
        {
            State = knownIdentity ? BackupInvocationBindingState.Known : BackupInvocationBindingState.Unknown,
            CallerIncarnationId = Guid.NewGuid(),
            SessionId = knownIdentity ? 4200 : null,
            SessionEstablishedLocal = knownIdentity
                ? new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Unspecified) : null,
            ConnectionId = knownIdentity ? Guid.NewGuid() : null
        };
        var permit = Guid.NewGuid();
        var store = new BackupInvocationAuthorizationStore(
            new BackupExecutionContractSqlServerTests.Factory(platform), TimeProvider.System);
        var grant = await store.AuthorizeAsync(session.Work.Lease,
            new(permit, permit, sql.Identity.MutationId, test.DatabaseId, binding, now, rowVersion));
        Assert.Equal(BackupExecutionContractCode.Succeeded, grant.Code);
        return new(attemptId, permit);
    }

    private static async Task<(Guid Id, BackupInvocationStatus Status)> ReadAttemptAsync(
        PlatformDatabaseSqlServerFixture platform, Guid taskId)
    {
        await using var db = platform.CreateContext();
        var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId);
        return (attempt.Id, attempt.BackupInvocationStatus);
    }

    private sealed record AuthorizedWithoutInvocation(Guid AttemptId, Guid PermitId);

    private sealed class FixedTerminationProbe(TargetSqlInvocationTerminationState state, string reason)
        : ITargetSqlInvocationTerminationProbe
    {
        public Task<TargetSqlInvocationTerminationEvidence> ReadAsync(TargetSqlConnectionInput connection,
            Guid invocationPermitId, BackupInvocationBinding originalBinding, int timeoutSeconds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TargetSqlInvocationTerminationEvidence(state, originalBinding, DateTimeOffset.UtcNow, reason));
    }

    private sealed class FailClosedUnknownSaveOnce : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<BackupTask>().Any(entry =>
                    entry.State == EntityState.Modified && entry.Entity.Status == BackupTaskStatus.ClosedUnknown))
            {
                Fired = true;
                throw new IOException("合成收口提交前失败");
            }

            return ValueTask.FromResult(result);
        }
    }
}
