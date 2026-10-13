namespace DbBackupManager.Application.BackupTasks;

public interface IBackupTaskExecutionStore
{
    Task<BackupTaskStoreResult<BackupTaskStateModel>> RequestCancellationAsync(
        BackupTaskMutationCommand command,
        CancellationToken cancellationToken = default);
}
