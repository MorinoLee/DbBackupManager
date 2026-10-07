using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.BackupTasks;

public sealed class BackupFile : ConcurrentEntity
{
    private BackupFile()
    {
    }

    private BackupFile(
        Guid id,
        Guid taskId,
        Guid attemptId,
        Guid databaseId,
        BackupFileLocation location,
        Guid? databaseServerId,
        Guid? storageTargetId,
        FileTransferProtocol protocol,
        string path,
        long lengthBytes,
        DateTimeOffset validatedAtUtc,
        int retentionDays)
        : base(id)
    {
        _ = BackupTaskValues.RequireId(id, nameof(id));
        TaskId = BackupTaskValues.RequireId(taskId, nameof(taskId));
        AttemptId = BackupTaskValues.RequireId(attemptId, nameof(attemptId));
        DatabaseId = BackupTaskValues.RequireId(databaseId, nameof(databaseId));
        BackupTaskValues.RequireDefined(location, nameof(location));
        BackupTaskValues.RequireDefined(protocol, nameof(protocol));

        if (location == BackupFileLocation.Local)
        {
            DatabaseServerId = BackupTaskValues.RequireId(
                databaseServerId ?? Guid.Empty,
                nameof(databaseServerId));
            if (storageTargetId is not null)
            {
                throw new ArgumentException("本地副本不能关联远程存储目标。", nameof(storageTargetId));
            }
        }
        else
        {
            StorageTargetId = BackupTaskValues.RequireId(
                storageTargetId ?? Guid.Empty,
                nameof(storageTargetId));
            if (databaseServerId is not null)
            {
                throw new ArgumentException("远程副本不能关联源数据库服务器。", nameof(databaseServerId));
            }
        }

        Location = location;
        Protocol = protocol;
        Path = NormalizePath(protocol, path);
        LengthBytes = BackupTaskValues.RequirePositive(lengthBytes, nameof(lengthBytes));
        ValidatedAtUtc = BackupTaskValues.RequireUtc(validatedAtUtc, nameof(validatedAtUtc));
        RetentionDays = BackupTaskValues.RequirePositive(retentionDays, 36500, nameof(retentionDays));
        Status = BackupFileStatus.Available;
    }

    public Guid TaskId { get; private set; }

    public Guid AttemptId { get; private set; }

    public Guid DatabaseId { get; private set; }

    public Guid? BackupSetId { get; private set; }

    public void AssociateBackupSet(Guid backupSetId)
    {
        if (BackupSetId is not null || RowVersion.Length != 0)
            throw new InvalidOperationException("备份集关联只能在新副本登记时指定。");
        BackupSetId = BackupTaskValues.RequireId(backupSetId, nameof(backupSetId));
    }

    public BackupFileLocation Location { get; private set; }

    public Guid? DatabaseServerId { get; private set; }

    public Guid? StorageTargetId { get; private set; }

    public FileTransferProtocol Protocol { get; private set; }

    public string Path { get; private set; } = string.Empty;

    public long LengthBytes { get; private set; }

    public DateTimeOffset ValidatedAtUtc { get; private set; }

    public int RetentionDays { get; private set; }

    public BackupFileStatus Status { get; private set; }

    public Guid? DeletionLeaseToken { get; private set; }

    public string? DeletionLeaseOwner { get; private set; }

    public DateTimeOffset? DeletionLeaseAcquiredAtUtc { get; private set; }

    public DateTimeOffset? DeletionLeaseExpiresAtUtc { get; private set; }

    public int DeletionAttemptCount { get; private set; }

    public DateTimeOffset? NextDeletionAttemptAtUtc { get; private set; }

    public string? DeletionErrorCode { get; private set; }

    public DateTimeOffset? DeletedAtUtc { get; private set; }

    public DateTimeOffset? MissingDetectedAtUtc { get; private set; }

    public BackupFileRetentionGroup RetentionGroup => new(DatabaseId, Location, StorageTargetId);

    public bool IsRetentionDue(DateTimeOffset utcNow)
    {
        var now = BackupTaskValues.RequireUtc(utcNow, nameof(utcNow));
        return now >= ValidatedAtUtc.AddDays(RetentionDays);
    }

    public static BackupFile CreateLocal(
        Guid id,
        Guid taskId,
        Guid attemptId,
        Guid databaseId,
        Guid databaseServerId,
        FileTransferProtocol protocol,
        string path,
        long lengthBytes,
        DateTimeOffset validatedAtUtc,
        int retentionDays)
    {
        return new BackupFile(
            id,
            taskId,
            attemptId,
            databaseId,
            BackupFileLocation.Local,
            databaseServerId,
            null,
            protocol,
            path,
            lengthBytes,
            validatedAtUtc,
            retentionDays);
    }

    public static BackupFile CreateRemote(
        Guid id,
        Guid taskId,
        Guid attemptId,
        Guid databaseId,
        Guid storageTargetId,
        FileTransferProtocol protocol,
        string path,
        long lengthBytes,
        DateTimeOffset validatedAtUtc,
        int retentionDays)
    {
        return new BackupFile(
            id,
            taskId,
            attemptId,
            databaseId,
            BackupFileLocation.Remote,
            null,
            storageTargetId,
            protocol,
            path,
            lengthBytes,
            validatedAtUtc,
            retentionDays);
    }

    public void ClaimDeletion(
        Guid leaseToken,
        string leaseOwner,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var acquiredAt = BackupTaskValues.RequireUtc(acquiredAtUtc, nameof(acquiredAtUtc));
        if (Status is not (BackupFileStatus.Available or BackupFileStatus.DeleteFailed))
        {
            throw new InvalidOperationException("只有可用或删除失败的副本可以取得删除租约。");
        }

        if (Status == BackupFileStatus.DeleteFailed
            && (NextDeletionAttemptAtUtc is null || NextDeletionAttemptAtUtc > acquiredAt))
        {
            throw new InvalidOperationException("副本尚未到达下一次允许删除时间。");
        }

        var nextAttemptCount = checked(DeletionAttemptCount + 1);
        SetDeletionLease(leaseToken, leaseOwner, acquiredAt, expiresAtUtc);
        DeletionAttemptCount = nextAttemptCount;
        Status = BackupFileStatus.DeletePending;
        NextDeletionAttemptAtUtc = null;
        DeletionErrorCode = null;
    }

    public void TakeOverExpiredDeletionLease(
        Guid leaseToken,
        string leaseOwner,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var acquiredAt = BackupTaskValues.RequireUtc(acquiredAtUtc, nameof(acquiredAtUtc));
        if (Status != BackupFileStatus.DeletePending
            || DeletionLeaseExpiresAtUtc is null
            || DeletionLeaseExpiresAtUtc > acquiredAt)
        {
            throw new InvalidOperationException("只有删除租约已经到期的副本可以被接管。");
        }

        _ = BackupTaskValues.RequireId(leaseToken, nameof(leaseToken));
        _ = BackupTaskValues.RequireText(leaseOwner, 200, nameof(leaseOwner));
        var expiry = BackupTaskValues.RequireUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (acquiredAt < ValidatedAtUtc || expiry <= acquiredAt)
        {
            throw new ArgumentException("删除租约时间范围无效。", nameof(expiresAtUtc));
        }

        ClearDeletionLease();
        SetDeletionLease(leaseToken, leaseOwner, acquiredAt, expiry);
    }

    public void RenewDeletionLease(
        Guid leaseToken,
        DateTimeOffset utcNow,
        DateTimeOffset expiresAtUtc)
    {
        var now = BackupTaskValues.RequireUtc(utcNow, nameof(utcNow));
        var expiry = BackupTaskValues.RequireUtc(expiresAtUtc, nameof(expiresAtUtc));
        EnsureDeletionLease(leaseToken, now);
        if (expiry <= now || expiry <= DeletionLeaseExpiresAtUtc)
        {
            throw new ArgumentException("续租到期时间必须晚于当前时间和原到期时间。", nameof(expiresAtUtc));
        }

        DeletionLeaseExpiresAtUtc = expiry;
    }

    public void RecordDeleted(Guid leaseToken, DateTimeOffset deletedAtUtc)
    {
        var deletedAt = BackupTaskValues.RequireUtc(deletedAtUtc, nameof(deletedAtUtc));
        EnsureDeletionLease(leaseToken, deletedAt);
        if (deletedAt < ValidatedAtUtc)
        {
            throw new ArgumentException("删除时间不能早于副本验证时间。", nameof(deletedAtUtc));
        }

        Status = BackupFileStatus.Deleted;
        DeletedAtUtc = deletedAt;
        MissingDetectedAtUtc = null;
        NextDeletionAttemptAtUtc = null;
        DeletionErrorCode = null;
        ClearDeletionLease();
    }

    public void RecordMissing(Guid leaseToken, DateTimeOffset detectedAtUtc)
    {
        var detectedAt = BackupTaskValues.RequireUtc(detectedAtUtc, nameof(detectedAtUtc));
        EnsureDeletionLease(leaseToken, detectedAt);
        MarkMissing(detectedAt);
        ClearDeletionLease();
    }

    public void RecordExternalMissing(DateTimeOffset detectedAtUtc)
    {
        var detectedAt = BackupTaskValues.RequireUtc(detectedAtUtc, nameof(detectedAtUtc));
        if (Status != BackupFileStatus.Available)
        {
            throw new InvalidOperationException("只有可用副本可以被健康核对标记为外部缺失。");
        }

        if (DeletionLeaseToken is not null)
        {
            throw new InvalidOperationException("持有删除租约的副本不能按健康核对标记缺失。");
        }

        DeletionAttemptCount = checked(DeletionAttemptCount + 1);
        MarkMissing(detectedAt);
    }

    public void RecordDeletionFailure(
        Guid leaseToken,
        DateTimeOffset failedAtUtc,
        string errorCode,
        DateTimeOffset nextAttemptAtUtc)
    {
        var failedAt = BackupTaskValues.RequireUtc(failedAtUtc, nameof(failedAtUtc));
        var nextAttempt = BackupTaskValues.RequireUtc(nextAttemptAtUtc, nameof(nextAttemptAtUtc));
        var code = BackupTaskValues.RequireText(errorCode, 100, nameof(errorCode));
        EnsureDeletionLease(leaseToken, failedAt);
        if (nextAttempt <= failedAt)
        {
            throw new ArgumentException("下次删除时间必须晚于本次失败时间。", nameof(nextAttemptAtUtc));
        }

        Status = BackupFileStatus.DeleteFailed;
        DeletionErrorCode = code;
        NextDeletionAttemptAtUtc = nextAttempt;
        DeletedAtUtc = null;
        MissingDetectedAtUtc = null;
        ClearDeletionLease();
    }

    private void MarkMissing(DateTimeOffset detectedAtUtc)
    {
        if (detectedAtUtc < ValidatedAtUtc)
        {
            throw new ArgumentException("缺失发现时间不能早于副本验证时间。", nameof(detectedAtUtc));
        }

        Status = BackupFileStatus.Missing;
        MissingDetectedAtUtc = detectedAtUtc;
        DeletedAtUtc = null;
        NextDeletionAttemptAtUtc = null;
        DeletionErrorCode = null;
    }

    private void SetDeletionLease(
        Guid leaseToken,
        string leaseOwner,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (DeletionLeaseToken is not null)
        {
            throw new InvalidOperationException("副本已经持有删除租约。");
        }

        var token = BackupTaskValues.RequireId(leaseToken, nameof(leaseToken));
        var owner = BackupTaskValues.RequireText(leaseOwner, 200, nameof(leaseOwner));
        var expiry = BackupTaskValues.RequireUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (acquiredAtUtc < ValidatedAtUtc || expiry <= acquiredAtUtc)
        {
            throw new ArgumentException("删除租约时间范围无效。", nameof(expiresAtUtc));
        }

        DeletionLeaseToken = token;
        DeletionLeaseOwner = owner;
        DeletionLeaseAcquiredAtUtc = acquiredAtUtc;
        DeletionLeaseExpiresAtUtc = expiry;
    }

    private void EnsureDeletionLease(Guid leaseToken, DateTimeOffset utcNow)
    {
        var token = BackupTaskValues.RequireId(leaseToken, nameof(leaseToken));
        if (Status != BackupFileStatus.DeletePending || DeletionLeaseToken != token)
        {
            throw new InvalidOperationException("删除租约能力不匹配。");
        }

        if (DeletionLeaseExpiresAtUtc is null || DeletionLeaseExpiresAtUtc <= utcNow)
        {
            throw new InvalidOperationException("删除租约已经到期。");
        }
    }

    private void ClearDeletionLease()
    {
        DeletionLeaseToken = null;
        DeletionLeaseOwner = null;
        DeletionLeaseAcquiredAtUtc = null;
        DeletionLeaseExpiresAtUtc = null;
    }

    private static string NormalizePath(FileTransferProtocol protocol, string value)
    {
        var path = BackupTaskValues.RequireText(value, 2048, nameof(value));
        var separator = protocol == FileTransferProtocol.Smb ? '\\' : '/';
        path = path.Replace(protocol == FileTransferProtocol.Smb ? '/' : '\\', separator);
        var parts = path.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".."))
        {
            throw new ArgumentException("副本路径不能包含相对路径段。", nameof(value));
        }

        if (protocol == FileTransferProtocol.Smb)
        {
            if (!path.StartsWith("\\\\", StringComparison.Ordinal) || parts.Length < 3)
            {
                throw new ArgumentException("SMB 副本必须使用包含文件名的 UNC 路径。", nameof(value));
            }

            return $"\\\\{string.Join('\\', parts)}".ToUpperInvariant();
        }

        if (!path.StartsWith('/') || parts.Length < 1)
        {
            throw new ArgumentException("SFTP 副本必须使用绝对文件路径。", nameof(value));
        }

        return $"/{string.Join('/', parts)}";
    }
}

public sealed class BackupFileStateChange
{
    private BackupFileStateChange()
    {
    }

    public BackupFileStateChange(
        Guid mutationId,
        Guid fileId,
        Guid taskId,
        BackupFileStatus? fromStatus,
        BackupFileStatus toStatus,
        string reasonCode,
        DateTimeOffset occurredAtUtc)
    {
        MutationId = BackupTaskValues.RequireId(mutationId, nameof(mutationId));
        FileId = BackupTaskValues.RequireId(fileId, nameof(fileId));
        TaskId = BackupTaskValues.RequireId(taskId, nameof(taskId));
        if (fromStatus is not null)
        {
            BackupTaskValues.RequireDefined(fromStatus.Value, nameof(fromStatus));
        }

        BackupTaskValues.RequireDefined(toStatus, nameof(toStatus));
        FromStatus = fromStatus;
        ToStatus = toStatus;
        ReasonCode = BackupTaskValues.RequireText(reasonCode, 100, nameof(reasonCode));
        OccurredAtUtc = BackupTaskValues.RequireUtc(occurredAtUtc, nameof(occurredAtUtc));
    }

    public long Id { get; private set; }

    public Guid MutationId { get; private set; }

    public Guid FileId { get; private set; }

    public Guid TaskId { get; private set; }

    public BackupFileStatus? FromStatus { get; private set; }

    public BackupFileStatus ToStatus { get; private set; }

    public string ReasonCode { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAtUtc { get; private set; }
}
