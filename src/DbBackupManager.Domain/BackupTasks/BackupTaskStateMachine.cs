namespace DbBackupManager.Domain.BackupTasks;

public static class BackupTaskStateMachine
{
    private static readonly BackupTaskStage[] LocalOnlyStages =
    [
        BackupTaskStage.Backup,
        BackupTaskStage.VerifyLocal
    ];

    private static readonly BackupTaskStage[] LocalAndRemoteStages =
    [
        BackupTaskStage.Backup,
        BackupTaskStage.VerifyLocal,
        BackupTaskStage.Transfer,
        BackupTaskStage.ValidateCopy
    ];

    private static readonly BackupTaskStage[] RemoteOnlyStages =
    [
        BackupTaskStage.Backup,
        BackupTaskStage.VerifyLocal,
        BackupTaskStage.Transfer,
        BackupTaskStage.ValidateCopy,
        BackupTaskStage.Cleanup
    ];

    public static BackupTaskExecutionState CreatePending(BackupStorageMode storageMode)
    {
        EnsureStorageMode(storageMode);
        return new BackupTaskExecutionState(BackupTaskStatus.Pending, BackupTaskStage.Backup);
    }

    public static IReadOnlyList<BackupTaskStage> GetRequiredStages(BackupStorageMode storageMode)
    {
        return [.. GetStagePath(storageMode)];
    }

    public static void EnsureValid(
        BackupTaskExecutionState state,
        BackupStorageMode storageMode)
    {
        var stagePath = GetStagePath(storageMode);

        if (state.CurrentStage is null)
        {
            if (state.Status != BackupTaskStatus.Cancelled)
            {
                throw new InvalidOperationException("当前任务状态必须记录阶段。");
            }

            return;
        }

        if (Array.IndexOf(stagePath, state.CurrentStage.Value) < 0)
        {
            throw new InvalidOperationException("当前阶段不属于该存储模式的执行路径。");
        }

        if (state.Status == BackupTaskStatus.Succeeded
            && state.CurrentStage != stagePath[^1])
        {
            throw new InvalidOperationException("成功任务必须停留在该存储模式的最后一个阶段。");
        }
    }

    public static BackupTaskExecutionState Claim(
        BackupTaskExecutionState state,
        BackupStorageMode storageMode)
    {
        EnsureState(state, storageMode, BackupTaskStatus.Pending);
        return new BackupTaskExecutionState(BackupTaskStatus.Running, state.CurrentStage);
    }

    public static BackupTaskExecutionState CompleteRunningStage(
        BackupTaskExecutionState state,
        BackupStorageMode storageMode)
    {
        EnsureState(state, storageMode, BackupTaskStatus.Running);

        var stagePath = GetStagePath(storageMode);
        var currentIndex = Array.IndexOf(stagePath, state.CurrentStage!.Value);

        return currentIndex == stagePath.Length - 1
            ? new BackupTaskExecutionState(BackupTaskStatus.Succeeded, state.CurrentStage)
            : new BackupTaskExecutionState(BackupTaskStatus.Running, stagePath[currentIndex + 1]);
    }

    public static BackupTaskExecutionState RecordConfirmedFailure(
        BackupTaskExecutionState state,
        BackupStorageMode storageMode)
    {
        EnsureState(state, storageMode, BackupTaskStatus.Running);
        return new BackupTaskExecutionState(BackupTaskStatus.Failed, state.CurrentStage);
    }

    public static BackupTaskExecutionState RejectPendingBackup(
        BackupTaskExecutionState state,
        BackupStorageMode storageMode)
    {
        EnsureState(state, storageMode, BackupTaskStatus.Pending);
        if (state.CurrentStage != BackupTaskStage.Backup)
        {
            throw new InvalidOperationException("只有尚未认领的 Backup 阶段可以记录准备失败。");
        }

        return new BackupTaskExecutionState(BackupTaskStatus.Failed, BackupTaskStage.Backup);
    }

    public static BackupTaskExecutionState RecordIndeterminateResult(
        BackupTaskExecutionState state,
        BackupStorageMode storageMode)
    {
        EnsureState(state, storageMode, BackupTaskStatus.Running);
        return new BackupTaskExecutionState(BackupTaskStatus.NeedsAttention, state.CurrentStage);
    }

    public static BackupTaskExecutionState RecordExpiredLease(
        BackupTaskExecutionState state,
        BackupStorageMode storageMode)
    {
        return RecordIndeterminateResult(state, storageMode);
    }

    public static BackupTaskExecutionState CancelPending(
        BackupTaskExecutionState state,
        BackupStorageMode storageMode)
    {
        EnsureState(state, storageMode, BackupTaskStatus.Pending);
        return new BackupTaskExecutionState(BackupTaskStatus.Cancelled, state.CurrentStage);
    }

    public static BackupTaskExecutionState CancelRunningAtSafeBoundary(
        BackupTaskExecutionState state,
        BackupStorageMode storageMode)
    {
        EnsureState(state, storageMode, BackupTaskStatus.Running);
        return new BackupTaskExecutionState(BackupTaskStatus.Cancelled, state.CurrentStage);
    }

    public static BackupTaskExecutionState ReconcileSucceeded(
        BackupTaskExecutionState state,
        BackupStorageMode storageMode)
    {
        EnsureState(state, storageMode, BackupTaskStatus.NeedsAttention);

        var stagePath = GetStagePath(storageMode);
        var currentIndex = Array.IndexOf(stagePath, state.CurrentStage!.Value);

        return currentIndex == stagePath.Length - 1
            ? new BackupTaskExecutionState(BackupTaskStatus.Succeeded, state.CurrentStage)
            : new BackupTaskExecutionState(BackupTaskStatus.Pending, stagePath[currentIndex + 1]);
    }

    public static BackupTaskRetryTransition ReconcileSafeToRetry(
        BackupTaskExecutionState state,
        BackupStorageMode storageMode)
    {
        EnsureState(state, storageMode, BackupTaskStatus.NeedsAttention);

        var attemptHandling = state.CurrentStage == BackupTaskStage.Backup
            ? BackupAttemptHandling.CreateNew
            : BackupAttemptHandling.ReuseExisting;

        return new BackupTaskRetryTransition(
            new BackupTaskExecutionState(BackupTaskStatus.Pending, state.CurrentStage),
            attemptHandling);
    }

    public static BackupTaskExecutionState ReconcileFailed(
        BackupTaskExecutionState state,
        BackupStorageMode storageMode)
    {
        EnsureState(state, storageMode, BackupTaskStatus.NeedsAttention);
        return new BackupTaskExecutionState(BackupTaskStatus.Failed, state.CurrentStage);
    }

    public static BackupTaskExecutionState ReconcileCancelled(
        BackupTaskExecutionState state,
        BackupStorageMode storageMode)
    {
        EnsureState(state, storageMode, BackupTaskStatus.NeedsAttention);
        return new BackupTaskExecutionState(BackupTaskStatus.Cancelled, state.CurrentStage);
    }

    public static BackupTaskExecutionState ReconcileInconclusive(
        BackupTaskExecutionState state,
        BackupStorageMode storageMode)
    {
        EnsureState(state, storageMode, BackupTaskStatus.NeedsAttention);
        return state;
    }

    public static BackupTaskRetryTransition RetryFailed(
        BackupTaskExecutionState state,
        BackupStorageMode storageMode)
    {
        EnsureState(state, storageMode, BackupTaskStatus.Failed);

        var (retryStage, attemptHandling) = state.CurrentStage switch
        {
            BackupTaskStage.Backup => (BackupTaskStage.Backup, BackupAttemptHandling.CreateNew),
            BackupTaskStage.VerifyLocal =>
                (BackupTaskStage.VerifyLocal, BackupAttemptHandling.ReuseExisting),
            BackupTaskStage.Transfer =>
                (BackupTaskStage.Transfer, BackupAttemptHandling.ReuseExisting),
            BackupTaskStage.ValidateCopy =>
                (BackupTaskStage.Transfer, BackupAttemptHandling.ReuseExisting),
            BackupTaskStage.Cleanup =>
                (BackupTaskStage.Cleanup, BackupAttemptHandling.ReuseExisting),
            _ => throw new InvalidOperationException("失败任务缺少可重试的阶段。")
        };

        return new BackupTaskRetryTransition(
            new BackupTaskExecutionState(BackupTaskStatus.Pending, retryStage),
            attemptHandling);
    }

    private static void EnsureState(
        BackupTaskExecutionState state,
        BackupStorageMode storageMode,
        BackupTaskStatus requiredStatus)
    {
        EnsureValid(state, storageMode);

        if (state.Status != requiredStatus)
        {
            throw new InvalidOperationException(
                $"任务必须处于 {requiredStatus} 状态才能执行该转换。");
        }
    }

    private static BackupTaskStage[] GetStagePath(BackupStorageMode storageMode)
    {
        return storageMode switch
        {
            BackupStorageMode.LocalOnly => LocalOnlyStages,
            BackupStorageMode.LocalAndRemote => LocalAndRemoteStages,
            BackupStorageMode.RemoteOnly => RemoteOnlyStages,
            _ => throw new ArgumentOutOfRangeException(
                nameof(storageMode),
                storageMode,
                "存储模式无效。")
        };
    }

    private static void EnsureStorageMode(BackupStorageMode storageMode)
    {
        _ = GetStagePath(storageMode);
    }
}
