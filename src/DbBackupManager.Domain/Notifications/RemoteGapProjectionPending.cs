using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.Notifications;

/// <summary>
/// 某个数据库还有一次尚未成功的远端缺口投影。成功投影后删除本行；同一库同时只保留一行。
/// </summary>
public sealed class RemoteGapProjectionPending : ConcurrentEntity
{
    private readonly List<RemoteGapProjectionPendingTarget> _targets = [];

    private RemoteGapProjectionPending()
    {
    }

    public RemoteGapProjectionPending(
        Guid id,
        Guid databaseId,
        Guid taskId,
        BackupTaskStage? stage,
        DateTimeOffset requestedAtUtc)
        : base(id)
    {
        DatabaseId = ConfigurationValues.RequireId(databaseId, nameof(databaseId));
        Note(taskId, stage, requestedAtUtc);
    }

    public Guid DatabaseId { get; private set; }

    public Guid TaskId { get; private set; }

    public BackupTaskStage? Stage { get; private set; }

    public DateTimeOffset RequestedAtUtc { get; private set; }

    public IReadOnlyCollection<RemoteGapProjectionPendingTarget> Targets => _targets;

    public IReadOnlyList<Guid> TargetIds() => _targets.Select(target => target.StorageTargetId).ToArray();

    public void Note(Guid taskId, BackupTaskStage? stage, DateTimeOffset requestedAtUtc)
    {
        TaskId = ConfigurationValues.RequireId(taskId, nameof(taskId));
        if (stage is not null && !Enum.IsDefined(stage.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "枚举值无效。");
        }

        Stage = stage;
        RequestedAtUtc = ConfigurationValues.RequireUtc(requestedAtUtc, nameof(requestedAtUtc));
    }

    public void IncludeTarget(Guid storageTargetId)
    {
        var target = ConfigurationValues.RequireId(storageTargetId, nameof(storageTargetId));
        if (_targets.Any(item => item.StorageTargetId == target))
        {
            return;
        }

        _targets.Add(new RemoteGapProjectionPendingTarget(Id, target));
    }
}

/// <summary>待投影标记要覆盖的远端目标。投影成功后随标记一起删除。</summary>
public sealed class RemoteGapProjectionPendingTarget
{
    private RemoteGapProjectionPendingTarget()
    {
    }

    internal RemoteGapProjectionPendingTarget(Guid pendingId, Guid storageTargetId)
    {
        PendingId = ConfigurationValues.RequireId(pendingId, nameof(pendingId));
        StorageTargetId = ConfigurationValues.RequireId(storageTargetId, nameof(storageTargetId));
    }

    public Guid PendingId { get; private set; }

    public Guid StorageTargetId { get; private set; }
}
