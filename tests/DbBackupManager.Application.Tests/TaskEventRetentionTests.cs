using DbBackupManager.Application.BackupTasks;

namespace DbBackupManager.Application.Tests;

public sealed class TaskEventRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DefaultOptionsAreLockedToConfirmedRetentionPolicy()
    {
        TaskEventRetentionOptions.Default.Validate();

        Assert.Equal(TimeSpan.FromDays(30), TaskEventRetentionOptions.Default.RetentionWindow);
        Assert.Equal(200_000, TaskEventRetentionOptions.Default.RetentionMaxCount);
        Assert.Equal(TimeSpan.FromHours(1), TaskEventRetentionOptions.Default.ScanInterval);
        Assert.Equal(5_000, TaskEventRetentionOptions.Default.MaxDeletionsPerRound);
        Assert.Equal(500, TaskEventRetentionOptions.Default.BatchSize);
        Assert.Equal(TimeSpan.FromHours(2), TaskEventRetentionOptions.Default.FailureBackoff);
    }

    [Fact]
    public void OptionsRejectInvalidCombinations()
    {
        Assert.Throws<InvalidOperationException>(() => Options(window: TimeSpan.Zero).Validate());
        Assert.Throws<InvalidOperationException>(() => Options(window: TimeSpan.FromDays(3651)).Validate());
        Assert.Throws<InvalidOperationException>(() => Options(maxCount: -1).Validate());
        Assert.Throws<InvalidOperationException>(() => Options(scanInterval: TimeSpan.Zero).Validate());
        Assert.Throws<InvalidOperationException>(() => Options(maxPerRound: 0).Validate());
        Assert.Throws<InvalidOperationException>(() => Options(batchSize: 0).Validate());
        Assert.Throws<InvalidOperationException>(() => Options(maxPerRound: 10, batchSize: 11).Validate());
        Assert.Throws<InvalidOperationException>(() => Options(backoff: TimeSpan.FromSeconds(30)).Validate());
    }

    [Fact]
    public async Task UnpublishedEventsSurviveWindowCapAndRepeatedRuns()
    {
        var store = new FakeStore();
        var ancient = Now.AddYears(-1);
        store.Seed(Guid.NewGuid(), ancient, publishedAtUtc: null);
        store.Seed(Guid.NewGuid(), ancient, ancient.AddMinutes(1));
        store.Seed(Guid.NewGuid(), Now.AddMinutes(-5), Now.AddMinutes(-4));
        var runner = Runner(store, Options(window: TimeSpan.FromDays(1), maxCount: 0));

        for (var round = 0; round < 3; round++)
        {
            await runner.RunOnceAsync();
        }

        var remaining = Assert.Single(store.Rows);
        Assert.Null(remaining.PublishedAtUtc);
        Assert.Equal(ancient, remaining.OccurredAtUtc);
    }

    [Fact]
    public async Task WindowDeletesOnlyExpiredPublishedEvents()
    {
        var store = new FakeStore();
        var expired = Guid.NewGuid();
        var recent = Guid.NewGuid();
        var unpublished = Guid.NewGuid();
        store.Seed(expired, Now.AddDays(-40), Now.AddDays(-40));
        store.Seed(recent, Now.AddDays(-10), Now.AddDays(-10));
        store.Seed(unpublished, Now.AddDays(-40), publishedAtUtc: null);
        var runner = Runner(store, Options(window: TimeSpan.FromDays(30), maxCount: 200_000));

        var result = await runner.RunOnceAsync();

        Assert.Equal(1, result.WindowDeleted);
        Assert.Equal(0, result.CapDeleted);
        Assert.False(result.HitRoundLimit);
        Assert.Equal(Now.AddDays(-40), result.OldestDeletedOccurredAtUtc);
        Assert.Equal(Now.AddDays(-40), result.NewestDeletedOccurredAtUtc);
        Assert.Equal(
            new[] { recent, unpublished }.Order(),
            store.Rows.Select(row => row.EventId).Order());
    }

    [Fact]
    public async Task CapTrimsOldestPublishedBeyondLimitWhenWindowDeletesNothing()
    {
        var store = new FakeStore();
        for (var index = 0; index < 5; index++)
        {
            store.Seed(Guid.NewGuid(), Now.AddHours(-index - 1), Now.AddHours(-index - 1));
        }

        var runner = Runner(store, Options(window: TimeSpan.FromDays(3650), maxCount: 2, batchSize: 10, maxPerRound: 100));
        var result = await runner.RunOnceAsync();

        Assert.Equal(0, result.WindowDeleted);
        Assert.Equal(3, result.CapDeleted);
        Assert.Equal(2, store.Rows.Count);
        Assert.Equal(Now.AddHours(-1), store.Rows.Max(row => row.OccurredAtUtc));
    }

    [Fact]
    public async Task SingleRoundRespectsMaxDeletionsPerRound()
    {
        var store = new FakeStore();
        for (var index = 0; index < 10; index++)
        {
            store.Seed(Guid.NewGuid(), Now.AddDays(-40).AddMinutes(-index), Now.AddDays(-40));
        }

        var runner = Runner(store, Options(window: TimeSpan.FromDays(30), maxPerRound: 4, batchSize: 2));
        var result = await runner.RunOnceAsync();

        Assert.Equal(4, result.TotalDeleted);
        Assert.Equal(2, result.Batches);
        Assert.True(result.HitRoundLimit);
        Assert.Equal(6, store.Rows.Count);
    }

    [Fact]
    public async Task BatchingContinuesUntilWindowCandidatesAreExhausted()
    {
        var store = new FakeStore();
        for (var index = 0; index < 5; index++)
        {
            store.Seed(Guid.NewGuid(), Now.AddDays(-40).AddMinutes(-index), Now.AddDays(-40));
        }

        var runner = Runner(store, Options(window: TimeSpan.FromDays(30), maxPerRound: 100, batchSize: 2));
        var result = await runner.RunOnceAsync();

        Assert.Equal(5, result.TotalDeleted);
        Assert.Equal(3, result.Batches);
        Assert.False(result.HitRoundLimit);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task CapZeroStillCannotDeleteUnpublishedEvents()
    {
        var store = new FakeStore();
        store.Seed(Guid.NewGuid(), Now.AddMinutes(-1), publishedAtUtc: null);
        var runner = Runner(store, Options(window: TimeSpan.FromDays(1), maxCount: 0));

        var result = await runner.RunOnceAsync();

        Assert.Equal(0, result.TotalDeleted);
        Assert.Single(store.Rows);
        Assert.Null(store.Rows[0].PublishedAtUtc);
    }

    private static TaskEventRetentionOptions Options(
        TimeSpan? window = null,
        int maxCount = 200_000,
        TimeSpan? scanInterval = null,
        int maxPerRound = 5_000,
        int batchSize = 500,
        TimeSpan? backoff = null) =>
        new(
            window ?? TimeSpan.FromDays(30),
            maxCount,
            scanInterval ?? TimeSpan.FromHours(1),
            maxPerRound,
            batchSize,
            backoff ?? TimeSpan.FromHours(2));

    private static TaskEventRetentionRunner Runner(FakeStore store, TaskEventRetentionOptions options) =>
        new(store, new FrozenTime(Now), options);

    private sealed class FrozenTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeStore : ITaskEventRetentionStore
    {
        private readonly List<Row> _rows = [];

        public List<Row> Rows => _rows;

        public void Seed(Guid eventId, DateTimeOffset occurredAtUtc, DateTimeOffset? publishedAtUtc) =>
            _rows.Add(new(eventId, occurredAtUtc, publishedAtUtc));

        public Task<int> CountPublishedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.Count(row => row.PublishedAtUtc is not null));

        public Task<TaskEventRetentionBatch> DeleteExpiredBatchAsync(
            DateTimeOffset cutoffUtc,
            int take,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Remove(_rows
                .Where(row => row.PublishedAtUtc is not null && row.OccurredAtUtc < cutoffUtc)
                .OrderBy(row => row.OccurredAtUtc).ThenBy(row => row.EventId)
                .Take(take)
                .ToArray()));

        public Task<TaskEventRetentionBatch> DeleteOldestPublishedBatchAsync(
            int take,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Remove(_rows
                .Where(row => row.PublishedAtUtc is not null)
                .OrderBy(row => row.OccurredAtUtc).ThenBy(row => row.EventId)
                .Take(take)
                .ToArray()));

        private TaskEventRetentionBatch Remove(Row[] batch)
        {
            if (batch.Length == 0)
            {
                return TaskEventRetentionBatch.Empty;
            }

            foreach (var row in batch)
            {
                _rows.Remove(row);
            }

            return new(batch.Length, batch.Min(row => row.OccurredAtUtc), batch.Max(row => row.OccurredAtUtc));
        }

        internal sealed record Row(Guid EventId, DateTimeOffset OccurredAtUtc, DateTimeOffset? PublishedAtUtc);
    }
}
