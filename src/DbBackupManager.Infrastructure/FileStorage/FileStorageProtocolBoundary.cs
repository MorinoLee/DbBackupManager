using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Infrastructure.FileStorage;

internal interface IFileStorageProtocolSessionFactory
{
    FileTransferProtocol Protocol { get; }

    ValueTask<IFileStorageProtocolSession> OpenAsync(
        BackupFileEndpointInput endpoint,
        CancellationToken cancellationToken);
}

internal interface IFileStorageProtocolSession : IAsyncDisposable
{
    ValueTask PrepareParentDirectoryAsync(
        string filePath,
        CancellationToken cancellationToken);

    ValueTask<BackupFileMetadata> InspectAsync(
        string path,
        CancellationToken cancellationToken);

    ValueTask<Stream> OpenReadAsync(
        string path,
        CancellationToken cancellationToken);

    ValueTask<Stream> CreateNewAsync(
        string path,
        CancellationToken cancellationToken);

    ValueTask RenameNoReplaceAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken);

    ValueTask DeleteAsync(
        string path,
        CancellationToken cancellationToken);
}

internal sealed class FileStorageAdapterException(
    BackupFileStorageFailureCode code,
    BackupFileStorageFailurePhase phase,
    bool indeterminate = false) : Exception(code.ToString())
{
    public BackupFileStorageFailureCode Code { get; } = code;

    public BackupFileStorageFailurePhase Phase { get; } = phase;

    public bool Indeterminate { get; } = indeterminate;
}
