using System.Data;
using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Application.Notifications;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Notifications;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

/// <summary>
/// 远端缺口通知在运行事实提交之后单独投影。投影失败不回滚事实。
/// 同一数据库、远端目标和缺口在恢复前只保留一封；恢复后再次出现可以再发。
/// </summary>
internal static class RemoteGapNotices
{
    internal readonly record struct Observation(
        Guid DatabaseId,
        Guid TaskId,
        BackupTaskStage? Stage,
        DateTimeOffset OccurredAtUtc,
        IReadOnlyCollection<Guid> StorageTargetIds);

    internal static async Task TryProjectAsync(
        IDbContextFactory<PlatformDbContext> factory,
        Observation observation,
        CancellationToken cancellationToken)
    {
        try
        {
            await ProjectAsync(factory, observation, knownGaps: null, cancellationToken);
        }
        catch (Exception)
        {
            // 运行事实已经提交。这次投影失败只跳过远端通知。
        }
    }

    internal static async Task<bool> ProjectAsync(
        IDbContextFactory<PlatformDbContext> factory,
        Observation observation,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>? knownGaps,
        CancellationToken cancellationToken)
    {
        var targets = observation.StorageTargetIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (observation.DatabaseId == Guid.Empty || observation.TaskId == Guid.Empty || targets.Length == 0)
        {
            return false;
        }

        await using var strategyContext = await factory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            if (!await PlanRetentionLock.TryAcquireAsync(context, observation.DatabaseId, cancellationToken))
            {
                return false;
            }

            var gaps = knownGaps is null
                ? await LoadGapsAsync(context, observation, targets, cancellationToken)
                : CopyGaps(targets, knownGaps);
            var stage = observation.Stage ?? await context.BackupTasks.AsNoTracking()
                .Where(task => task.Id == observation.TaskId)
                .Select(task => task.CurrentStage)
                .SingleOrDefaultAsync(cancellationToken);
            var saved = await SyncAsync(
                context,
                observation.DatabaseId,
                observation.TaskId,
                PlanNotificationRules.RemoteNoticeStage(stage),
                observation.OccurredAtUtc,
                gaps,
                cancellationToken);
            if (!saved)
            {
                return false;
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        });
    }

    internal static async Task<bool> SyncAsync(
        PlatformDbContext context,
        Guid databaseId,
        Guid taskId,
        BackupTaskStage stage,
        DateTimeOffset occurredAtUtc,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> gaps,
        CancellationToken cancellationToken)
    {
        var targets = gaps.Keys.Where(id => id != Guid.Empty).ToArray();
        if (targets.Length == 0)
        {
            return true;
        }

        var open = await context.RemoteGapEpisodes.AsTracking()
            .Where(episode => episode.DatabaseId == databaseId
                && episode.RecoveredAtUtc == null
                && targets.Contains(episode.StorageTargetId))
            .ToListAsync(cancellationToken);
        foreach (var episode in open)
        {
            if (!gaps.TryGetValue(episode.StorageTargetId, out var codes) || !codes.Contains(episode.GapCode))
            {
                episode.MarkRecovered(occurredAtUtc);
            }
        }

        foreach (var (target, codes) in gaps)
        {
            foreach (var code in codes.Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.Ordinal))
            {
                if (open.Any(episode => episode.IsOpen
                    && episode.StorageTargetId == target
                    && string.Equals(episode.GapCode, code, StringComparison.Ordinal)))
                {
                    continue;
                }

                var notificationId = Guid.NewGuid();
                context.NotificationOutbox.Add(new NotificationOutbox(
                    notificationId,
                    Guid.NewGuid(),
                    NotificationType.RemoteUnrecoverable,
                    NotificationSourceKind.BackupTask,
                    taskId,
                    taskId,
                    stage,
                    occurredAtUtc,
                    code));
                context.RemoteGapEpisodes.Add(new RemoteGapEpisode(
                    Guid.NewGuid(),
                    databaseId,
                    target,
                    code,
                    notificationId,
                    occurredAtUtc));
            }
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (IsOpenGapConflict(exception))
        {
            return false;
        }
    }

    private static async Task<Dictionary<Guid, IReadOnlyCollection<string>>> LoadGapsAsync(
        PlatformDbContext context,
        Observation observation,
        Guid[] targets,
        CancellationToken cancellationToken)
    {
        var wanted = targets.ToHashSet();
        var surface = await PlanLocationRetentionWork.LoadSurfaceAsync(
            context, observation.DatabaseId, observation.OccurredAtUtc, cancellationToken);
        var gaps = wanted.ToDictionary(
            target => target,
            _ => (IReadOnlyCollection<string>)Array.Empty<string>());
        foreach (var item in surface.Assessments)
        {
            if (item.Location != BackupFileLocation.Remote
                || item.StorageTargetId is not Guid target
                || !wanted.Contains(target))
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
            gaps[target] = PlanNotificationRules.RemoteGapCodes(projected);
        }

        return gaps;
    }

    private static Dictionary<Guid, IReadOnlyCollection<string>> CopyGaps(
        Guid[] targets,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> knownGaps)
    {
        var gaps = new Dictionary<Guid, IReadOnlyCollection<string>>();
        foreach (var target in targets)
        {
            gaps[target] = knownGaps.TryGetValue(target, out var codes)
                ? codes
                : Array.Empty<string>();
        }

        return gaps;
    }

    private static bool IsOpenGapConflict(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 } sql
        && sql.Message.Contains("UX_RemoteGapEpisodes_Open", StringComparison.Ordinal);
}
