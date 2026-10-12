using System.Threading.Channels;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.Tests;

public sealed class BackupPlanHeartbeatTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TransientRenewalFailureDoesNotCancelIoAndDisposeKeepsTheCommittedCode()
    {
        var ticks = Channel.CreateUnbounded<byte>();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RenewStore(() =>
        {
            observed.TrySetResult();
            throw new InvalidOperationException("瞬时续租失败");
        });
        var code = BackupExecutionContractCode.Succeeded;
        await using (var heartbeat = Heartbeat(store, Now.Add(BackupPlanHeartbeat.LeaseDuration), ticks))
        {
            await ticks.Writer.WriteAsync(0);
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(heartbeat.Token.IsCancellationRequested);
            Assert.IsType<InvalidOperationException>(heartbeat.LastFault);
            code = BackupExecutionContractCode.Succeeded;
        }
        Assert.Equal(BackupExecutionContractCode.Succeeded, code);
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task NonSuccessRenewalRetriesUntilALaterSuccessAndDoesNotCancel()
    {
        var ticks = Channel.CreateUnbounded<byte>();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var store = new RenewStore(() =>
        {
            if (++calls == 1) return BackupExecutionContractCode.Conflict;
            second.TrySetResult();
            return BackupExecutionContractCode.Succeeded;
        });
        await using var heartbeat = Heartbeat(store, Now.Add(BackupPlanHeartbeat.LeaseDuration), ticks);
        await ticks.Writer.WriteAsync(0);
        await ticks.Writer.WriteAsync(1);
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(heartbeat.Token.IsCancellationRequested);
        Assert.Null(heartbeat.LastFault);
        Assert.Equal(2, store.Calls);
        Assert.All(store.RequestedExpiries, expiry => Assert.Equal(Now.Add(BackupPlanHeartbeat.LeaseDuration), expiry));
    }

    [Fact]
    public async Task ConfirmedLeaseLossCancelsIoEvenWhenTheLocalExpiryStillHasSlack()
    {
        var ticks = Channel.CreateUnbounded<byte>();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RenewStore(() =>
        {
            observed.TrySetResult();
            return BackupExecutionContractCode.LeaseLost;
        });
        await using var heartbeat = Heartbeat(store, Now.Add(BackupPlanHeartbeat.LeaseDuration), ticks);
        await ticks.Writer.WriteAsync(0);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(heartbeat.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task RenewalFailureCancelsOnlyInsideTheNearExpiryWindow(int slackSeconds, bool cancel)
    {
        var ticks = Channel.CreateUnbounded<byte>();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RenewStore(() =>
        {
            observed.TrySetResult();
            return BackupExecutionContractCode.Conflict;
        });
        var expires = Now.Add(BackupPlanHeartbeat.NearExpiry).AddSeconds(slackSeconds);
        await using var heartbeat = Heartbeat(store, expires, ticks);
        await ticks.Writer.WriteAsync(0);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(cancel, heartbeat.Token.IsCancellationRequested);
    }

    private static BackupPlanHeartbeat Heartbeat(RenewStore store, DateTimeOffset expires, Channel<byte> ticks) =>
        new(store, new Frozen(Now), Guid.NewGuid(), Guid.NewGuid(), expires, CancellationToken.None,
            token => ticks.Reader.ReadAsync(token).AsTask());

    private sealed class Frozen(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RenewStore(Func<BackupExecutionContractCode> next) : IBackupPlanExecutionStore
    {
        public int Calls { get; private set; }
        public List<DateTimeOffset> RequestedExpiries { get; } = [];
        public Task<BackupExecutionContractCode> RenewAsync(Guid taskId, Guid leaseToken, DateTimeOffset expires, CancellationToken cancellationToken = default)
        {
            Calls++;
            RequestedExpiries.Add(expires);
            return Task.FromResult(next());
        }
        public Task<BackupExecutionContractResult<BackupPlanExecutionSession>> OpenAsync(Guid taskId, Guid requestMutationId, bool reconciliationOnly, Guid leaseToken, string owner, DateTimeOffset now, DateTimeOffset expires, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupPlanExecutionSession?> RefreshAsync(Guid taskId, Guid leaseToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QualifiedManagedFull?> FindQualifiedFullAsync(Guid databaseId, Guid backupSetGuid, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupExecutionContractCode> ApplyAsync(LeaseHandle lease, ReserveBackupExecutionOperation identity, DbBackupManager.Application.FileStorage.IVerifiedBackupFileHandle? protectedFile = null, DbBackupManager.Application.FileStorage.IVerifiedBackupFileHandle? protectedDependency = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupExecutionContractCode> SuspendAsync(LeaseHandle lease, Guid mutationId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupExecutionContractCode> CompleteRemoteCleanupAsync(LeaseHandle lease, bool retained, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupExecutionContractCode> CloseDeferredRemoteCleanupAsync(Guid taskId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

public sealed class BackupPlanExecutionOutcomeTests
{
    [Fact]
    public async Task UnexpectedExceptionSuspendsWithAuditReasonAndStillPropagates()
    {
        var store = new ScriptStore(Session(BackupTaskStage.Backup, BackupStorageMode.LocalOnly), []);
        var runner = Runner(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunTaskOnceAsync(new(store.TaskId, Guid.NewGuid())));
        Assert.Equal("plan.unexpected_error", store.SuspendReason);
    }

    [Fact]
    public async Task UnexpectedExceptionSurvivesWhenSuspendItselfFails()
    {
        var store = new ScriptStore(Session(BackupTaskStage.Backup, BackupStorageMode.LocalOnly), []) { FailSuspend = true };
        var runner = Runner(store);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunTaskOnceAsync(new(store.TaskId, Guid.NewGuid())));
        Assert.NotEqual("暂停读取失败", exception.Message);
        Assert.Null(store.SuspendReason);
    }

    [Fact]
    public async Task UnobservedCommitResponseLeavesTheLiveLeaseAndDoesNotSuspend()
    {
        var session = Session(BackupTaskStage.Backup, BackupStorageMode.LocalOnly, frozenSql: true);
        var store = new ScriptStore(session, []) { UnobservedApply = true };
        var runner = Runner(store);
        await Assert.ThrowsAsync<PlanExecutionUnobservedCommitException>(() => runner.RunTaskOnceAsync(new(store.TaskId, Guid.NewGuid())));
        Assert.Null(store.SuspendReason);
    }

    [Fact]
    public async Task LostLeaseAfterNonFinalStageReturnsLeaseLostInsteadOfSuccess()
    {
        var session = Session(BackupTaskStage.Backup, BackupStorageMode.LocalOnly, frozenSql: true);
        var store = new ScriptStore(session, [session, null]);
        var runner = Runner(store);
        Assert.Equal(BackupExecutionContractCode.LeaseLost, await runner.RunTaskOnceAsync(new(store.TaskId, Guid.NewGuid())));
        Assert.Null(store.SuspendReason);
    }

    [Fact]
    public async Task CompletedFinalStageStillReturnsSuccessWhenTheLeaseIsReleased()
    {
        var session = Session(BackupTaskStage.VerifyLocal, BackupStorageMode.LocalOnly);
        var released = Session(BackupTaskStage.Cleanup, BackupStorageMode.LocalOnly);
        var store = new ScriptStore(session, [released, null]);
        var runner = Runner(store);
        Assert.Equal(BackupExecutionContractCode.Succeeded, await runner.RunTaskOnceAsync(new(store.TaskId, Guid.NewGuid())));
    }

    [Fact]
    public async Task CancellationAfterLeaseLossReturnsLeaseLost()
    {
        var session = Session(BackupTaskStage.VerifyLocal, BackupStorageMode.LocalAndRemote);
        var store = new ScriptStore(session, [null]) { CancelRefresh = true };
        var runner = Runner(store);
        Assert.Equal(BackupExecutionContractCode.LeaseLost, await runner.RunTaskOnceAsync(new(store.TaskId, Guid.NewGuid())));
        Assert.Null(store.SuspendReason);
    }

    [Fact]
    public async Task RecoveryCancellationAfterLeaseLossReturnsLeaseLost()
    {
        var session = Session(BackupTaskStage.Backup, BackupStorageMode.LocalOnly);
        var store = new ScriptStore(session, [null]) { CancelRefresh = true };
        var stages = new BackupPlanReadOnlyStages(store, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System);
        var reconciler = new BackupPlanTaskReconciler(store, stages, TimeProvider.System);
        Assert.Equal(BackupExecutionContractCode.LeaseLost, await reconciler.ReconcilePlanTaskOnceAsync(new(store.TaskId, Guid.NewGuid())));
        Assert.Null(store.SuspendReason);
    }

    [Fact]
    public async Task CancellationWhileLeaseIsHeldSuspendsAndRethrows()
    {
        var session = Session(BackupTaskStage.VerifyLocal, BackupStorageMode.LocalAndRemote);
        var store = new ScriptStore(session, [session]) { CancelRefresh = true };
        var runner = Runner(store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunTaskOnceAsync(new(store.TaskId, Guid.NewGuid())));
        Assert.Equal("plan.interrupted", store.SuspendReason);
    }

    [Fact]
    public void RejectedObservationSequenceUsesTheHighestExistingSequence()
    {
        var operations = new[]
        {
            Operation(BackupExecutionOperationKind.Admission, 1),
            Operation(BackupExecutionOperationKind.Admission, 3),
            Operation(BackupExecutionOperationKind.SqlResult, 1)
        };
        Assert.Equal(4, BackupPlanOperationFlow.NextSequence(operations, BackupExecutionOperationKind.Admission));
        Assert.Equal(2, BackupPlanOperationFlow.NextSequence(operations, BackupExecutionOperationKind.SqlResult));
        Assert.Equal(1, BackupPlanOperationFlow.NextSequence(operations, BackupExecutionOperationKind.Recovery));
    }

    private static BackupPlanTaskRunner Runner(ScriptStore store)
    {
        var stages = new BackupPlanReadOnlyStages(store, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System);
        return new(store, null!, null!, null!, null!, null!, null!, null!, null!, stages, null!, TimeProvider.System);
    }

    private static FrozenBackupExecutionOperation Operation(BackupExecutionOperationKind kind, int sequence) =>
        new(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), kind, sequence, Guid.NewGuid(), DateTimeOffset.UtcNow),
            BackupExecutionOperationState.Abandoned, new(), []);

    private static BackupPlanExecutionSession Session(BackupTaskStage stage, BackupStorageMode mode, bool frozenSql = false)
    {
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var endpoint = new BackupFileEndpointModel(FileTransferProtocol.Smb, "localhost", null, @"C:\synthetic", Guid.NewGuid(), null);
        var policy = new BackupTaskPolicyModel(mode, null, null, 7, null, true, false, false, 30, 30, 30, "UTC");
        var snapshot = new BackupTaskSnapshotModel(taskId, "合成计划", new(Guid.NewGuid(), "server", Guid.NewGuid(), "instance", Guid.NewGuid(), "synthetic"),
            new("synthetic", Guid.NewGuid(), true, true, "合成", 30), @"C:\synthetic", "v3", endpoint, policy, BackupType.Full, BackupRunPurpose.PlanFull);
        var attempt = new BackupAttemptModel(attemptId, 1, BackupInvocationStatus.Prepared, @"C:\synthetic\a.bak", @"C:\synthetic\a.bak",
            null, null, null, null, null, null, null, [1]);
        var lease = new LeaseHandle(taskId, Guid.NewGuid(), BackupLeasePurpose.Execution, stage, attemptId, now.AddMinutes(2), [1]);
        var task = new BackupTaskStateModel(taskId, BackupTaskStatus.Running, stage, attemptId, null, 0, null, null, now, null);
        var operations = frozenSql
            ? new[] { new FrozenBackupExecutionOperation(new(Guid.NewGuid(), taskId, attemptId, BackupExecutionOperationKind.SqlResult, 1, Guid.NewGuid(), now), BackupExecutionOperationState.Frozen, new() { Outcome = BackupExecutionOutcome.Succeeded, SqlSuccessObserved = true }, []) }
            : Array.Empty<FrozenBackupExecutionOperation>();
        return new(new(task, snapshot, attempt, lease), operations, null);
    }

    private sealed class ScriptStore(BackupPlanExecutionSession opened, BackupPlanExecutionSession?[] refreshes) : IBackupPlanExecutionStore
    {
        private int _refresh;
        public Guid TaskId => opened.Work.Task.TaskId;
        public string? SuspendReason { get; private set; }
        public bool FailSuspend { get; init; }
        public bool CancelRefresh { get; init; }
        public bool UnobservedApply { get; init; }
        public Task<BackupExecutionContractResult<BackupPlanExecutionSession>> OpenAsync(Guid taskId, Guid requestMutationId, bool reconciliationOnly, Guid leaseToken, string owner, DateTimeOffset now, DateTimeOffset expires, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupExecutionContractResult<BackupPlanExecutionSession>(BackupExecutionContractCode.Succeeded, opened));
        public Task<BackupPlanExecutionSession?> RefreshAsync(Guid taskId, Guid leaseToken, CancellationToken cancellationToken = default)
        {
            if (CancelRefresh && cancellationToken.CanBeCanceled) throw new OperationCanceledException(cancellationToken);
            if (FailSuspend) throw new InvalidOperationException("暂停读取失败");
            var session = _refresh < refreshes.Length ? refreshes[_refresh] : opened;
            _refresh++;
            return Task.FromResult(session);
        }
        public Task<BackupExecutionContractCode> RenewAsync(Guid taskId, Guid leaseToken, DateTimeOffset expires, CancellationToken cancellationToken = default) =>
            Task.FromResult(BackupExecutionContractCode.Succeeded);
        public Task<QualifiedManagedFull?> FindQualifiedFullAsync(Guid databaseId, Guid backupSetGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult<QualifiedManagedFull?>(null);
        public Task<BackupExecutionContractCode> ApplyAsync(LeaseHandle lease, ReserveBackupExecutionOperation identity, DbBackupManager.Application.FileStorage.IVerifiedBackupFileHandle? protectedFile = null, DbBackupManager.Application.FileStorage.IVerifiedBackupFileHandle? protectedDependency = null, CancellationToken cancellationToken = default)
        {
            if (UnobservedApply) throw new PlanExecutionUnobservedCommitException();
            return Task.FromResult(BackupExecutionContractCode.Succeeded);
        }
        public Task<BackupExecutionContractCode> SuspendAsync(LeaseHandle lease, Guid mutationId, string reason, CancellationToken cancellationToken = default)
        {
            SuspendReason = reason;
            return Task.FromResult(BackupExecutionContractCode.Succeeded);
        }
        public Task<BackupExecutionContractCode> CompleteRemoteCleanupAsync(LeaseHandle lease, bool retained, CancellationToken cancellationToken = default) =>
            Task.FromResult(BackupExecutionContractCode.Succeeded);
        public Task<BackupExecutionContractCode> CloseDeferredRemoteCleanupAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult(BackupExecutionContractCode.Succeeded);
    }
}
