using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.Notifications;

/// <summary>
/// 一个尚未恢复的远端缺口只对应一封通知。恢复后保留本行，同一缺口再次出现时另开一行。
/// </summary>
public sealed class RemoteGapEpisode : ConcurrentEntity
{
    private RemoteGapEpisode()
    {
    }

    public RemoteGapEpisode(
        Guid id,
        Guid databaseId,
        Guid storageTargetId,
        string gapCode,
        Guid notificationId,
        DateTimeOffset openedAtUtc)
        : base(id)
    {
        DatabaseId = ConfigurationValues.RequireId(databaseId, nameof(databaseId));
        StorageTargetId = ConfigurationValues.RequireId(storageTargetId, nameof(storageTargetId));
        GapCode = ConfigurationValues.RequireText(gapCode, 100, nameof(gapCode));
        NotificationId = ConfigurationValues.RequireId(notificationId, nameof(notificationId));
        OpenedAtUtc = ConfigurationValues.RequireUtc(openedAtUtc, nameof(openedAtUtc));
    }

    public Guid DatabaseId { get; private set; }

    public Guid StorageTargetId { get; private set; }

    public string GapCode { get; private set; } = string.Empty;

    public Guid NotificationId { get; private set; }

    public DateTimeOffset OpenedAtUtc { get; private set; }

    public DateTimeOffset? RecoveredAtUtc { get; private set; }

    public bool IsOpen => RecoveredAtUtc is null;

    public void MarkRecovered(DateTimeOffset recoveredAtUtc)
    {
        var recoveredAt = ConfigurationValues.RequireUtc(recoveredAtUtc, nameof(recoveredAtUtc));
        if (recoveredAt < OpenedAtUtc)
        {
            throw new ArgumentException("恢复时间不能早于缺口出现时间。", nameof(recoveredAtUtc));
        }

        if (RecoveredAtUtc is not null)
        {
            if (RecoveredAtUtc != recoveredAt)
            {
                throw new InvalidOperationException("远端缺口已经记为恢复。");
            }

            return;
        }

        RecoveredAtUtc = recoveredAt;
    }
}
