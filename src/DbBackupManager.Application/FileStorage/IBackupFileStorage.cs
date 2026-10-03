namespace DbBackupManager.Application.FileStorage;

public interface IBackupFileStorageProbe
{
    Task<BackupFileStorageResult<BackupFileMetadata>> InspectAsync(
        BackupFileEndpointInput endpoint,
        string path,
        CancellationToken cancellationToken = default);
}

public interface IBackupDirectoryPreparer
{
    Task<BackupFileStorageResult<BackupFileMutationReceipt>> PrepareParentAsync(
        BackupDirectoryPreparationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IBackupFileTransferExecutor
{
    Task<BackupFileStorageResult<BackupFileTransferReceipt>> TransferAsync(
        BackupFileTransferRequest request,
        CancellationToken cancellationToken = default);

    Task<BackupFileStorageResult<BackupFileMutationReceipt>> RenameAsync(
        BackupFileRenameRequest request,
        CancellationToken cancellationToken = default);
}

public interface IBackupFileDeletionExecutor
{
    Task<BackupFileStorageResult<BackupFileMutationReceipt>> DeleteAsync(
        BackupFileDeleteRequest request,
        CancellationToken cancellationToken = default);
}
