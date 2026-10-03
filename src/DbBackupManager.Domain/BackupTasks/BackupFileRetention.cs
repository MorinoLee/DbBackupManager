namespace DbBackupManager.Domain.BackupTasks;

public readonly record struct BackupFileRetentionGroup
{
    public BackupFileRetentionGroup(
        Guid databaseId,
        BackupFileLocation location,
        Guid? storageTargetId)
    {
        DatabaseId = BackupTaskValues.RequireId(databaseId, nameof(databaseId));
        BackupTaskValues.RequireDefined(location, nameof(location));
        if (location == BackupFileLocation.Local)
        {
            if (storageTargetId is not null)
            {
                throw new ArgumentException("本地 Retention 组不能包含存储目标。", nameof(storageTargetId));
            }
        }
        else if (storageTargetId is null || storageTargetId == Guid.Empty)
        {
            throw new ArgumentException("远程 Retention 组必须包含存储目标。", nameof(storageTargetId));
        }

        Location = location;
        StorageTargetId = storageTargetId;
    }

    public Guid DatabaseId { get; }

    public BackupFileLocation Location { get; }

    public Guid? StorageTargetId { get; }
}

public static class BackupFileRetentionPolicy
{
    public static BackupFile? SelectDeletionCandidate(
        IReadOnlyList<BackupFile> groupFiles,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(groupFiles);
        var now = BackupTaskValues.RequireUtc(utcNow, nameof(utcNow));
        BackupFileRetentionGroup? group = null;
        foreach (var file in groupFiles)
        {
            ArgumentNullException.ThrowIfNull(file);
            if (group is null)
            {
                group = file.RetentionGroup;
            }
            else if (file.RetentionGroup != group)
            {
                throw new ArgumentException("候选计算只能使用同一 Retention 组的副本。", nameof(groupFiles));
            }
        }

        var available = groupFiles
            .Where(file => file.Status == BackupFileStatus.Available)
            .OrderByDescending(file => file.ValidatedAtUtc)
            .ThenByDescending(file => file.Id)
            .ToArray();
        if (available.Length < 2)
        {
            return null;
        }

        return available
            .Skip(1)
            .Where(file => file.IsRetentionDue(now))
            .OrderBy(file => file.ValidatedAtUtc)
            .ThenBy(file => file.Id)
            .FirstOrDefault();
    }

    public static bool HasProtectedAvailableCopy(
        IReadOnlyList<BackupFile> groupFiles,
        Guid excludedFileId)
    {
        ArgumentNullException.ThrowIfNull(groupFiles);
        _ = BackupTaskValues.RequireId(excludedFileId, nameof(excludedFileId));
        return groupFiles.Any(file =>
            file.Id != excludedFileId && file.Status == BackupFileStatus.Available);
    }

    public static TimeSpan DeletionBackoff(int deletionAttemptCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(deletionAttemptCount, 1);
        return deletionAttemptCount switch
        {
            1 => TimeSpan.FromMinutes(1),
            2 => TimeSpan.FromMinutes(5),
            3 => TimeSpan.FromMinutes(15),
            _ => TimeSpan.FromHours(1),
        };
    }
}
