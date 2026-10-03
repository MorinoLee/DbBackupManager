namespace DbBackupManager.Domain.BackupTasks;

public sealed class TaskEvent
{
    private TaskEvent()
    {
    }

    public TaskEvent(Guid eventId, Guid taskId, DateTimeOffset occurredAtUtc)
    {
        EventId = BackupTaskValues.RequireId(eventId, nameof(eventId));
        TaskId = BackupTaskValues.RequireId(taskId, nameof(taskId));
        OccurredAtUtc = BackupTaskValues.RequireUtc(occurredAtUtc, nameof(occurredAtUtc));
    }

    public Guid EventId { get; private set; }

    public Guid TaskId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public DateTimeOffset? PublishedAtUtc { get; private set; }

    public void MarkPublished(DateTimeOffset now)
    {
        PublishedAtUtc ??= BackupTaskValues.RequireUtc(now, nameof(now));
    }
}
