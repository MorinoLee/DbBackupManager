namespace DbBackupManager.Application.BackupTasks;

/// <summary>
/// 已发布 <see cref="Domain.BackupTasks.TaskEvent"/> 的保留策略。窗口与上限共同决定可清理范围；
/// 未发布事件（<c>PublishedAtUtc IS NULL</c>）在任何配置组合下都不可删除。
/// </summary>
public sealed record TaskEventRetentionOptions(
    TimeSpan RetentionWindow,
    int RetentionMaxCount,
    TimeSpan ScanInterval,
    int MaxDeletionsPerRound,
    int BatchSize,
    TimeSpan FailureBackoff)
{
    public static TaskEventRetentionOptions Default { get; } = new(
        TimeSpan.FromDays(30),
        200_000,
        TimeSpan.FromHours(1),
        5_000,
        500,
        TimeSpan.FromHours(2));

    public void Validate()
    {
        // 批次上限同时受 SQL Server 参数个数限制约束：批量删除按 EventId 列表生成 IN 子句，
        // 单批过大会直接失败，因此批次不得与单轮上限一起放开到任意值。
        if (RetentionWindow <= TimeSpan.Zero
            || RetentionWindow > TimeSpan.FromDays(3650)
            || RetentionMaxCount < 0
            || ScanInterval < TimeSpan.FromSeconds(1)
            || ScanInterval > TimeSpan.FromDays(1)
            || MaxDeletionsPerRound is < 1 or > 1_000_000
            || BatchSize is < 1 or > 1_000
            || BatchSize > MaxDeletionsPerRound
            || FailureBackoff < ScanInterval
            || FailureBackoff > TimeSpan.FromDays(1))
        {
            throw new InvalidOperationException("TaskEvent 保留窗口、上限、扫描间隔、批次或退避配置无效。");
        }
    }
}

public sealed record TaskEventRetentionBatch(
    int DeletedCount,
    DateTimeOffset? OldestOccurredAtUtc,
    DateTimeOffset? NewestOccurredAtUtc)
{
    public static TaskEventRetentionBatch Empty { get; } = new(0, null, null);
}

public enum TaskEventRetentionFailureCode
{
    StoreUnavailable,
    DeleteFailed,
    ConfigurationInvalid,
    UnexpectedFailure,
}

public sealed class TaskEventRetentionOperationException(
    TaskEventRetentionFailureCode failureCode) : Exception("TaskEvent 保留清理操作失败。")
{
    public TaskEventRetentionFailureCode FailureCode { get; } = failureCode;
}

public sealed record TaskEventRetentionRoundResult(
    int WindowDeleted,
    int CapDeleted,
    int Batches,
    bool HitRoundLimit,
    DateTimeOffset? OldestDeletedOccurredAtUtc,
    DateTimeOffset? NewestDeletedOccurredAtUtc)
{
    public int TotalDeleted => WindowDeleted + CapDeleted;
}

public interface ITaskEventRetentionStore
{
    Task<int> CountPublishedAsync(CancellationToken cancellationToken = default);

    Task<TaskEventRetentionBatch> DeleteExpiredBatchAsync(
        DateTimeOffset cutoffUtc,
        int take,
        CancellationToken cancellationToken = default);

    Task<TaskEventRetentionBatch> DeleteOldestPublishedBatchAsync(
        int take,
        CancellationToken cancellationToken = default);
}

public sealed class TaskEventRetentionRunner(
    ITaskEventRetentionStore store,
    TimeProvider clock,
    TaskEventRetentionOptions options)
{
    public async Task<TaskEventRetentionRoundResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        options.Validate();

        var cutoffUtc = clock.GetUtcNow().ToUniversalTime() - options.RetentionWindow;
        var batches = 0;
        var windowDeleted = 0;
        DateTimeOffset? oldest = null;
        DateTimeOffset? newest = null;

        while (windowDeleted < options.MaxDeletionsPerRound)
        {
            var take = Math.Min(options.BatchSize, options.MaxDeletionsPerRound - windowDeleted);
            var batch = await store.DeleteExpiredBatchAsync(cutoffUtc, take, cancellationToken);
            batches++;
            windowDeleted += batch.DeletedCount;
            Track(batch, ref oldest, ref newest);
            if (batch.DeletedCount < take)
            {
                break;
            }
        }

        if (windowDeleted >= options.MaxDeletionsPerRound)
        {
            return new(windowDeleted, 0, batches, true, oldest, newest);
        }

        // 上限保护的是病态突发写入：即使未到保留窗口，也把已发布事件裁到上限以内。
        var capDeleted = 0;
        var excess = await store.CountPublishedAsync(cancellationToken) - options.RetentionMaxCount;
        while (excess > 0 && windowDeleted + capDeleted < options.MaxDeletionsPerRound)
        {
            var remaining = options.MaxDeletionsPerRound - windowDeleted - capDeleted;
            var take = Math.Min(Math.Min(options.BatchSize, remaining), excess);
            var batch = await store.DeleteOldestPublishedBatchAsync(take, cancellationToken);
            batches++;
            capDeleted += batch.DeletedCount;
            excess -= batch.DeletedCount;
            Track(batch, ref oldest, ref newest);
            if (batch.DeletedCount < take)
            {
                break;
            }
        }

        return new(windowDeleted, capDeleted, batches, windowDeleted + capDeleted >= options.MaxDeletionsPerRound, oldest, newest);
    }

    private static void Track(
        TaskEventRetentionBatch batch,
        ref DateTimeOffset? oldest,
        ref DateTimeOffset? newest)
    {
        if (batch.DeletedCount == 0)
        {
            return;
        }

        if (batch.OldestOccurredAtUtc is { } batchOldest && (oldest is null || batchOldest < oldest))
        {
            oldest = batchOldest;
        }

        if (batch.NewestOccurredAtUtc is { } batchNewest && (newest is null || batchNewest > newest))
        {
            newest = batchNewest;
        }
    }
}
