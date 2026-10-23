using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupPlanExecutionRecoveryLocalDbTests(PlatformDatabaseSqlServerFixture platform)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Theory]
    [InlineData("OpenAsync", null)]
    [InlineData("ReserveAsync", "Admission")]
    [InlineData("FreezeAsync", "Admission")]
    [InlineData("AuthorizeAsync", null)]
    [InlineData("FreezeAsync", "SqlResult")]
    [InlineData("ApplyAsync", "SqlResult")]
    [InlineData("ReserveAsync", "Metadata")]
    [InlineData("FreezeAsync", "Metadata")]
    [InlineData("ApplyAsync", "Metadata")]
    [InlineData("ReserveAsync", "VerifyLocal")]
    [InlineData("FreezeAsync", "VerifyLocal")]
    [InlineData("ApplyAsync", "VerifyLocal")]
    [InlineData("ReserveAsync", "Transfer")]
    [InlineData("FreezeAsync", "Transfer")]
    [InlineData("ApplyAsync", "Transfer")]
    [InlineData("ReserveAsync", "ValidateCopy")]
    [InlineData("FreezeAsync", "ValidateCopy")]
    [InlineData("ApplyAsync", "ValidateCopy")]
    [InlineData("TransferVerifiedAsync", null)]
    [InlineData("RenameAndVerifyAsync", null)]
    public async Task LostCommitResponseUsesOriginalAttemptAndReadOnlyRecovery(string point, string? kind)
    {
        // 授权响应丢失时 BACKUP 从未发出，原连接在异常展开时关闭。终止探测不依赖成功元数据：
        // 任务以结果未知收口，不再永久保留未终止授权阻塞其他库的认领。
        if (point != "AuthorizeAsync") { await RunLostCommitResponseAsync(platform, point, kind); return; }
        var isolated = new PlatformDatabaseSqlServerFixture();
        try { await isolated.InitializeAsync(); await RunLostCommitResponseAsync(isolated, point, kind); }
        finally { await isolated.DisposeAsync(); }
    }

    private static async Task RunLostCommitResponseAsync(PlatformDatabaseSqlServerFixture platform, string point, string? kind)
    {
        var fault = new PlanExecutionCommitFault(point, kind);
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalAndRemote, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        var request = new RunBackupPlanTaskRequest(taskId, Guid.NewGuid());
        await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(request));
        Assert.True(fault.Fired);
        var originalCalls = test.Invocations;
        Guid attemptId;
        await using (var db = platform.CreateContext())
        {
            attemptId = (await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId)).Id;
            var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
            if (task.Status == BackupTaskStatus.Running)
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [BackupTasks] SET [LeaseAcquiredAtUtc] = {DateTimeOffset.UtcNow.AddMinutes(-5)}, [LeaseExpiresAtUtc] = {DateTimeOffset.UtcNow.AddSeconds(-1)} WHERE [Id] = {taskId}");
        }
        Assert.Equal(BackupExecutionContractCode.AlreadyApplied, await test.Runner.RunTaskOnceAsync(request));
        BackupTaskStatus recoveryStatus;
        await using (var db = platform.CreateContext())
        {
            var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
            recoveryStatus = task.Status;
        }
        if (recoveryStatus != BackupTaskStatus.Succeeded)
        {
            var writes = test.PlanWrites;
            await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid()));
            Assert.Equal(writes, test.PlanWrites);
        }
        Assert.Equal(originalCalls, test.Invocations);
        await using (var db = platform.CreateContext())
        {
            var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
            Assert.Equal(attemptId, task.CurrentBackupAttemptId);
            Assert.Equal(1, await db.BackupAttempts.CountAsync(x => x.TaskId == taskId));
            if (point == "AuthorizeAsync")
            {
                // 缺失文件/msdb 不能反推未调用；已证明终止但无成功证据时以 ClosedUnknown 收口。
                Assert.Equal(BackupTaskStatus.ClosedUnknown, task.Status);
                Assert.Equal("sql.recovery_result_unknown_closed", task.ErrorCode);
                Assert.NotNull((await db.BackupInvocationAuthorizations.SingleAsync(x => x.AttemptId == attemptId)).TerminalObservedAtUtc);
                Assert.Equal(0, originalCalls);
            }
            recoveryStatus = task.Status;
        }
        if (recoveryStatus == BackupTaskStatus.Pending)
            await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid()));
        if (point != "AuthorizeAsync" && !(point == "ReserveAsync" && kind is "Transfer" or "ValidateCopy"))
        {
            await test.AssertCompletedAsync(taskId, BackupType.Full);
            Assert.Equal(1, test.Invocations);
        }
        Assert.Equal(0, test.PlanDeletions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqlCompletedBeforeFreezeIsRecoveredWithoutInventingPlatformTimeOrCallingBackup(bool loseLease)
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        test.AfterBackupResponse = loseLease ? () => test.ExpireLeaseAsync(taskId)
            : () => throw new LostPlanCommitResponseException();
        if (loseLease) Assert.Equal(BackupExecutionContractCode.LeaseLost, await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        else await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        test.AfterBackupResponse = null;
        Guid originalAttempt;
        await using (var before = platform.CreateContext())
        {
            // SQL 已成功，但冻结提交前失租或丢失响应；成功事实尚未写入，后续只能只读收回原 Attempt。
            var attempt = await before.BackupAttempts.SingleAsync(x => x.TaskId == taskId);
            originalAttempt = attempt.Id;
            var sql = await before.BackupPlanExecutionOperations.SingleAsync(x => x.AttemptId == attempt.Id && x.Kind == BackupExecutionOperationKind.SqlResult);
            Assert.Equal(BackupExecutionOperationState.Reserved, sql.State);
            Assert.Null(sql.Facts.PlatformCompletedAtUtc);
            Assert.False(await before.BackupPlanExecutionOperations.AnyAsync(x => x.AttemptId == attempt.Id && x.Kind == BackupExecutionOperationKind.Recovery));
        }
        await test.ExpireLeaseAsync(taskId);
        Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
        Assert.Equal(1, test.Invocations);
        await using (var db = platform.CreateContext())
        {
            var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId);
            Assert.Equal(BackupInvocationStatus.Succeeded, attempt.BackupInvocationStatus);
            var sql = await db.BackupPlanExecutionOperations.SingleAsync(x => x.AttemptId == attempt.Id && x.Kind == BackupExecutionOperationKind.SqlResult);
            Assert.Equal(BackupExecutionOperationState.Reserved, sql.State);
            Assert.Null(sql.Facts.PlatformCompletedAtUtc);
            var recovered = await db.BackupPlanExecutionOperations.SingleAsync(x => x.AttemptId == attempt.Id && x.Kind == BackupExecutionOperationKind.Recovery);
            Assert.Equal(BackupSqlOutcomeSource.RecoveredEvidence, recovered.Facts.SqlOutcomeSource);
            Assert.Null(recovered.Facts.PlatformCompletedAtUtc);
            Assert.Equal(BackupTaskStatus.Pending, (await db.BackupTasks.SingleAsync(x => x.Id == taskId)).Status);
        }
        await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid()));
        Assert.Equal(1, test.Invocations);
        await using var finalDb = platform.CreateContext();
        var completed = await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId);
        Assert.Equal(BackupTaskStatus.Succeeded, completed.Status);
        Assert.Equal(originalAttempt, completed.CurrentBackupAttemptId);
        Assert.Equal(originalAttempt, (await finalDb.BackupAttempts.SingleAsync(x => x.TaskId == taskId)).Id);
        Assert.Equal(0, test.PlanDeletions);
    }

    [Theory]
    [InlineData("OpenAsync", null)]
    [InlineData("ReserveAsync", "Recovery")]
    [InlineData("FreezeAsync", "Recovery")]
    [InlineData("RecordTerminationAsync", null)]
    [InlineData("ApplyAsync", "Recovery")]
    public async Task LostReadOnlyRecoveryCommitResponseReplaysOriginalFactsWithoutBackup(string method, string? kind)
    {
        var fault = new PlanExecutionCommitFault(method, kind) { Armed = false };
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        test.AfterBackupResponse = () => throw new LostPlanCommitResponseException();
        await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        test.AfterBackupResponse = null;
        fault.Armed = true;
        await test.ExpireLeaseAsync(taskId);
        var writes = test.PlanWrites;
        await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid())));
        Assert.True(fault.Fired);
        BackupTaskStatus status;
        await using (var db = platform.CreateContext()) status = (await db.BackupTasks.SingleAsync(x => x.Id == taskId)).Status;
        if (status == BackupTaskStatus.NeedsAttention)
        {
            await test.ExpireLeaseAsync(taskId);
            await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid()));
        }
        Assert.Equal(writes, test.PlanWrites);
        Assert.Equal(1, test.Invocations);
        await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid()));
        await test.AssertCompletedAsync(taskId, BackupType.Full);
        await using var finalDb = platform.CreateContext();
        Assert.Equal(1, await finalDb.BackupAttempts.CountAsync(x => x.TaskId == taskId));
        var original = await finalDb.BackupPlanExecutionOperations.SingleAsync(x => x.TaskId == taskId && x.Kind == BackupExecutionOperationKind.SqlResult);
        Assert.Equal(BackupExecutionOperationState.Reserved, original.State);
        Assert.Null(original.Facts.PlatformCompletedAtUtc);
        Assert.NotNull((await finalDb.BackupInvocationAuthorizations.SingleAsync(x => x.TaskId == taskId)).TerminalObservedAtUtc);
        Assert.Equal(0, test.PlanDeletions);
    }

    [Theory]
    [InlineData("VerifyLocal", "content")]
    [InlineData("Transfer", "content")]
    [InlineData("ValidateCopy", "content")]
    [InlineData("VerifyLocal", "object")]
    [InlineData("Transfer", "object")]
    [InlineData("ValidateCopy", "object")]
    [InlineData("ValidateCopy", "both_paths")]
    [InlineData("Transfer", "incomplete")]
    public async Task ChangedOriginalObjectsRemainProtectedAndCannotBecomeAvailable(string kind, string change)
    {
        var fault = new PlanExecutionCommitFault("FreezeAsync", kind);
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalAndRemote, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        string path;
        string? part;
        await using (var db = platform.CreateContext())
        {
            var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId);
            path = kind == "VerifyLocal" ? attempt.WorkerSourceFilePath : kind == "Transfer" ? attempt.RemotePartialFilePath! : attempt.RemoteFinalFilePath!;
            part = attempt.RemotePartialFilePath;
        }
        var bytes = await File.ReadAllBytesAsync(path);
        switch (change)
        {
            case "both_paths": await File.WriteAllBytesAsync(part!, bytes); break;
            case "object":
                File.Move(path, path + ".original");
                await File.WriteAllBytesAsync(path, bytes);
                break;
            case "incomplete": await File.WriteAllBytesAsync(path, bytes.AsMemory(0, bytes.Length / 2).ToArray()); break;
            default:
                bytes[0] ^= 0xff;
                await File.WriteAllBytesAsync(path, bytes);
                break;
        }
        await test.ExpireLeaseAsync(taskId);
        var writes = test.PlanWrites;
        await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid()));
        Assert.Equal(writes, test.PlanWrites);
        Assert.Equal(1, test.Invocations);
        Assert.True(File.Exists(path));
        await using var finalDb = platform.CreateContext();
        Assert.Equal(BackupTaskStatus.NeedsAttention, (await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId)).Status);
        Assert.Equal(BackupInvocationStatus.Succeeded, (await finalDb.BackupAttempts.SingleAsync(x => x.TaskId == taskId)).BackupInvocationStatus);
        Assert.Equal(kind == "VerifyLocal" ? 0 : 1, await finalDb.BackupFiles.CountAsync(x => x.TaskId == taskId));
        Assert.Equal(0, test.PlanDeletions);
    }

    [Theory]
    [InlineData(BackupExecutionOperationKind.SqlResult, BackupTaskStage.Backup, 0, 0)]
    [InlineData(BackupExecutionOperationKind.Metadata, BackupTaskStage.VerifyLocal, 0, 0)]
    [InlineData(BackupExecutionOperationKind.VerifyLocal, BackupTaskStage.VerifyLocal, 1, 0)]
    [InlineData(BackupExecutionOperationKind.Transfer, BackupTaskStage.Transfer, 1, 1)]
    [InlineData(BackupExecutionOperationKind.ValidateCopy, BackupTaskStage.ValidateCopy, 1, 1)]
    public async Task StageTransactionFailureRollsBackSetCopiesHistoryAndAppliedReceipt(BackupExecutionOperationKind kind,
        BackupTaskStage stage, int sets, int copies)
    {
        var failure = new BeforeStageSaveFailure(kind);
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalAndRemote, null, failure);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        await Assert.ThrowsAsync<IOException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        Assert.True(failure.Fired);
        await using (var db = platform.CreateContext())
        {
            var operation = await db.BackupPlanExecutionOperations.SingleAsync(x => x.TaskId == taskId && x.Kind == kind);
            Assert.Equal(BackupExecutionOperationState.Frozen, operation.State);
            Assert.Equal(stage, (await db.BackupTasks.SingleAsync(x => x.Id == taskId)).CurrentStage);
            Assert.False(await db.BackupTaskStateChanges.AnyAsync(x => x.MutationId == operation.Id));
            Assert.Equal(sets, await db.BackupSets.CountAsync(x => x.TaskId == taskId));
            Assert.Equal(copies, await db.BackupFiles.CountAsync(x => x.TaskId == taskId));
            if (sets == 0) Assert.False(await db.BackupSetEvidence.AnyAsync(x => x.TaskId == taskId));
        }
        await test.ExpireLeaseAsync(taskId);
        var writes = test.PlanWrites;
        await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid()));
        Assert.Equal(writes, test.PlanWrites);
        BackupTaskStatus nextStatus;
        await using (var db = platform.CreateContext())
        {
            var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
            nextStatus = task.Status;
        }
        if (nextStatus == BackupTaskStatus.Pending) await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid()));
        await test.AssertCompletedAsync(taskId, BackupType.Full);
        Assert.Equal(1, test.Invocations);
        Assert.Equal(0, test.PlanDeletions);
    }

    [Fact]
    public async Task SqlSuccessSurvivesUnknownMetadataAndAnExplicitNewReadOnlyObservationCanCompleteIt()
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
        test.MetadataUnavailable = true;
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid()));
        await using (var db = platform.CreateContext())
        {
            Assert.Equal(BackupInvocationStatus.Succeeded, (await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId)).BackupInvocationStatus);
            Assert.True((await db.BackupSets.SingleAsync(x => x.TaskId == taskId)).SqlSuccessObserved);
            Assert.Equal(BackupTaskStatus.NeedsAttention, (await db.BackupTasks.SingleAsync(x => x.Id == taskId)).Status);
            Assert.False(await db.BackupFiles.AnyAsync(x => x.TaskId == taskId));
        }
        test.MetadataUnavailable = false;
        await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid()));
        await test.AssertCompletedAsync(taskId, BackupType.Full);
        Assert.Equal(1, test.Invocations);
        Assert.Equal(0, test.PlanDeletions);
    }

    [Fact]
    public async Task HostCancellationAfterSqlSuccessPreservesOriginalFactAndFile()
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
        using var cancelled = new CancellationTokenSource();
        test.AfterBackupResponse = () => cancelled.CancelAsync();
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid()), cancelled.Token));
        await using (var db = platform.CreateContext())
        {
            var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId);
            Assert.Equal(BackupInvocationStatus.Succeeded, attempt.BackupInvocationStatus);
            Assert.True(File.Exists(attempt.LocalSqlFilePath));
            Assert.Equal(BackupTaskStatus.NeedsAttention, (await db.BackupTasks.SingleAsync(x => x.Id == taskId)).Status);
        }
        await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid()));
        await test.AssertCompletedAsync(taskId, BackupType.Full);
        Assert.Equal(1, test.Invocations);
    }

    [Fact]
    public async Task FrozenMetadataReplayDoesNotReadSqlAgainOrDuplicateRegistration()
    {
        var fault = new PlanExecutionCommitFault("FreezeAsync", "Metadata");
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        var reads = test.MetadataReads;
        test.MetadataUnavailable = true;
        await test.ExpireLeaseAsync(taskId);
        var request = new ReconcileBackupPlanTaskRequest(taskId, Guid.NewGuid());
        await test.Reconciler.ReconcilePlanTaskOnceAsync(request);
        Assert.Equal(reads, test.MetadataReads);
        await test.AssertCompletedAsync(taskId, BackupType.Full);
        int evidence;
        int events;
        int history;
        await using (var db = platform.CreateContext())
        {
            evidence = await db.BackupSetEvidence.CountAsync(x => x.TaskId == taskId);
            events = await db.TaskEvents.CountAsync(x => x.TaskId == taskId);
            history = await db.BackupTaskStateChanges.CountAsync(x => x.TaskId == taskId);
        }
        Assert.Equal(BackupExecutionContractCode.AlreadyApplied, await test.Reconciler.ReconcilePlanTaskOnceAsync(request));
        await using var finalDb = platform.CreateContext();
        Assert.Equal(evidence, await finalDb.BackupSetEvidence.CountAsync(x => x.TaskId == taskId));
        Assert.Equal(events, await finalDb.TaskEvents.CountAsync(x => x.TaskId == taskId));
        Assert.Equal(history, await finalDb.BackupTaskStateChanges.CountAsync(x => x.TaskId == taskId));
        Assert.Equal(1, test.Invocations);
    }

    [Theory]
    [InlineData("VerifyLocal")]
    [InlineData("ValidateCopy")]
    public async Task RealSourceAndFinalProtectionRejectReplacementInFreezeToCommitWindow(string kind)
    {
        string? source = null, final = null;
        var fault = new PlanExecutionCommitFault("FreezeAsync", kind);
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalAndRemote, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        test.AfterBackupResponse = async () =>
        {
            await using var db = platform.CreateContext();
            var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId);
            source = attempt.WorkerSourceFilePath;
            final = attempt.RemoteFinalFilePath;
        };
        fault.BeforeThrow = () =>
        {
            var path = kind == "VerifyLocal" ? source! : final!;
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, [1, 2, 3, 4]));
            Assert.Throws<IOException>(() => File.Move(path, path + ".replaced"));
        };
        await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        await test.ExpireLeaseAsync(taskId);
        await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid()));
        if (kind == "VerifyLocal") await test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid()));
        await test.AssertCompletedAsync(taskId, BackupType.Full);
        Assert.Equal(1, test.Invocations);
        Assert.Equal(0, test.PlanDeletions);
    }

    [Fact]
    public async Task LostProtectionPayloadConflictAndLostLeaseCannotRegisterFrozenCopy()
    {
        var fault = new PlanExecutionCommitFault("FreezeAsync", "VerifyLocal");
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        LeaseHandle lease;
        FrozenBackupExecutionOperation frozen;
        BackupFileEndpointInput endpoint;
        string path;
        await using (var db = platform.CreateContext())
        {
            lease = (await db.BackupTasks.SingleAsync(x => x.Id == taskId)).ToLeaseHandle();
            var operation = await db.BackupPlanExecutionOperations.SingleAsync(x => x.TaskId == taskId && x.Kind == BackupExecutionOperationKind.VerifyLocal);
            frozen = await BackupExecutionOperationStore.DetachAsync(db, operation, CancellationToken.None);
            var snapshot = (await db.BackupTaskSnapshots.SingleAsync(x => x.TaskId == taskId)).ToSnapshotModel();
            endpoint = BackupPlanExecutionStore.Endpoint(snapshot.WorkerSourceEndpoint);
            path = (await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId)).WorkerSourceFilePath;
        }
        var operationStore = new BackupExecutionOperationStore(new BackupExecutionContractSqlServerTests.Factory(platform), TimeProvider.System);
        Assert.Equal(BackupExecutionContractCode.Conflict,
            (await operationStore.FreezeAsync(lease, new(frozen.Identity, frozen.Facts with
            { Content = frozen.Facts.Content with { DigestHex = new string('a', 64) } }, frozen.Observations))).Code);
        var verified = await test.FileVerifier.OpenVerifiedAsync(new(taskId, lease.BackupAttemptId, endpoint, path, 30, frozen.Facts.Content));
        Assert.True(verified.IsSucceeded);
        var handle = verified.Value!;
        await handle.DisposeAsync();
        Assert.Equal(BackupExecutionContractCode.Conflict, await test.Store.ApplyAsync(lease, frozen.Identity, handle));
        await test.ExpireLeaseAsync(taskId);
        Assert.Equal(BackupExecutionContractCode.LeaseLost, await test.Store.ApplyAsync(lease, frozen.Identity, handle));
        await using (var db = platform.CreateContext()) Assert.False(await db.BackupFiles.AnyAsync(x => x.TaskId == taskId));
        await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid()));
        await test.AssertCompletedAsync(taskId, BackupType.Full);
        Assert.Equal(1, test.Invocations);
    }

    [Fact]
    public async Task AnotherValidBackupCannotReplaceSourceBetweenMetadataAndLocalVerification()
    {
        var fault = new PlanExecutionCommitFault("FreezeAsync", "Metadata");
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        string source;
        await using (var db = platform.CreateContext()) source = (await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId)).WorkerSourceFilePath;
        var otherPath = test.NewSqlPath();
        var prepared = await test.SqlAdapter().PrepareAsync(test.SqlInput,
            new TargetSqlBackupRequest(test.TargetDatabaseName, otherPath, BackupRunPurpose.PlanFull, true, false, 60), Guid.NewGuid());
        Assert.True(prepared.IsSucceeded);
        await using (var invocation = prepared.Value!) Assert.True((await invocation.InvokeOnceAsync(Guid.NewGuid())).IsSucceeded);
        File.Delete(source);
        File.Copy(otherPath, source);
        await test.ExpireLeaseAsync(taskId);
        var reads = test.MetadataReads;
        await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid()));
        Assert.Equal(reads, test.MetadataReads);
        Assert.Equal(1, test.Invocations);
        await using var finalDb = platform.CreateContext();
        Assert.Equal(BackupTaskStatus.NeedsAttention, (await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId)).Status);
        Assert.False(await finalDb.BackupFiles.AnyAsync(x => x.TaskId == taskId));
        Assert.Equal(BackupInvocationStatus.Succeeded, (await finalDb.BackupAttempts.SingleAsync(x => x.TaskId == taskId)).BackupInvocationStatus);
        Assert.True(File.Exists(source));
        Assert.Equal(0, test.PlanDeletions);
    }

    [Fact]
    public async Task CommitPortRejectsSourceDifferentFromAppliedMetadataEvenWithLiveProtection()
    {
        var fault = new PlanExecutionCommitFault("ApplyAsync", "Metadata");
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        LeaseHandle lease;
        Guid setId;
        BackupFileEndpointInput endpoint;
        string path;
        await using (var db = platform.CreateContext())
        {
            lease = (await db.BackupTasks.SingleAsync(x => x.Id == taskId)).ToLeaseHandle();
            setId = (await db.BackupSets.SingleAsync(x => x.TaskId == taskId)).Id;
            endpoint = BackupPlanExecutionStore.Endpoint((await db.BackupTaskSnapshots.SingleAsync(x => x.TaskId == taskId))
                .ToSnapshotModel().WorkerSourceEndpoint);
            path = (await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId)).WorkerSourceFilePath;
        }
        File.Delete(path);
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        var read = await test.FileVerifier.OpenVerifiedAsync(new(taskId, lease.BackupAttemptId, endpoint, path, 30));
        Assert.True(read.IsSucceeded);
        await using var handle = read.Value!;
        var identity = new ReserveBackupExecutionOperation(Guid.NewGuid(), taskId, lease.BackupAttemptId,
            BackupExecutionOperationKind.VerifyLocal, 1, setId, DateTimeOffset.UtcNow);
        var operations = new BackupExecutionOperationStore(new BackupExecutionContractSqlServerTests.Factory(platform), TimeProvider.System);
        Assert.Equal(BackupExecutionContractCode.Succeeded, (await operations.ReserveAsync(lease, identity)).Code);
        Assert.Equal(BackupExecutionContractCode.Succeeded, (await operations.FreezeAsync(lease, new(identity,
            new()
            {
                Outcome = BackupExecutionOutcome.Succeeded,
                EvidenceAtUtc = DateTimeOffset.UtcNow,
                SqlSuccessObserved = true,
                Content = handle.Content
            }, []))).Code);
        Assert.Equal(BackupExecutionContractCode.Conflict, await test.Store.ApplyAsync(lease, identity, handle));
        await using var finalDb = platform.CreateContext();
        Assert.False(await finalDb.BackupFiles.AnyAsync(x => x.TaskId == taskId));
        Assert.Null((await finalDb.BackupAttempts.SingleAsync(x => x.TaskId == taskId)).LocalVerifiedAtUtc);
        Assert.Equal(BackupExecutionOperationState.Frozen,
            (await finalDb.BackupPlanExecutionOperations.SingleAsync(x => x.Id == identity.MutationId)).State);
    }

    [Fact]
    public async Task MetadataApplyPersistsFrozenSourceTimesAndConflictingReplay()
    {
        var fault = new PlanExecutionCommitFault("ApplyAsync", "Metadata");
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));

        LeaseHandle lease;
        ReserveBackupExecutionOperation identity;
        BackupFileEndpointInput endpoint;
        string path;
        Dictionary<int, DateTimeOffset?> frozenTimes;
        await using (var db = platform.CreateContext())
        {
            var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
            lease = task.ToLeaseHandle();
            var operation = await db.BackupPlanExecutionOperations.AsNoTracking()
                .SingleAsync(x => x.TaskId == taskId && x.Kind == BackupExecutionOperationKind.Metadata);
            identity = new(operation.Id, operation.TaskId, operation.AttemptId, operation.Kind,
                operation.Sequence, operation.IntendedBackupSetId, operation.ObservedAtUtc);
            endpoint = BackupPlanExecutionStore.Endpoint((await db.BackupTaskSnapshots.SingleAsync(x => x.TaskId == taskId))
                .ToSnapshotModel().WorkerSourceEndpoint);
            path = (await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId)).WorkerSourceFilePath;
            frozenTimes = await db.BackupPlanExecutionObservations.AsNoTracking()
                .Where(x => x.OperationId == operation.Id && x.Facts.EvidenceAtUtc != null)
                .ToDictionaryAsync(x => x.EntryNumber, x => x.Facts.EvidenceAtUtc);
        }

        var read = await test.FileVerifier.OpenVerifiedAsync(new(taskId, lease.BackupAttemptId, endpoint, path, 30));
        Assert.True(read.IsSucceeded);
        await using var handle = read.Value!;
        var apply = await test.Store.ApplyAsync(lease, identity, handle);
        Assert.True(apply is BackupExecutionContractCode.Succeeded or BackupExecutionContractCode.AlreadyApplied, apply.ToString());

        await using (var db = platform.CreateContext())
        {
            var evidence = await db.BackupSetEvidence.AsNoTracking()
                .Where(x => x.AttemptId == lease.BackupAttemptId
                    && (x.Source == BackupSetEvidenceSource.BackupHeader || x.Source == BackupSetEvidenceSource.Msdb))
                .ToArrayAsync();
            Assert.NotEmpty(evidence);
            Assert.All(evidence, row => Assert.NotNull(row.SourceObservedAtUtc));
            Assert.All(evidence, row => Assert.Contains(row.SourceObservedAtUtc!.Value, frozenTimes.Values.Select(x => x!.Value)));
            Assert.Contains(evidence, row => row.Source == BackupSetEvidenceSource.BackupHeader
                && row.SourceObservedAtUtc == frozenTimes[0]);
            Assert.Contains(evidence, row => row.Source == BackupSetEvidenceSource.Msdb
                && row.SourceObservedAtUtc == frozenTimes[1]);
        }

        Assert.Equal(BackupExecutionContractCode.AlreadyApplied, await test.Store.ApplyAsync(lease, identity, handle));
        var operations = new BackupExecutionOperationStore(new BackupExecutionContractSqlServerTests.Factory(platform), TimeProvider.System);
        var saved = await operations.ReadAsync(identity);
        Assert.Equal(BackupExecutionContractCode.AlreadyApplied, saved.Code);
        var changed = saved.Value!.Observations.Select(x => x.EntryNumber == 0
            ? x with { Facts = x.Facts with { EvidenceAtUtc = x.Facts.EvidenceAtUtc?.AddSeconds(1) } }
            : x).ToArray();
        var conflict = await operations.FreezeAsync(lease,
            new(identity, saved.Value.Facts, changed));
        Assert.Equal(BackupExecutionContractCode.Conflict, conflict.Code);
    }


    [Fact]
    public async Task CancellationBeforeAuthorizationCannotGrantOrInvokeAndReadOnlyRecoveryCancelsPreparedTask()
    {
        var fault = new PlanExecutionCommitFault("FreezeAsync", "Admission");
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly, fault);
        var taskId = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        fault.BeforeThrow = () =>
        {
            using var db = platform.CreateContext();
            db.BackupTasks.AsTracking().Single(x => x.Id == taskId).RequestCancellation(DateTimeOffset.UtcNow, BackupStorageMode.LocalOnly);
            db.SaveChanges();
        };
        await Assert.ThrowsAsync<LostPlanCommitResponseException>(() => test.Runner.RunTaskOnceAsync(new(taskId, Guid.NewGuid())));
        LeaseHandle lease;
        Guid sqlOperation;
        byte[] rowVersion;
        Guid databaseId;
        await using (var db = platform.CreateContext())
        {
            lease = (await db.BackupTasks.SingleAsync(x => x.Id == taskId)).ToLeaseHandle();
            sqlOperation = (await db.BackupPlanExecutionOperations.SingleAsync(x => x.TaskId == taskId && x.Kind == BackupExecutionOperationKind.SqlResult)).Id;
            rowVersion = (await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId)).RowVersion;
            databaseId = (await db.BackupTaskSnapshots.SingleAsync(x => x.TaskId == taskId)).DatabaseId;
        }
        var authorizations = new BackupInvocationAuthorizationStore(new BackupExecutionContractSqlServerTests.Factory(platform), TimeProvider.System);
        var binding = new BackupInvocationBinding
        {
            State = BackupInvocationBindingState.Known,
            CallerIncarnationId = Guid.NewGuid(),
            SessionId = 77,
            SessionEstablishedLocal = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified),
            ConnectionId = Guid.NewGuid()
        };
        Assert.Equal(BackupExecutionContractCode.Conflict, (await authorizations.AuthorizeAsync(lease,
            new(sqlOperation, sqlOperation, sqlOperation, databaseId, binding, DateTimeOffset.UtcNow, rowVersion))).Code);
        await test.ExpireLeaseAsync(taskId);
        await test.Reconciler.ReconcilePlanTaskOnceAsync(new(taskId, Guid.NewGuid()));
        await using var finalDb = platform.CreateContext();
        Assert.Equal(BackupTaskStatus.Cancelled, (await finalDb.BackupTasks.SingleAsync(x => x.Id == taskId)).Status);
        Assert.Equal(1, await finalDb.BackupAttempts.CountAsync(x => x.TaskId == taskId));
        Assert.False(await finalDb.BackupInvocationAuthorizations.AnyAsync(x => x.TaskId == taskId));
        Assert.Equal(0, test.Invocations);
        Assert.Equal(0, test.PlanDeletions);
    }

    private sealed class BeforeStageSaveFailure(BackupExecutionOperationKind kind) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<BackupPlanExecutionOperation>()
                .Any(x => x.State == EntityState.Modified && x.Entity.Kind == kind && x.Entity.State == BackupExecutionOperationState.Applied))
            { Fired = true; throw new IOException("合成阶段保存失败"); }
            return ValueTask.FromResult(result);
        }
    }
}
