namespace DbBackupManager.Application.BackupTasks;

public sealed record TaskInvalidation(Guid EventId, Guid TaskId, DateTimeOffset OccurredAtUtc);

public interface ITaskEventStore
{
    Task<IReadOnlyList<TaskInvalidation>> ReadPendingAsync(int take, CancellationToken token = default);

    Task AcknowledgeAsync(IReadOnlyList<Guid> ids, CancellationToken token = default);
}

public interface ITaskRefreshPublisher
{
    void Publish();
}

public sealed record TaskEventRelayOptions(
    TimeSpan PollInterval,
    int BatchSize,
    int MaxEventsPerRound,
    TimeSpan ErrorBackoff)
{
    public static TaskEventRelayOptions Default { get; } = new(
        TimeSpan.FromSeconds(1),
        100,
        1_000,
        TimeSpan.FromSeconds(2));

    public void Validate()
    {
        if (PollInterval < TimeSpan.FromMilliseconds(500) || PollInterval > TimeSpan.FromSeconds(10)
            || BatchSize is < 1 or > 500
            || MaxEventsPerRound < BatchSize
            || MaxEventsPerRound > 10_000
            || ErrorBackoff < TimeSpan.FromMilliseconds(500)
            || ErrorBackoff > TimeSpan.FromSeconds(60))
        {
            throw new InvalidOperationException("任务事件中继间隔、批次或退避配置无效。");
        }
    }
}

public sealed record TaskEventRelayRoundResult(
    int EventsRead,
    int EventsAcknowledged,
    int Batches,
    int PublishCalls,
    TimeSpan? OldestEventDelay,
    Guid? OldestEventId,
    bool HitRoundLimit);

public sealed class TaskEventRelayRunner(
    ITaskEventStore store,
    ITaskRefreshPublisher publisher,
    TimeProvider clock,
    TaskEventRelayOptions options)
{
    public async Task<TaskEventRelayRoundResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(publisher);
        options.Validate();
        var now = clock.GetUtcNow();
        var read = 0;
        var acknowledged = 0;
        var batches = 0;
        var publishCalls = 0;
        Guid? oldestId = null;
        DateTimeOffset? oldestAt = null;

        while (true)
        {
            var remaining = options.MaxEventsPerRound - read;
            if (remaining <= 0)
            {
                return Result(hitRoundLimit: true);
            }

            var take = Math.Min(options.BatchSize, remaining);
            var batch = await store.ReadPendingAsync(take, cancellationToken);
            if (batch.Count == 0)
            {
                return Result(hitRoundLimit: false);
            }

            batches++;
            read += batch.Count;
            foreach (var item in batch)
            {
                if (oldestAt is null || item.OccurredAtUtc < oldestAt)
                {
                    oldestAt = item.OccurredAtUtc;
                    oldestId = item.EventId;
                }
            }

            publisher.Publish();
            publishCalls++;
            await store.AcknowledgeAsync([.. batch.Select(item => item.EventId)], cancellationToken);
            acknowledged += batch.Count;
            if (batch.Count < take)
            {
                return Result(hitRoundLimit: false);
            }
        }

        TaskEventRelayRoundResult Result(bool hitRoundLimit) => new(
            read,
            acknowledged,
            batches,
            publishCalls,
            oldestAt is null ? null : now - oldestAt.Value,
            oldestId,
            hitRoundLimit);
    }
}
