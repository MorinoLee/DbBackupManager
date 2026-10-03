namespace DbBackupManager.Domain.BackupTasks;

public sealed class BackupTaskStateChange
{
    private BackupTaskStateChange()
    {
    }

    public BackupTaskStateChange(
        Guid mutationId,
        Guid taskId,
        BackupTaskStatus? fromStatus,
        BackupTaskStage? fromStage,
        BackupTaskStatus toStatus,
        BackupTaskStage? toStage,
        Guid? backupAttemptId,
        string reasonCode,
        string? message,
        DateTimeOffset occurredAtUtc)
    {
        MutationId = BackupTaskValues.RequireId(mutationId, nameof(mutationId));
        TaskId = BackupTaskValues.RequireId(taskId, nameof(taskId));
        ValidateState(fromStatus, fromStage, isPrevious: true);
        ValidateState(toStatus, toStage, isPrevious: false);
        BackupAttemptId = backupAttemptId is null
            ? null
            : BackupTaskValues.RequireId(backupAttemptId.Value, nameof(backupAttemptId));
        ReasonCode = BackupTaskValues.RequireText(reasonCode, 100, nameof(reasonCode));
        Message = BackupTaskValues.OptionalText(message, 500, nameof(message));
        OccurredAtUtc = BackupTaskValues.RequireUtc(occurredAtUtc, nameof(occurredAtUtc));
        FromStatus = fromStatus;
        FromStage = fromStage;
        ToStatus = toStatus;
        ToStage = toStage;
    }

    public long Id { get; private set; }

    public Guid MutationId { get; private set; }

    public Guid TaskId { get; private set; }

    public BackupTaskStatus? FromStatus { get; private set; }

    public BackupTaskStage? FromStage { get; private set; }

    public BackupTaskStatus ToStatus { get; private set; }

    public BackupTaskStage? ToStage { get; private set; }

    public Guid? BackupAttemptId { get; private set; }

    public string ReasonCode { get; private set; } = string.Empty;

    public string? Message { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    private static void ValidateState(
        BackupTaskStatus? status,
        BackupTaskStage? stage,
        bool isPrevious)
    {
        if (status is null)
        {
            if (!isPrevious || stage is not null)
            {
                throw new ArgumentException("只有首条状态历史可以没有前一状态和阶段。");
            }

            return;
        }

        BackupTaskValues.RequireDefined(status.Value, nameof(status));
        if (stage is not null)
        {
            BackupTaskValues.RequireDefined(stage.Value, nameof(stage));
        }

        if (status != BackupTaskStatus.Cancelled && stage is null)
        {
            throw new ArgumentException("非取消状态必须记录阶段。", nameof(stage));
        }
    }
}
