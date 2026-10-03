using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.BackupTasks;

public enum BackupFileRetentionOutcome
{
    Deleted = 1,
    Missing = 2,
    Failed = 3,
}

public sealed record ClaimBackupFileDeletionCommand(
    Guid LeaseToken,
    Guid MutationId,
    string LeaseOwner,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record BackupFileRetentionCommitCommand(
    Guid MutationId,
    BackupFileRetentionOutcome Outcome,
    DateTimeOffset OccurredAtUtc,
    string? ErrorCode = null);

public sealed class BackupFileRetentionWorkItem
{
    private readonly byte[] _rowVersion;

    public BackupFileRetentionWorkItem(
        Guid fileId,
        Guid taskId,
        Guid databaseId,
        BackupFileLocation location,
        Guid? storageTargetId,
        Guid? databaseServerId,
        FileTransferProtocol protocol,
        string path,
        long lengthBytes,
        BackupFileStatus status,
        Guid leaseToken,
        DateTimeOffset leaseExpiresAtUtc,
        byte[] rowVersion,
        BackupFileEndpointInput endpoint)
    {
        ArgumentNullException.ThrowIfNull(rowVersion);
        FileId = BackupFileStorageValues.Id(fileId, nameof(fileId));
        TaskId = BackupFileStorageValues.Id(taskId, nameof(taskId));
        DatabaseId = BackupFileStorageValues.Id(databaseId, nameof(databaseId));
        if (!Enum.IsDefined(location))
        {
            throw new ArgumentOutOfRangeException(nameof(location));
        }

        Location = location;
        StorageTargetId = storageTargetId;
        DatabaseServerId = databaseServerId;
        if (!Enum.IsDefined(protocol))
        {
            throw new ArgumentOutOfRangeException(nameof(protocol));
        }

        Protocol = protocol;
        Path = BackupFileStorageValues.ExactPath(path, nameof(path));
        LengthBytes = BackupFileStorageValues.PositiveLength(lengthBytes, nameof(lengthBytes));
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
        LeaseToken = BackupFileStorageValues.Id(leaseToken, nameof(leaseToken));
        if (leaseExpiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("删除租约到期时间必须使用 UTC。", nameof(leaseExpiresAtUtc));
        }

        LeaseExpiresAtUtc = leaseExpiresAtUtc;
        _rowVersion = [.. rowVersion];
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public Guid FileId { get; }

    public Guid TaskId { get; }

    public Guid DatabaseId { get; }

    public BackupFileLocation Location { get; }

    public Guid? StorageTargetId { get; }

    public Guid? DatabaseServerId { get; }

    public FileTransferProtocol Protocol { get; }

    public string Path { get; }

    public long LengthBytes { get; }

    public BackupFileStatus Status { get; }

    public Guid LeaseToken { get; }

    public DateTimeOffset LeaseExpiresAtUtc { get; }

    public BackupFileEndpointInput Endpoint { get; }

    public byte[] RowVersion => [.. _rowVersion];
}

public sealed class BackupFileHealthWorkItem
{
    private readonly byte[] _rowVersion;

    public BackupFileHealthWorkItem(
        Guid fileId,
        Guid taskId,
        string path,
        long lengthBytes,
        byte[] rowVersion,
        BackupFileEndpointInput? endpoint)
    {
        ArgumentNullException.ThrowIfNull(rowVersion);
        FileId = BackupFileStorageValues.Id(fileId, nameof(fileId));
        TaskId = BackupFileStorageValues.Id(taskId, nameof(taskId));
        Path = BackupFileStorageValues.ExactPath(path, nameof(path));
        LengthBytes = BackupFileStorageValues.PositiveLength(lengthBytes, nameof(lengthBytes));
        _rowVersion = [.. rowVersion];
        Endpoint = endpoint;
    }

    public Guid FileId { get; }

    public Guid TaskId { get; }

    public string Path { get; }

    public long LengthBytes { get; }

    public BackupFileEndpointInput? Endpoint { get; }

    public byte[] RowVersion => [.. _rowVersion];
}

public interface IBackupFileRetentionStore
{
    Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ClaimNextAsync(
        ClaimBackupFileDeletionCommand command,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ClaimFileAsync(
        Guid fileId,
        ClaimBackupFileDeletionCommand command,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> RefreshWorkItemAsync(
        Guid fileId,
        Guid leaseToken,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> CommitAsync(
        BackupFileRetentionWorkItem work,
        BackupFileRetentionCommitCommand command,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupFileHealthWorkItem>> FindNextHealthInspectionAsync(
        Guid? afterFileId,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupFileHealthWorkItem>> CommitExternalMissingAsync(
        BackupFileHealthWorkItem work,
        Guid mutationId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default);
}
