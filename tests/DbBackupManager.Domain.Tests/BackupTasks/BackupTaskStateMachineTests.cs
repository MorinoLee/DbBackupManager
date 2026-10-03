using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Domain.Tests.BackupTasks;

public sealed class BackupTaskStateMachineTests
{
    public static TheoryData<BackupStorageMode, BackupTaskStage[]> StagePaths => new()
    {
        {
            BackupStorageMode.LocalOnly,
            [BackupTaskStage.Backup, BackupTaskStage.VerifyLocal]
        },
        {
            BackupStorageMode.LocalAndRemote,
            [
                BackupTaskStage.Backup,
                BackupTaskStage.VerifyLocal,
                BackupTaskStage.Transfer,
                BackupTaskStage.ValidateCopy
            ]
        },
        {
            BackupStorageMode.RemoteOnly,
            [
                BackupTaskStage.Backup,
                BackupTaskStage.VerifyLocal,
                BackupTaskStage.Transfer,
                BackupTaskStage.ValidateCopy,
                BackupTaskStage.Cleanup
            ]
        }
    };

    [Theory]
    [MemberData(nameof(StagePaths))]
    public void RequiredStagesMatchStorageMode(
        BackupStorageMode storageMode,
        BackupTaskStage[] expectedStages)
    {
        var stages = BackupTaskStateMachine.GetRequiredStages(storageMode);

        Assert.Equal(expectedStages, stages);
    }

    [Theory]
    [MemberData(nameof(StagePaths))]
    public void SuccessfulFlowTraversesEveryRequiredStage(
        BackupStorageMode storageMode,
        BackupTaskStage[] expectedStages)
    {
        var state = BackupTaskStateMachine.CreatePending(storageMode);
        state = BackupTaskStateMachine.Claim(state, storageMode);

        foreach (var expectedStage in expectedStages)
        {
            Assert.Equal(BackupTaskStatus.Running, state.Status);
            Assert.Equal(expectedStage, state.CurrentStage);
            state = BackupTaskStateMachine.CompleteRunningStage(state, storageMode);
        }

        Assert.Equal(BackupTaskStatus.Succeeded, state.Status);
        Assert.Equal(expectedStages[^1], state.CurrentStage);
        BackupTaskStateMachine.EnsureValid(state, storageMode);
    }

    [Theory]
    [InlineData(BackupStorageMode.LocalOnly, BackupTaskStage.Transfer)]
    [InlineData(BackupStorageMode.LocalOnly, BackupTaskStage.Cleanup)]
    [InlineData(BackupStorageMode.LocalAndRemote, BackupTaskStage.Cleanup)]
    public void StageOutsideStorageModePathIsRejected(
        BackupStorageMode storageMode,
        BackupTaskStage stage)
    {
        var state = new BackupTaskExecutionState(BackupTaskStatus.Pending, stage);

        Assert.Throws<InvalidOperationException>(() =>
            BackupTaskStateMachine.EnsureValid(state, storageMode));
    }

    [Fact]
    public void SucceededStateMustUseFinalStageForStorageMode()
    {
        var state = new BackupTaskExecutionState(
            BackupTaskStatus.Succeeded,
            BackupTaskStage.VerifyLocal);

        Assert.Throws<InvalidOperationException>(() =>
            BackupTaskStateMachine.EnsureValid(state, BackupStorageMode.RemoteOnly));
    }

    [Fact]
    public void OnlyPendingTaskCanBeClaimed()
    {
        var state = new BackupTaskExecutionState(
            BackupTaskStatus.Running,
            BackupTaskStage.Backup);

        Assert.Throws<InvalidOperationException>(() =>
            BackupTaskStateMachine.Claim(state, BackupStorageMode.LocalOnly));
    }

    [Theory]
    [InlineData(BackupTaskStage.Backup)]
    [InlineData(BackupTaskStage.VerifyLocal)]
    [InlineData(BackupTaskStage.Transfer)]
    [InlineData(BackupTaskStage.ValidateCopy)]
    [InlineData(BackupTaskStage.Cleanup)]
    public void ConfirmedFailurePreservesCurrentStage(BackupTaskStage stage)
    {
        var running = RunningRemoteOnly(stage);

        var failed = BackupTaskStateMachine.RecordConfirmedFailure(
            running,
            BackupStorageMode.RemoteOnly);

        Assert.Equal(BackupTaskStatus.Failed, failed.Status);
        Assert.Equal(stage, failed.CurrentStage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndeterminateResultAndExpiredLeaseRequireReconciliation(bool leaseExpired)
    {
        var running = RunningRemoteOnly(BackupTaskStage.Backup);

        var needsAttention = leaseExpired
            ? BackupTaskStateMachine.RecordExpiredLease(running, BackupStorageMode.RemoteOnly)
            : BackupTaskStateMachine.RecordIndeterminateResult(running, BackupStorageMode.RemoteOnly);

        Assert.Equal(BackupTaskStatus.NeedsAttention, needsAttention.Status);
        Assert.Equal(BackupTaskStage.Backup, needsAttention.CurrentStage);
    }

    [Fact]
    public void SuccessfulReconciliationAdvancesToPendingNextStage()
    {
        var needsAttention = new BackupTaskExecutionState(
            BackupTaskStatus.NeedsAttention,
            BackupTaskStage.Backup);

        var reconciled = BackupTaskStateMachine.ReconcileSucceeded(
            needsAttention,
            BackupStorageMode.RemoteOnly);

        Assert.Equal(BackupTaskStatus.Pending, reconciled.Status);
        Assert.Equal(BackupTaskStage.VerifyLocal, reconciled.CurrentStage);
    }

    [Fact]
    public void SuccessfulReconciliationAtFinalStageCompletesTask()
    {
        var needsAttention = new BackupTaskExecutionState(
            BackupTaskStatus.NeedsAttention,
            BackupTaskStage.Cleanup);

        var reconciled = BackupTaskStateMachine.ReconcileSucceeded(
            needsAttention,
            BackupStorageMode.RemoteOnly);

        Assert.Equal(BackupTaskStatus.Succeeded, reconciled.Status);
        Assert.Equal(BackupTaskStage.Cleanup, reconciled.CurrentStage);
    }

    [Theory]
    [InlineData(BackupTaskStage.Backup, BackupAttemptHandling.CreateNew)]
    [InlineData(BackupTaskStage.Transfer, BackupAttemptHandling.ReuseExisting)]
    [InlineData(BackupTaskStage.ValidateCopy, BackupAttemptHandling.ReuseExisting)]
    public void SafeRetryReconciliationDoesNotAdvanceStageAndPreservesAttemptBoundary(
        BackupTaskStage stage,
        BackupAttemptHandling expectedAttemptHandling)
    {
        var needsAttention = new BackupTaskExecutionState(
            BackupTaskStatus.NeedsAttention,
            stage);

        var reconciled = BackupTaskStateMachine.ReconcileSafeToRetry(
            needsAttention,
            BackupStorageMode.RemoteOnly);

        Assert.Equal(BackupTaskStatus.Pending, reconciled.State.Status);
        Assert.Equal(stage, reconciled.State.CurrentStage);
        Assert.Equal(expectedAttemptHandling, reconciled.AttemptHandling);
    }

    [Fact]
    public void ReconciliationCanConfirmFailureOrSafeCancellation()
    {
        var needsAttention = new BackupTaskExecutionState(
            BackupTaskStatus.NeedsAttention,
            BackupTaskStage.ValidateCopy);

        var failed = BackupTaskStateMachine.ReconcileFailed(
            needsAttention,
            BackupStorageMode.RemoteOnly);
        var cancelled = BackupTaskStateMachine.ReconcileCancelled(
            needsAttention,
            BackupStorageMode.RemoteOnly);

        Assert.Equal(BackupTaskStatus.Failed, failed.Status);
        Assert.Equal(BackupTaskStatus.Cancelled, cancelled.Status);
        Assert.Equal(needsAttention.CurrentStage, failed.CurrentStage);
        Assert.Equal(needsAttention.CurrentStage, cancelled.CurrentStage);
    }

    [Theory]
    [InlineData(BackupStorageMode.LocalOnly, BackupTaskStage.Backup)]
    [InlineData(BackupStorageMode.LocalOnly, BackupTaskStage.VerifyLocal)]
    [InlineData(BackupStorageMode.LocalAndRemote, BackupTaskStage.Backup)]
    [InlineData(BackupStorageMode.RemoteOnly, BackupTaskStage.VerifyLocal)]
    public void InconclusiveReconciliationKeepsOriginalNeedsAttentionState(
        BackupStorageMode storageMode,
        BackupTaskStage stage)
    {
        var state = new BackupTaskExecutionState(
            BackupTaskStatus.NeedsAttention,
            stage);

        var result = BackupTaskStateMachine.ReconcileInconclusive(state, storageMode);

        Assert.Equal(state, result);
    }

    [Theory]
    [InlineData(BackupTaskStatus.Pending, BackupTaskStage.Backup)]
    [InlineData(BackupTaskStatus.Running, BackupTaskStage.Backup)]
    [InlineData(BackupTaskStatus.Failed, BackupTaskStage.Backup)]
    [InlineData(BackupTaskStatus.Succeeded, BackupTaskStage.VerifyLocal)]
    [InlineData(BackupTaskStatus.Cancelled, BackupTaskStage.Backup)]
    public void InconclusiveReconciliationRejectsNonNeedsAttention(
        BackupTaskStatus status,
        BackupTaskStage stage)
    {
        var state = new BackupTaskExecutionState(status, stage);

        Assert.Throws<InvalidOperationException>(() =>
            BackupTaskStateMachine.ReconcileInconclusive(state, BackupStorageMode.LocalOnly));
    }

    [Fact]
    public void PendingTaskAndRunningTaskCanCancelOnlyThroughTheirSafeBoundaries()
    {
        var pending = BackupTaskStateMachine.CreatePending(BackupStorageMode.LocalOnly);
        var running = BackupTaskStateMachine.Claim(pending, BackupStorageMode.LocalOnly);

        var cancelledBeforeStart = BackupTaskStateMachine.CancelPending(
            pending,
            BackupStorageMode.LocalOnly);
        var cancelledAtSafeBoundary = BackupTaskStateMachine.CancelRunningAtSafeBoundary(
            running,
            BackupStorageMode.LocalOnly);

        Assert.Equal(BackupTaskStatus.Cancelled, cancelledBeforeStart.Status);
        Assert.Equal(BackupTaskStage.Backup, cancelledBeforeStart.CurrentStage);
        Assert.Equal(BackupTaskStatus.Cancelled, cancelledAtSafeBoundary.Status);
        Assert.Equal(BackupTaskStage.Backup, cancelledAtSafeBoundary.CurrentStage);
    }

    [Theory]
    [InlineData(BackupTaskStage.Backup, BackupTaskStage.Backup, BackupAttemptHandling.CreateNew)]
    [InlineData(
        BackupTaskStage.VerifyLocal,
        BackupTaskStage.VerifyLocal,
        BackupAttemptHandling.ReuseExisting)]
    [InlineData(BackupTaskStage.Transfer, BackupTaskStage.Transfer, BackupAttemptHandling.ReuseExisting)]
    [InlineData(
        BackupTaskStage.ValidateCopy,
        BackupTaskStage.Transfer,
        BackupAttemptHandling.ReuseExisting)]
    [InlineData(BackupTaskStage.Cleanup, BackupTaskStage.Cleanup, BackupAttemptHandling.ReuseExisting)]
    public void FailedRetryUsesSafeStageAndAttemptHandling(
        BackupTaskStage failedStage,
        BackupTaskStage expectedRetryStage,
        BackupAttemptHandling expectedAttemptHandling)
    {
        var failed = new BackupTaskExecutionState(BackupTaskStatus.Failed, failedStage);

        var retry = BackupTaskStateMachine.RetryFailed(failed, BackupStorageMode.RemoteOnly);

        Assert.Equal(BackupTaskStatus.Pending, retry.State.Status);
        Assert.Equal(expectedRetryStage, retry.State.CurrentStage);
        Assert.Equal(expectedAttemptHandling, retry.AttemptHandling);
    }

    [Theory]
    [InlineData(BackupTaskStatus.Succeeded)]
    [InlineData(BackupTaskStatus.Cancelled)]
    public void TerminalTaskCannotBeClaimedOrRetried(BackupTaskStatus terminalStatus)
    {
        var stage = terminalStatus == BackupTaskStatus.Succeeded
            ? BackupTaskStage.VerifyLocal
            : BackupTaskStage.Backup;
        var terminal = new BackupTaskExecutionState(terminalStatus, stage);

        Assert.Throws<InvalidOperationException>(() =>
            BackupTaskStateMachine.Claim(terminal, BackupStorageMode.LocalOnly));
        Assert.Throws<InvalidOperationException>(() =>
            BackupTaskStateMachine.RetryFailed(terminal, BackupStorageMode.LocalOnly));
    }

    [Fact]
    public void CancelledBeforeStartMayHaveNoCurrentStage()
    {
        var cancelled = new BackupTaskExecutionState(BackupTaskStatus.Cancelled, null);

        BackupTaskStateMachine.EnsureValid(cancelled, BackupStorageMode.LocalOnly);
    }

    [Fact]
    public void NonCancelledStateRequiresCurrentStage()
    {
        Assert.Throws<ArgumentException>(() =>
            new BackupTaskExecutionState(BackupTaskStatus.Pending, null));
    }

    [Fact]
    public void UndefinedStorageModeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BackupTaskStateMachine.CreatePending((BackupStorageMode)999));
    }

    private static BackupTaskExecutionState RunningRemoteOnly(BackupTaskStage stage)
    {
        var state = new BackupTaskExecutionState(BackupTaskStatus.Running, stage);
        BackupTaskStateMachine.EnsureValid(state, BackupStorageMode.RemoteOnly);
        return state;
    }
}
