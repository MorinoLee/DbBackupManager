using System.Data.Common;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Web.BackupTasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbBackupManager.Web.Tests.BackupTasks;

public sealed class TaskEventRetentionServiceTests
{
    private static readonly TaskEventRetentionOptions Options = new(
        TimeSpan.FromDays(30),
        200_000,
        TimeSpan.FromHours(1),
        5_000,
        500,
        TimeSpan.FromHours(2));

    [Fact]
    public async Task SuccessfulRoundUsesScanInterval()
    {
        var clock = new RecordingTimeProvider();
        var store = new FakeStore();
        var logs = new RecordingLoggerProvider();
        using var host = CreateHost(clock, store, logs);
        using var cts = new CancellationTokenSource();
        await host.StartAsync(cts.Token);
        await WaitUntil(() => clock.Delays.Count > 0);
        await cts.CancelAsync();
        await host.StopAsync();

        Assert.Contains(TimeSpan.FromHours(1), clock.Delays);
        Assert.True(store.CountCalls >= 1);
        Assert.DoesNotContain(logs.Entries, entry => entry.EventId.Id == 5706);
    }

    [Fact]
    public async Task StoreFailureUsesBackoffAndLogsOnlyStableReasonCode()
    {
        var clock = new RecordingTimeProvider();
        var store = new FakeStore
        {
            Exception = new FakeDbException("Server=secret-host;Database=secret-db"),
        };
        var logs = new RecordingLoggerProvider();
        using var host = CreateHost(clock, store, logs);
        using var cts = new CancellationTokenSource();
        await host.StartAsync(cts.Token);
        await WaitUntil(() => clock.Delays.Count > 0);
        await cts.CancelAsync();
        await host.StopAsync();

        Assert.Contains(TimeSpan.FromHours(2), clock.Delays);
        Assert.DoesNotContain(TimeSpan.FromHours(1), clock.Delays);
        var warning = Assert.Single(logs.Entries, entry => entry.EventId.Id == 5706);
        Assert.Contains("retention_store_unavailable", warning.Message, StringComparison.Ordinal);
        Assert.Contains("failureBackoff=True", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-host", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-db", warning.Message, StringComparison.Ordinal);
        Assert.Null(warning.Exception);
    }

    [Fact]
    public async Task DeleteFailureUsesStableReasonAndCancellationAddsNoWarning()
    {
        var clock = new RecordingTimeProvider();
        var store = new FakeStore
        {
            Exception = new TaskEventRetentionOperationException(
                TaskEventRetentionFailureCode.DeleteFailed),
        };
        var logs = new RecordingLoggerProvider();
        using var host = CreateHost(clock, store, logs);
        using var cts = new CancellationTokenSource();
        await host.StartAsync(cts.Token);
        await WaitUntil(() => clock.Delays.Count > 0);
        await cts.CancelAsync();
        await host.StopAsync();

        var warning = Assert.Single(logs.Entries, entry => entry.EventId.Id == 5706);
        Assert.Contains("retention_delete_failed", warning.Message, StringComparison.Ordinal);
        Assert.Null(warning.Exception);
    }

    [Fact]
    public void DefaultOptionsAreRegisteredAndValid()
    {
        TaskEventRetentionOptions.Default.Validate();
        Assert.Equal(TimeSpan.FromDays(30), TaskEventRetentionOptions.Default.RetentionWindow);
        Assert.Equal(200_000, TaskEventRetentionOptions.Default.RetentionMaxCount);
    }

    private static IHost CreateHost(
        TimeProvider clock,
        ITaskEventRetentionStore store,
        ILoggerProvider? loggerProvider = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        if (loggerProvider is not null)
        {
            builder.Logging.AddProvider(loggerProvider);
        }

        builder.Services.AddSingleton(Options);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IPlatformDatabaseReadiness>(new PlatformReadinessStub());
        builder.Services.AddHostedService<TaskEventRetentionService>();
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

    private sealed class RecordingTimeProvider : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];

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

    private sealed class FakeStore : ITaskEventRetentionStore
    {
        public Exception? Exception { get; set; }

        public int CountCalls { get; private set; }

        public Task<int> CountPublishedAsync(CancellationToken cancellationToken = default)
        {
            CountCalls++;
            return Exception is not null
                ? throw Exception
                : Task.FromResult(0);
        }

        public Task<TaskEventRetentionBatch> DeleteExpiredBatchAsync(
            DateTimeOffset cutoffUtc,
            int take,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(TaskEventRetentionBatch.Empty);

        public Task<TaskEventRetentionBatch> DeleteOldestPublishedBatchAsync(
            int take,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(TaskEventRetentionBatch.Empty);
    }

    private sealed class FakeDbException(string message) : DbException(message);

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
