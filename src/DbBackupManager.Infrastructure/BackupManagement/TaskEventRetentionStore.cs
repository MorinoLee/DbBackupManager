using System.Data.Common;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.BackupManagement;

/// <summary>
/// TaskEvent 清理的持久化实现。删除走 <c>ExecuteDeleteAsync</c>，不经过 ChangeTracker，
/// 因此不会触发 <see cref="PlatformDbContext"/> 中「任务事件仅允许推进投递标记」的守卫；
/// 该守卫保护的是正常业务路径的追加语义，不得为清理而放宽。
/// </summary>
internal sealed class TaskEventRetentionStore(IDbContextFactory<PlatformDbContext> factory) : ITaskEventRetentionStore
{
    private const int MaxTake = 1_000;

    public async Task<int> CountPublishedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await CreateDbContextAsync(cancellationToken);
            return await db.TaskEvents.AsNoTracking()
                .CountAsync(item => item.PublishedAtUtc != null, cancellationToken);
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            throw new TaskEventRetentionOperationException(
                TaskEventRetentionFailureCode.StoreUnavailable);
        }
    }

    public async Task<TaskEventRetentionBatch> DeleteExpiredBatchAsync(
        DateTimeOffset cutoffUtc,
        int take,
        CancellationToken cancellationToken = default)
    {
        RequireTake(take);
        await using var db = await CreateDbContextAsync(cancellationToken);
        IReadOnlyList<TaskEventCandidate> candidates;
        try
        {
            candidates = await db.TaskEvents.AsNoTracking()
                .Where(item => item.PublishedAtUtc != null && item.OccurredAtUtc < cutoffUtc)
                .OrderBy(item => item.OccurredAtUtc).ThenBy(item => item.EventId)
                .Take(take)
                .Select(item => new TaskEventCandidate(item.EventId, item.OccurredAtUtc))
                .ToListAsync(cancellationToken);
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            throw new TaskEventRetentionOperationException(
                TaskEventRetentionFailureCode.StoreUnavailable);
        }

        return await DeleteExpiredAsync(db, candidates, cutoffUtc, cancellationToken);
    }

    public async Task<TaskEventRetentionBatch> DeleteOldestPublishedBatchAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        RequireTake(take);
        await using var db = await CreateDbContextAsync(cancellationToken);
        IReadOnlyList<TaskEventCandidate> candidates;
        try
        {
            candidates = await db.TaskEvents.AsNoTracking()
                .Where(item => item.PublishedAtUtc != null)
                .OrderBy(item => item.OccurredAtUtc).ThenBy(item => item.EventId)
                .Take(take)
                .Select(item => new TaskEventCandidate(item.EventId, item.OccurredAtUtc))
                .ToListAsync(cancellationToken);
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            throw new TaskEventRetentionOperationException(
                TaskEventRetentionFailureCode.StoreUnavailable);
        }

        return await DeleteOldestPublishedAsync(db, candidates, cancellationToken);
    }

    private static async Task<TaskEventRetentionBatch> DeleteExpiredAsync(
        PlatformDbContext db,
        IReadOnlyList<TaskEventCandidate> candidates,
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        var batch = PrepareBatch(candidates);
        if (batch is null)
        {
            return TaskEventRetentionBatch.Empty;
        }

        try
        {
            var deleted = await db.TaskEvents
                .Where(item => batch.Ids.Contains(item.EventId)
                    && item.PublishedAtUtc != null
                    && item.OccurredAtUtc < cutoffUtc)
                .ExecuteDeleteAsync(cancellationToken);
            return new(deleted, batch.Occurred.Min(), batch.Occurred.Max());
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            throw new TaskEventRetentionOperationException(
                TaskEventRetentionFailureCode.DeleteFailed);
        }
    }

    private static async Task<TaskEventRetentionBatch> DeleteOldestPublishedAsync(
        PlatformDbContext db,
        IReadOnlyList<TaskEventCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var batch = PrepareBatch(candidates);
        if (batch is null)
        {
            return TaskEventRetentionBatch.Empty;
        }

        try
        {
            var deleted = await db.TaskEvents
                .Where(item => batch.Ids.Contains(item.EventId) && item.PublishedAtUtc != null)
                .ExecuteDeleteAsync(cancellationToken);
            return new(deleted, batch.Occurred.Min(), batch.Occurred.Max());
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            throw new TaskEventRetentionOperationException(
                TaskEventRetentionFailureCode.DeleteFailed);
        }
    }

    private static DeletionBatch? PrepareBatch(IReadOnlyList<TaskEventCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var ids = new Guid[candidates.Count];
        var occurred = new DateTimeOffset[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            ids[index] = candidates[index].EventId;
            occurred[index] = candidates[index].OccurredAtUtc;
        }

        return new(ids, occurred);
    }

    private static void RequireTake(int take)
    {
        if (take is < 1 or > MaxTake)
        {
            throw new ArgumentOutOfRangeException(
                nameof(take),
                take,
                $"TaskEvent 清理批次必须在 1 到 {MaxTake} 之间。");
        }
    }

    private sealed record TaskEventCandidate(Guid EventId, DateTimeOffset OccurredAtUtc);

    private sealed record DeletionBatch(Guid[] Ids, DateTimeOffset[] Occurred);

    private static bool IsStoreFailure(Exception exception) =>
        exception is DbException or InvalidOperationException;

    private async Task<PlatformDbContext> CreateDbContextAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await factory.CreateDbContextAsync(cancellationToken);
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            throw new TaskEventRetentionOperationException(
                TaskEventRetentionFailureCode.StoreUnavailable);
        }
    }
}
