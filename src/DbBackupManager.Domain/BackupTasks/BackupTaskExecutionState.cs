namespace DbBackupManager.Domain.BackupTasks;

public readonly record struct BackupTaskExecutionState
{
    public BackupTaskExecutionState(BackupTaskStatus status, BackupTaskStage? currentStage)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "任务状态无效。");
        }

        if (currentStage is not null && !Enum.IsDefined(currentStage.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentStage),
                currentStage,
                "任务阶段无效。");
        }

        if (status != BackupTaskStatus.Cancelled && currentStage is null)
        {
            throw new ArgumentException("除尚未开始即取消的任务外，任务必须记录当前阶段。", nameof(currentStage));
        }

        Status = status;
        CurrentStage = currentStage;
    }

    public BackupTaskStatus Status { get; }

    public BackupTaskStage? CurrentStage { get; }
}

public readonly record struct BackupTaskRetryTransition(
    BackupTaskExecutionState State,
    BackupAttemptHandling AttemptHandling);
