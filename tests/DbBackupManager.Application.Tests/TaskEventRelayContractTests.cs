using DbBackupManager.Application.BackupTasks;

namespace DbBackupManager.Application.Tests;

public sealed class TaskEventRelayContractTests
{
    private static readonly DateTimeOffset Zero = new(2026, 9, 17, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OptionsRejectInvalidIntervalBatchAndBackoff()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new TaskEventRelayOptions(TimeSpan.FromMilliseconds(499), 100, 1000, TimeSpan.FromSeconds(2)).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new TaskEventRelayOptions(TimeSpan.FromSeconds(1), 0, 1000, TimeSpan.FromSeconds(2)).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new TaskEventRelayOptions(TimeSpan.FromSeconds(1), 100, 99, TimeSpan.FromSeconds(2)).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new TaskEventRelayOptions(TimeSpan.FromSeconds(1), 100, 1000, TimeSpan.FromMilliseconds(499)).Validate());
        TaskEventRelayOptions.Default.Validate();
    }

    [Fact]
    public async Task EmptyPendingDoesNotPublishOrAcknowledge()
    {
        var store = new FakeStore();
        var publisher = new FakePublisher();
        var runner = new TaskEventRelayRunner(store, publisher, new FrozenTime(Zero), Options(100, 1000));

        var result = await runner.RunOnceAsync();

        Assert.Equal(0, result.EventsRead);
        Assert.Equal(0, result.PublishCalls);
        Assert.Equal(0, store.AcknowledgeCalls);
        Assert.False(result.HitRoundLimit);
    }

    [Theory]
    [InlineData(0, 100, 1_000, 1, 1)]
    [InlineData(99, 100, 1_000, 1, 1)]
    [InlineData(1_001, 100, 1_000, 2, 11)]
    [InlineData(10_001, 500, 10_000, 2, 21)]
    public async Task StructuralLoadMatrixConvergesWithBoundedRoundsAndBatches(
        int pendingCount,
        int batchSize,
        int maxEventsPerRound,
        int expectedRounds,
        int expectedReadCalls)
    {
        var store = new FakeStore(Enumerable.Range(1, pendingCount).Select(index => Item(index)));
        var publisher = new FakePublisher();
        var runner = new TaskEventRelayRunner(
            store,
            publisher,
            new FrozenTime(Zero),
            Options(batchSize, maxEventsPerRound));
        var results = new List<TaskEventRelayRoundResult>();

        do
        {
            results.Add(await runner.RunOnceAsync());
        }
        while (store.Pending.Count > 0);

        Assert.Equal(expectedRounds, results.Count);
        Assert.Equal(pendingCount, results.Sum(result => result.EventsRead));
        Assert.Equal(pendingCount, results.Sum(result => result.EventsAcknowledged));
        Assert.All(results, result => Assert.InRange(result.EventsRead, 0, maxEventsPerRound));
        Assert.Equal(expectedReadCalls, store.ReadCalls);
        Assert.InRange(store.MaxRequestedTake, 0, batchSize);
        Assert.InRange(store.MaxAcknowledgementSize, 0, batchSize);
        Assert.Equal(store.AcknowledgeCalls, publisher.Calls);
        Assert.Empty(store.Pending);
    }

    [Fact]
    public async Task ConsecutiveIdleRoundsStayReadOnlyAndDoNotPublish()
    {
        var store = new FakeStore();
        var publisher = new FakePublisher();
        var runner = new TaskEventRelayRunner(
            store,
            publisher,
            new FrozenTime(Zero),
            Options(100, 1_000));

        var results = new List<TaskEventRelayRoundResult>();
        for (var index = 0; index < 5; index++)
        {
            results.Add(await runner.RunOnceAsync());
        }

        Assert.All(results, result => Assert.Equal(0, result.EventsRead));
        Assert.Equal(5, store.ReadCalls);
        Assert.Equal(0, store.AcknowledgeCalls);
        Assert.Equal(0, publisher.Calls);
    }

    [Fact]
    public async Task FullBatchesCatchUpUntilDrainedAndRecordOldestDelay()
    {
        var older = Item(1, Zero.AddSeconds(-30));
        var store = new FakeStore([older, Item(2, Zero.AddSeconds(-10)), Item(3, Zero.AddSeconds(-5))]);
        var publisher = new FakePublisher();
        var runner = new TaskEventRelayRunner(store, publisher, new FrozenTime(Zero), Options(2, 1000));

        var result = await runner.RunOnceAsync();

        Assert.Equal(3, result.EventsRead);
        Assert.Equal(3, result.EventsAcknowledged);
        Assert.Equal(2, result.Batches);
        Assert.Equal(2, result.PublishCalls);
        Assert.Equal(TimeSpan.FromSeconds(30), result.OldestEventDelay);
        Assert.Equal(older.EventId, result.OldestEventId);
        Assert.False(result.HitRoundLimit);
        Assert.Empty(store.Pending);
    }

    [Fact]
    public async Task RoundLimitStopsCatchUpAndLeavesRemainingPending()
    {
        var store = new FakeStore([Item(1), Item(2), Item(3), Item(4), Item(5)]);
        var publisher = new FakePublisher();
        var runner = new TaskEventRelayRunner(store, publisher, new FrozenTime(Zero), Options(2, 4));

        var result = await runner.RunOnceAsync();

        Assert.Equal(4, result.EventsRead);
        Assert.Equal(4, result.EventsAcknowledged);
        Assert.True(result.HitRoundLimit);
        Assert.Single(store.Pending);
        Assert.Equal(2, result.PublishCalls);
    }

    [Fact]
    public async Task FailureBeforeReadDoesNotPublishOrAcknowledge()
    {
        var store = new FakeStore([Item(1)]) { FailRead = new InvalidOperationException("read") };
        var publisher = new FakePublisher();
        var runner = new TaskEventRelayRunner(store, publisher, new FrozenTime(Zero), Options(100, 1000));

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunOnceAsync());
        Assert.Equal(0, publisher.Calls);
        Assert.Equal(0, store.AcknowledgeCalls);
        Assert.Single(store.Pending);
    }

    [Fact]
    public async Task FailureDuringPublishLeavesBatchUnacknowledgedForReplay()
    {
        var first = Item(1);
        var store = new FakeStore([first, Item(2)]);
        var publisher = new FakePublisher { Fail = new InvalidOperationException("publish") };
        var runner = new TaskEventRelayRunner(store, publisher, new FrozenTime(Zero), Options(100, 1000));

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunOnceAsync());
        Assert.Equal(1, publisher.Calls);
        Assert.Equal(0, store.AcknowledgeCalls);
        Assert.Equal(2, store.Pending.Count);

        publisher.Fail = null;
        var replay = await runner.RunOnceAsync();
        Assert.Equal(2, replay.EventsAcknowledged);
        Assert.Equal(2, publisher.Calls);
        Assert.Empty(store.Pending);
    }

    [Fact]
    public async Task FailureBeforeAcknowledgeKeepsUnpublishedAndAllowsDuplicatePublish()
    {
        var store = new FakeStore([Item(1), Item(2)]) { FailAcknowledge = new InvalidOperationException("ack") };
        var publisher = new FakePublisher();
        var runner = new TaskEventRelayRunner(store, publisher, new FrozenTime(Zero), Options(100, 1000));

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunOnceAsync());
        Assert.Equal(1, publisher.Calls);
        Assert.Equal(2, store.Pending.Count);

        store.FailAcknowledge = null;
        var replay = await runner.RunOnceAsync();
        Assert.Equal(2, replay.EventsAcknowledged);
        Assert.Equal(2, publisher.Calls);
        Assert.Empty(store.Pending);
    }

    private static TaskEventRelayOptions Options(int batchSize, int maxEventsPerRound) =>
        new(TimeSpan.FromSeconds(1), batchSize, maxEventsPerRound, TimeSpan.FromSeconds(2));

    private static TaskInvalidation Item(int n, DateTimeOffset? occurredAt = null) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{n:D12}"), Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), occurredAt ?? Zero);

    private sealed class FrozenTime(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakePublisher : ITaskRefreshPublisher
    {
        public int Calls { get; private set; }

        public Exception? Fail { get; set; }

        public void Publish()
        {
            Calls++;
            if (Fail is not null)
            {
                throw Fail;
            }
        }
    }

    private sealed class FakeStore(IEnumerable<TaskInvalidation>? pending = null) : ITaskEventStore
    {
        public List<TaskInvalidation> Pending { get; } = [.. pending ?? []];

        public int ReadCalls { get; private set; }

        public int AcknowledgeCalls { get; private set; }

        public int MaxRequestedTake { get; private set; }

        public int MaxAcknowledgementSize { get; private set; }

        public Exception? FailRead { get; set; }

        public Exception? FailAcknowledge { get; set; }

        public Task<IReadOnlyList<TaskInvalidation>> ReadPendingAsync(
            int take,
            CancellationToken token = default)
        {
            ReadCalls++;
            MaxRequestedTake = Math.Max(MaxRequestedTake, take);
            if (FailRead is not null)
            {
                throw FailRead;
            }

            return Task.FromResult<IReadOnlyList<TaskInvalidation>>(
                [.. Pending.OrderBy(item => item.OccurredAtUtc).ThenBy(item => item.EventId).Take(take)]);
        }

        public Task AcknowledgeAsync(IReadOnlyList<Guid> ids, CancellationToken token = default)
        {
            AcknowledgeCalls++;
            MaxAcknowledgementSize = Math.Max(MaxAcknowledgementSize, ids.Count);
            if (FailAcknowledge is not null)
            {
                throw FailAcknowledge;
            }

            Pending.RemoveAll(item => ids.Contains(item.EventId));
            return Task.CompletedTask;
        }
    }
}
