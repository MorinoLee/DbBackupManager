using DbBackupManager.Application.FileStorage;

namespace DbBackupManager.Application.BackupTasks;

internal sealed record BackupStageResult(
    BackupStageOutcome Outcome,
    string? ErrorCode = null,
    long? Length = null)
{
    internal static BackupStageResult MapFile<T>(BackupFileStorageResult<T> result) where T : class =>
        new(
            result.Outcome == BackupFileStorageOutcome.Indeterminate
                ? BackupStageOutcome.Indeterminate
                : BackupStageOutcome.ConfirmedFailed,
            result.Failure!.Code.ToString());
}
