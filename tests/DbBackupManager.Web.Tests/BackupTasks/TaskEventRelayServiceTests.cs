using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Web.BackupTasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbBackupManager.Web.Tests.BackupTasks;

public sealed class TaskEventRelayServiceTests
{
    private static readonly TaskEventRelayOptions Options = new(
        TimeSpan.FromSeconds(1),
        100,
        1_000,
        TimeSpan.FromSeconds(2));

    [Fact]
    public async Task NonEmptyRoundLogsOnlyStableAggregateFields()
    {
        var now = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        var eventId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var taskId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var clock = new RecordingTimeProvider(now);
        var store = new FakeStore(
        [
            new TaskInvalidation(eventId, taskId, now - TimeSpan.FromSeconds(5)),
        ]);
        var publisher = new FakePublisher();
        var logs = new RecordingLoggerProvider();
        using var host = CreateHost(clock, store, publisher, logs);
        using var cts = new CancellationTokenSource();

        await host.StartAsync(cts.Token);
        await WaitUntil(() => clock.Delays.Count > 0);
        await cts.CancelAsync();
        await host.StopAsync();

        var completed = Assert.Single(logs.Entries, entry => entry.EventId.Id == 5707);
        Assert.Equal(LogLevel.Information, completed.Level);
        Assert.Contains("eventsRead=1", completed.Message, StringComparison.Ordinal);
        Assert.Contains("eventsAcknowledged=1", completed.Message, StringComparison.Ordinal);
        Assert.Contains("batches=1", completed.Message, StringComparison.Ordinal);
        Assert.Contains("publishCalls=1", completed.Message, StringComparison.Ordinal);
        Assert.Contains("hitRoundLimit=False", completed.Message, StringComparison.Ordinal);
        Assert.Contains("oldestEventDelayMs=5000", completed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(eventId.ToString(), completed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(taskId.ToString(), completed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(completed.Exception);
        Assert.Equal(1, publisher.PublishCalls);
    }

    [Fact]
    public async Task EmptyRoundDoesNotWriteCompletedLog()
    {
        var clock = new RecordingTimeProvider(DateTimeOffset.UtcNow);
        var store = new FakeStore([]);
        var logs = new RecordingLoggerProvider();
        using var host = CreateHost(clock, store, new FakePublisher(), logs);
        using var cts = new CancellationTokenSource();

        await host.StartAsync(cts.Token);
        await WaitUntil(() => clock.Delays.Count > 0);
        await cts.CancelAsync();
        await host.StopAsync();

        Assert.DoesNotContain(logs.Entries, entry => entry.EventId.Id == 5707);
        Assert.DoesNotContain(logs.Entries, entry => entry.EventId.Id == 5702);
    }

    [Fact]
    public async Task StoreFailureUsesBackoffAndDoesNotExposeRawException()
    {
        var clock = new RecordingTimeProvider(DateTimeOffset.UtcNow);
        var store = new FakeStore([])
        {
            Exception = new InvalidOperationException("Server=secret-host;Database=secret-db"),
        };
        var logs = new RecordingLoggerProvider();
        using var host = CreateHost(clock, store, new FakePublisher(), logs);
        using var cts = new CancellationTokenSource();

        await host.StartAsync(cts.Token);
        await WaitUntil(() => clock.Delays.Count > 0);
        await cts.CancelAsync();
        await host.StopAsync();

        Assert.Contains(Options.ErrorBackoff, clock.Delays);
        Assert.DoesNotContain(Options.PollInterval, clock.Delays);
        var warning = Assert.Single(logs.Entries, entry => entry.EventId.Id == 5702);
        Assert.DoesNotContain("secret-host", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-db", warning.Message, StringComparison.Ordinal);
        Assert.Null(warning.Exception);
        Assert.DoesNotContain(logs.Entries, entry => entry.EventId.Id == 5707);
    }

    private static IHost CreateHost(
        TimeProvider clock,
        ITaskEventStore store,
        ITaskRefreshPublisher publisher,
        ILoggerProvider loggerProvider)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(loggerProvider);
        builder.Services.AddSingleton(Options);
        builder.Services.AddSingleton(clock);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(publisher);
        builder.Services.AddSingleton<IPlatformDatabaseReadiness>(new PlatformReadinessStub());
        builder.Services.AddHostedService<TaskEventRelay>();
        return builder.Build();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var index = 0; index < 50 && !condition(); index++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class RecordingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];

        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            Delays.Add(dueTime);
            return new CompletedTimer();
        }

        private sealed class CompletedTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeStore(IEnumerable<TaskInvalidation> pending) : ITaskEventStore
    {
        private readonly List<TaskInvalidation> _pending = [.. pending];

        public Exception? Exception { get; set; }

        public Task<IReadOnlyList<TaskInvalidation>> ReadPendingAsync(
            int take,
            CancellationToken token = default)
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult<IReadOnlyList<TaskInvalidation>>([.. _pending.Take(take)]);
        }

        public Task AcknowledgeAsync(
            IReadOnlyList<Guid> ids,
            CancellationToken token = default)
        {
            _pending.RemoveAll(item => ids.Contains(item.EventId));
            return Task.CompletedTask;
        }
    }

    private sealed class FakePublisher : ITaskRefreshPublisher
    {
        public int PublishCalls { get; private set; }

        public void Publish() => PublishCalls++;
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(List<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Add(new(logLevel, eventId, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception? Exception);
}
