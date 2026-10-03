using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Notifications;

namespace DbBackupManager.Infrastructure.Persistence;

internal static class NotificationOutboxWriter
{
    public static void EnqueueTaskStage(
        PlatformDbContext context,
        Guid mutationId,
        Guid taskId,
        BackupTaskStage stage,
        BackupStageOutcome outcome,
        DateTimeOffset occurredAtUtc,
        string? errorCode)
    {
        var type = outcome switch
        {
            BackupStageOutcome.ConfirmedFailed => NotificationType.TaskFailed,
            BackupStageOutcome.Indeterminate => NotificationType.TaskIndeterminate,
            _ => (NotificationType?)null,
        };
        if (type is null)
        {
            return;
        }

        TryAdd(
            context,
            mutationId,
            type.Value,
            NotificationSourceKind.BackupTask,
            taskId,
            taskId,
            stage,
            occurredAtUtc,
            string.IsNullOrWhiteSpace(errorCode) ? Reason(type.Value) : errorCode);
    }

    public static void EnqueueReconciliation(
        PlatformDbContext context,
        Guid mutationId,
        Guid taskId,
        BackupTaskStage stage,
        BackupReconciliationOutcome outcome,
        DateTimeOffset occurredAtUtc,
        string? errorCode)
    {
        var type = outcome switch
        {
            BackupReconciliationOutcome.Failed => NotificationType.TaskFailed,
            BackupReconciliationOutcome.Inconclusive => NotificationType.TaskIndeterminate,
            _ => (NotificationType?)null,
        };
        if (type is null)
        {
            return;
        }

        TryAdd(
            context,
            mutationId,
            type.Value,
            NotificationSourceKind.BackupTask,
            taskId,
            taskId,
            stage,
            occurredAtUtc,
            string.IsNullOrWhiteSpace(errorCode) ? Reason(type.Value) : errorCode);
    }

    public static void EnqueueLeaseExpired(
        PlatformDbContext context,
        Guid mutationId,
        Guid taskId,
        BackupTaskStage stage,
        DateTimeOffset occurredAtUtc,
        string errorCode) =>
        TryAdd(
            context,
            mutationId,
            NotificationType.TaskIndeterminate,
            NotificationSourceKind.BackupTask,
            taskId,
            taskId,
            stage,
            occurredAtUtc,
            errorCode);

    public static void EnqueueRetention(
        PlatformDbContext context,
        Guid mutationId,
        Guid fileId,
        Guid taskId,
        BackupFileRetentionOutcome outcome,
        DateTimeOffset occurredAtUtc,
        string? errorCode)
    {
        var type = outcome switch
        {
            BackupFileRetentionOutcome.Failed => NotificationType.RetentionDeleteFailed,
            BackupFileRetentionOutcome.Missing => NotificationType.RetentionMissing,
            _ => (NotificationType?)null,
        };
        if (type is null)
        {
            return;
        }

        TryAdd(
            context,
            mutationId,
            type.Value,
            NotificationSourceKind.BackupFile,
            fileId,
            taskId,
            null,
            occurredAtUtc,
            string.IsNullOrWhiteSpace(errorCode) ? Reason(type.Value) : errorCode);
    }

    public static void EnqueueExternalMissing(
        PlatformDbContext context,
        Guid mutationId,
        Guid fileId,
        Guid taskId,
        DateTimeOffset occurredAtUtc) =>
        TryAdd(
            context,
            mutationId,
            NotificationType.RetentionMissing,
            NotificationSourceKind.BackupFile,
            fileId,
            taskId,
            null,
            occurredAtUtc,
            "file.health_missing");

    public static bool TryAdd(
        PlatformDbContext context,
        Guid mutationId,
        NotificationType type,
        NotificationSourceKind sourceKind,
        Guid sourceId,
        Guid? taskId,
        BackupTaskStage? stage,
        DateTimeOffset occurredAtUtc,
        string errorCode)
    {
        if (context.NotificationOutbox.Local.Any(item => item.MutationId == mutationId && item.Type == type))
        {
            return false;
        }

        context.NotificationOutbox.Add(new NotificationOutbox(
            Guid.NewGuid(),
            mutationId,
            type,
            sourceKind,
            sourceId,
            taskId,
            stage,
            occurredAtUtc,
            errorCode));
        return true;
    }

    private static string Reason(NotificationType type) => type switch
    {
        NotificationType.TaskFailed => "stage.confirmed_failed",
        NotificationType.TaskIndeterminate => "stage.indeterminate",
        NotificationType.RetentionDeleteFailed => "file.delete_failed",
        NotificationType.RetentionMissing => "file.missing",
        NotificationType.AdminTest => "smtp.test",
        _ => "notification.unknown",
    };
}
