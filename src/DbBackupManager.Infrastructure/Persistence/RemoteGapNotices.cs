using System.Data;
using System.Text;
using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Application.Notifications;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Notifications;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DbBackupManager.Infrastructure.Persistence;

/// <summary>
/// 远端缺口通知在运行事实提交之后单独投影。需要投影时，待补发标记与事实写在同一事务。
/// 标记用单条 upsert 写入，不先读、也不按 RowVersion 冲突失败。1205 死锁不在这里吞掉，
/// 由事实事务外层的执行策略把整段事务重试。投影成功后只清除这次已经投影的目标；
/// 失败、拿不到保留锁或尚未投影的目标都留下标记。之后只由写入口补做：
/// 阶段提交、Cleanup、远端副本删除和计划保留删除。读取恢复摘要不写库、不拿锁、不入队。不启动后台循环。
/// 同一数据库、远端目标和缺口在恢复前只保留一封；恢复后再次出现可以再发。
/// 位置未知或这次没有该目标的投影时，不新开也不关闭已打开缺口。
/// </summary>
internal static class RemoteGapNotices
{
    private static readonly Action<ILogger, string, Exception?> ProjectionFailed = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(6401, "RemoteGapProjectionFailed"),
        "远端缺口投影失败，已跳过这封通知。异常类型：{ExceptionType}");

    private static readonly Action<ILogger, Guid, Exception?> LockUnavailable = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(6402, "RemoteGapProjectionLockUnavailable"),
        "未能取得计划保留锁，已跳过这次远端缺口投影。数据库：{DatabaseId}");

    internal readonly record struct Observation(
        Guid DatabaseId,
        Guid TaskId,
        BackupTaskStage? Stage,
        DateTimeOffset OccurredAtUtc,
        IReadOnlyCollection<Guid> StorageTargetIds);

    internal static async Task MarkPendingAsync(
        PlatformDbContext context,
        Observation observation,
        CancellationToken cancellationToken)
    {
        var targets = DistinctTargets(observation.StorageTargetIds);
        if (observation.DatabaseId == Guid.Empty || observation.TaskId == Guid.Empty || targets.Length == 0)
        {
            return;
        }

        if (observation.Stage is BackupTaskStage stage && !Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(observation), observation.Stage, "枚举值无效。");
        }

        _ = ConfigurationValues.RequireUtc(observation.OccurredAtUtc, nameof(observation));
        var parameters = new List<SqlParameter>
        {
            new("@databaseId", SqlDbType.UniqueIdentifier) { Value = observation.DatabaseId },
            new("@taskId", SqlDbType.UniqueIdentifier) { Value = observation.TaskId },
            new("@stage", SqlDbType.NVarChar, 30) { Value = (object?)observation.Stage?.ToString() ?? DBNull.Value },
            new("@requestedAt", SqlDbType.DateTimeOffset) { Value = observation.OccurredAtUtc },
        };
        var values = new StringBuilder();
        for (var index = 0; index < targets.Length; index++)
        {
            var name = "@target" + index;
            parameters.Add(new SqlParameter(name, SqlDbType.UniqueIdentifier) { Value = targets[index] });
            if (index > 0)
            {
                values.Append(", ");
            }

            values.Append('(').Append(name).Append(')');
        }

        // 与投影方抢同一行时可能 1205。异常交给外层执行策略，连同本事务里的事实一起重试。
        // values 只含本方法生成的参数名。
        var sql = """
            MERGE [RemoteGapProjectionPendings] WITH (HOLDLOCK) AS target
            USING (SELECT @databaseId AS [DatabaseId]) AS source
            ON target.[DatabaseId] = source.[DatabaseId]
            WHEN MATCHED THEN
                UPDATE SET
                    [TaskId] = @taskId,
                    [Stage] = @stage,
                    [RequestedAtUtc] = @requestedAt,
                    [UpdatedAtUtc] = SYSDATETIMEOFFSET()
            WHEN NOT MATCHED THEN
                INSERT ([Id], [DatabaseId], [TaskId], [Stage], [RequestedAtUtc], [CreatedAtUtc], [UpdatedAtUtc])
                VALUES (NEWID(), @databaseId, @taskId, @stage, @requestedAt, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());

            MERGE [RemoteGapProjectionPendingTargets] WITH (HOLDLOCK) AS target
            USING (
                SELECT pending.[Id] AS [PendingId], incoming.[StorageTargetId]
                FROM [RemoteGapProjectionPendings] AS pending
                INNER JOIN (VALUES
            """ + values + """
            ) AS incoming([StorageTargetId])
                    ON pending.[DatabaseId] = @databaseId
            ) AS source
            ON target.[PendingId] = source.[PendingId]
               AND target.[StorageTargetId] = source.[StorageTargetId]
            WHEN NOT MATCHED THEN
                INSERT ([PendingId], [StorageTargetId])
                VALUES (source.[PendingId], source.[StorageTargetId]);
            """;
        await context.Database.ExecuteSqlRawAsync(sql, parameters.Cast<object>(), cancellationToken);
    }

    internal static async Task TryDrainPendingAsync(
        IDbContextFactory<PlatformDbContext> factory,
        Guid databaseId,
        CancellationToken cancellationToken,
        ILogger? logger = null,
        IReadOnlyDictionary<Guid, RemoteGapProjection>? knownGaps = null)
    {
        if (databaseId == Guid.Empty)
        {
            return;
        }

        try
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            var pending = await context.RemoteGapProjectionPendings.AsNoTracking()
                .Include(item => item.Targets)
                .SingleOrDefaultAsync(item => item.DatabaseId == databaseId, cancellationToken);
            if (pending is null)
            {
                return;
            }

            await ProjectAsync(
                factory,
                new Observation(
                    pending.DatabaseId,
                    pending.TaskId,
                    pending.Stage,
                    pending.RequestedAtUtc,
                    pending.TargetIds()),
                knownGaps,
                cancellationToken,
                logger);
        }
        catch (Exception exception)
        {
            if (logger is not null)
            {
                ProjectionFailed(logger, exception.GetType().Name, null);
            }
        }
    }

    internal static async Task TryProjectAsync(
        IDbContextFactory<PlatformDbContext> factory,
        Observation observation,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        try
        {
            await ProjectAsync(factory, observation, knownGaps: null, cancellationToken, logger);
        }
        catch (Exception exception)
        {
            // 运行事实和待补发标记已经提交。这次投影失败只留下标记。日志只保留异常类型。
            if (logger is not null)
            {
                ProjectionFailed(logger, exception.GetType().Name, null);
            }
        }
    }

    internal static async Task<bool> ProjectAsync(
        IDbContextFactory<PlatformDbContext> factory,
        Observation observation,
        IReadOnlyDictionary<Guid, RemoteGapProjection>? knownGaps,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var requested = DistinctTargets(observation.StorageTargetIds);
        if (observation.DatabaseId == Guid.Empty || observation.TaskId == Guid.Empty || requested.Length == 0)
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
            if (!await PlanRetentionLock.TryAcquireAsync(
                context, observation.DatabaseId, cancellationToken))
            {
                if (logger is not null)
                {
                    LockUnavailable(logger, observation.DatabaseId, null);
                }

                return false;
            }

            var pending = await context.RemoteGapProjectionPendings.AsTracking()
                .Include(item => item.Targets)
                .SingleOrDefaultAsync(item => item.DatabaseId == observation.DatabaseId, cancellationToken);
            var targets = DistinctTargets(requested.Concat(pending?.TargetIds() ?? []));
            var taskId = pending?.TaskId ?? observation.TaskId;
            var occurredAt = pending?.RequestedAtUtc ?? observation.OccurredAtUtc;
            var stage = pending is null ? observation.Stage : pending.Stage;
            stage ??= await context.BackupTasks.AsNoTracking()
                .Where(task => task.Id == taskId)
                .Select(task => task.CurrentStage)
                .SingleOrDefaultAsync(cancellationToken);
            var scoped = observation with { TaskId = taskId, Stage = stage, OccurredAtUtc = occurredAt, StorageTargetIds = targets };
            var gaps = knownGaps is null
                ? await LoadGapsAsync(context, scoped, targets, cancellationToken)
                : CopyGaps(targets, knownGaps);
            var saved = await SyncAsync(
                context,
                observation.DatabaseId,
                taskId,
                PlanNotificationRules.RemoteNoticeStage(stage),
                occurredAt,
                gaps,
                cancellationToken);
            if (!saved)
            {
                return false;
            }

            await ClearProjectedTargetsAsync(context, observation.DatabaseId, gaps.Keys, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        });
    }

    private static async Task ClearProjectedTargetsAsync(
        PlatformDbContext context,
        Guid databaseId,
        IEnumerable<Guid> projectedTargets,
        CancellationToken cancellationToken)
    {
        var targets = DistinctTargets(projectedTargets);
        if (targets.Length == 0)
        {
            return;
        }

        var parameters = new List<SqlParameter>
        {
            new("@databaseId", SqlDbType.UniqueIdentifier) { Value = databaseId },
        };
        var list = new StringBuilder();
        for (var index = 0; index < targets.Length; index++)
        {
            var name = "@clear" + index;
            parameters.Add(new SqlParameter(name, SqlDbType.UniqueIdentifier) { Value = targets[index] });
            if (index > 0)
            {
                list.Append(", ");
            }

            list.Append(name);
        }

        var sql = """
            DELETE targetRow
            FROM [RemoteGapProjectionPendingTargets] AS targetRow
            INNER JOIN [RemoteGapProjectionPendings] AS pending ON pending.[Id] = targetRow.[PendingId]
            WHERE pending.[DatabaseId] = @databaseId
              AND targetRow.[StorageTargetId] IN (
            """ + list + """
            );

            DELETE pending
            FROM [RemoteGapProjectionPendings] AS pending
            WHERE pending.[DatabaseId] = @databaseId
              AND NOT EXISTS (
                  SELECT 1
                  FROM [RemoteGapProjectionPendingTargets] AS targetRow
                  WHERE targetRow.[PendingId] = pending.[Id]);
            """;
        await context.Database.ExecuteSqlRawAsync(sql, parameters.Cast<object>(), cancellationToken);
    }

    private static Guid[] DistinctTargets(IEnumerable<Guid> targetIds) =>
        targetIds.Where(id => id != Guid.Empty).Distinct().ToArray();

    internal static async Task<bool> SyncAsync(
        PlatformDbContext context,
        Guid databaseId,
        Guid taskId,
        BackupTaskStage stage,
        DateTimeOffset occurredAtUtc,
        IReadOnlyDictionary<Guid, RemoteGapProjection> gaps,
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
            if (!gaps.TryGetValue(episode.StorageTargetId, out var projection)
                || !projection.UpdatesOpenEpisodes
                || projection.Codes.Contains(episode.GapCode, StringComparer.Ordinal))
            {
                continue;
            }

            episode.MarkRecovered(occurredAtUtc);
        }

        foreach (var (target, projection) in gaps)
        {
            if (!projection.UpdatesOpenEpisodes)
            {
                continue;
            }

            foreach (var code in projection.Codes.Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.Ordinal))
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

    private static async Task<Dictionary<Guid, RemoteGapProjection>> LoadGapsAsync(
        PlatformDbContext context,
        Observation observation,
        Guid[] targets,
        CancellationToken cancellationToken)
    {
        var wanted = targets.ToHashSet();
        var surface = await PlanLocationRetentionWork.LoadSurfaceAsync(
            context, observation.DatabaseId, observation.OccurredAtUtc, cancellationToken);
        var gaps = new Dictionary<Guid, RemoteGapProjection>();
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

    private static Dictionary<Guid, RemoteGapProjection> CopyGaps(
        Guid[] targets,
        IReadOnlyDictionary<Guid, RemoteGapProjection> knownGaps)
    {
        var gaps = new Dictionary<Guid, RemoteGapProjection>();
        foreach (var target in targets)
        {
            if (knownGaps.TryGetValue(target, out var projection))
            {
                gaps[target] = projection;
            }
        }

        return gaps;
    }

    private static bool IsOpenGapConflict(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 } sql
        && sql.Message.Contains("UX_RemoteGapEpisodes_Open", StringComparison.Ordinal);
}
