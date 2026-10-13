using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Notifications;
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

    public static void EnqueuePlanOutcome(
        PlatformDbContext context,
        Guid mutationId,
        Guid taskId,
        BackupTaskStage? stage,
        BackupTaskStatus status,
        DateTimeOffset occurredAtUtc,
        string? errorCode)
    {
        if (PlanNotificationRules.ForCommittedTask(status) is not { } type || stage is null)
        {
            return;
        }

        TryAdd(
            context,
            mutationId,
            type,
            NotificationSourceKind.BackupTask,
            taskId,
            taskId,
            stage,
            occurredAtUtc,
            string.IsNullOrWhiteSpace(errorCode) ? PlanNotificationRules.Fallback(type) : errorCode);
    }

    public static bool EnqueueRemoteUnrecoverable(
        PlatformDbContext context,
        Guid mutationId,
        Guid taskId,
        BackupTaskStage stage,
        DateTimeOffset occurredAtUtc,
        string errorCode) =>
        TryAdd(
            context,
            mutationId,
            NotificationType.RemoteUnrecoverable,
            NotificationSourceKind.BackupTask,
            taskId,
            taskId,
            stage,
            occurredAtUtc,
            string.IsNullOrWhiteSpace(errorCode) ? PlanNotificationRules.LocationNotRestorable : errorCode);

    public static async Task EnqueueRemoteProjectionAsync(
        PlatformDbContext context,
        Guid mutationId,
        Guid taskId,
        BackupTaskStage stage,
        DateTimeOffset occurredAtUtc,
        Guid databaseId,
        IReadOnlyCollection<Guid> storageTargetIds,
        CancellationToken cancellationToken)
    {
        var targets = storageTargetIds.Where(id => id != Guid.Empty).ToHashSet();
        if (targets.Count == 0)
        {
            return;
        }

        var surface = await PlanLocationRetentionWork.LoadSurfaceAsync(
            context, databaseId, occurredAtUtc, cancellationToken);
        string? code = null;
        foreach (var item in surface.Assessments)
        {
            if (item.Location != BackupFileLocation.Remote
                || item.StorageTargetId is not Guid target
                || !targets.Contains(target))
            {
                continue;
            }

            var projected = BackupRecoverySurface.Project(
                item.Location,
                item.StorageTargetId,
                item.WindowDays,
                item.Conclusion,
                surface.Baseline,
                item.Copies.Select(copy => copy.Reason).ToArray());
            if (PlanNotificationRules.RemoteErrorCode(projected) is { } candidate)
            {
                code = PlanNotificationRules.PreferRemoteCode(code, candidate);
            }
        }

        if (code is null)
        {
            return;
        }

        if (EnqueueRemoteUnrecoverable(context, mutationId, taskId, stage, occurredAtUtc, code))
        {
            await context.SaveChangesAsync(cancellationToken);
        }
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

    private static string Reason(NotificationType type) => PlanNotificationRules.Fallback(type);
}
