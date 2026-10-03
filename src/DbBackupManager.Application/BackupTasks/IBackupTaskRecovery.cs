using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.BackupTasks;

public interface IBackupTaskRecovery
{
    Task<bool> ReconcileOnceAsync(CancellationToken cancellationToken = default);

    Task<bool> ReconcileTaskOnceAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);
}

public interface IBackupReconciliationEvidenceProbe
{
    Task<BackupReconciliationEvidence> InspectAsync(
        BackupExecutionWorkItem workItem,
        CancellationToken cancellationToken = default);
}

public enum BackupReconciliationConclusion
{
    Succeeded = 1,
    ConfirmedFailed = 2,
    Inconclusive = 3,
}

public enum BackupArtifactObservation
{
    Unknown = 1,
    Missing = 2,
    PresentStable = 3,
    PresentChanging = 4,
    IdentityMismatch = 5,
}

public enum TargetBackupObservation
{
    Unknown = 1,
    NotFound = 2,
    InProgress = 3,
    CompletedMatching = 4,
    IdentityMismatch = 5,
}

public enum BackupVerificationObservation
{
    NotAttempted = 1,
    Succeeded = 2,
    ConfirmedFailed = 3,
    Indeterminate = 4,
}

public sealed class BackupReconciliationEvidence
{
    public BackupReconciliationEvidence(
        Guid taskId,
        Guid backupAttemptId,
        BackupTaskStage stage,
        DateTimeOffset observedAtUtc,
        BackupReconciliationConclusion conclusion,
        BackupArtifactObservation artifact,
        TargetBackupObservation targetBackup,
        BackupVerificationObservation verification,
        string reasonCode,
        long? sourceLengthBytes = null)
    {
        TaskId = RequireId(taskId, nameof(taskId));
        BackupAttemptId = RequireId(backupAttemptId, nameof(backupAttemptId));
        Stage = RequireDefined(stage, nameof(stage));
        ObservedAtUtc = RequireUtc(observedAtUtc, nameof(observedAtUtc));
        Conclusion = RequireDefined(conclusion, nameof(conclusion));
        Artifact = RequireDefined(artifact, nameof(artifact));
        TargetBackup = RequireDefined(targetBackup, nameof(targetBackup));
        Verification = RequireDefined(verification, nameof(verification));
        ReasonCode = RequireReasonCode(reasonCode);
        SourceLengthBytes = sourceLengthBytes switch
        {
            null => null,
            > 0 => sourceLengthBytes,
            _ => throw new ArgumentOutOfRangeException(
                nameof(sourceLengthBytes),
                "核对文件长度必须为正数。"),
        };

        EnsureSucceededEvidenceIsComplete();
    }

    public Guid TaskId { get; }

    public Guid BackupAttemptId { get; }

    public BackupTaskStage Stage { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public BackupReconciliationConclusion Conclusion { get; }

    public BackupArtifactObservation Artifact { get; }

    public TargetBackupObservation TargetBackup { get; }

    public BackupVerificationObservation Verification { get; }

    public string ReasonCode { get; }

    public long? SourceLengthBytes { get; }

    private void EnsureSucceededEvidenceIsComplete()
    {
        if (Conclusion != BackupReconciliationConclusion.Succeeded)
        {
            return;
        }

        if (Stage == BackupTaskStage.Cleanup)
        {
            if (Artifact != BackupArtifactObservation.Missing
                || Verification != BackupVerificationObservation.NotAttempted)
            {
                throw new ArgumentException("Cleanup 核对成功必须确认 Worker 源文件已不存在。");
            }

            return;
        }

        if (Artifact != BackupArtifactObservation.PresentStable
            || TargetBackup != TargetBackupObservation.CompletedMatching)
        {
            throw new ArgumentException("核对成功必须确认任务专属文件稳定且目标备份身份匹配。");
        }

        if (Stage == BackupTaskStage.VerifyLocal
            && (Verification != BackupVerificationObservation.Succeeded
                || SourceLengthBytes is null))
        {
            throw new ArgumentException("本地校验核对成功必须包含校验成功和正数文件长度证据。");
        }

        if (Stage == BackupTaskStage.Backup
            && Verification != BackupVerificationObservation.NotAttempted)
        {
            throw new ArgumentException("Backup 核对成功只能推进到 VerifyLocal，不能跨阶段保存校验结论。");
        }

        if (Stage is BackupTaskStage.Transfer or BackupTaskStage.ValidateCopy
            && (Verification != BackupVerificationObservation.NotAttempted
                || SourceLengthBytes is null))
        {
            throw new ArgumentException("远程传输核对成功必须包含正数长度证据，且不能保存 SQL 校验结论。");
        }
    }

    private static Guid RequireId(Guid value, string parameterName)
    {
        return value != Guid.Empty
            ? value
            : throw new ArgumentException("标识不能为空。", parameterName);
    }

    private static T RequireDefined<T>(T value, string parameterName)
        where T : struct, Enum
    {
        return Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, value, "枚举值无效。");
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        return value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentException("时间必须使用 UTC。", parameterName);
    }

    private static string RequireReasonCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 100
            || value.Any(character => !(character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_' or '.' or '-')))
        {
            throw new ArgumentException(
                "核对原因码必须是长度不超过 100 的小写稳定标识。",
                nameof(value));
        }

        return value;
    }
}
