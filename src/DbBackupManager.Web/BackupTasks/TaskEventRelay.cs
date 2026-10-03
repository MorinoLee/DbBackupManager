using System.Collections.Concurrent;
using DbBackupManager.Application.BackupTasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbBackupManager.Web.BackupTasks;

public sealed class TaskRefreshNotifier : ITaskRefreshPublisher
{
    private static readonly Action<ILogger, Exception?> SubscriberFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(5703),
        "任务刷新订阅者失败，已隔离并继续通知其余组件。");

    private readonly ConcurrentDictionary<Guid, Action> _handlers = new();
    private readonly ILogger _logger;

    public TaskRefreshNotifier(ILogger<TaskRefreshNotifier>? logger = null)
    {
        _logger = logger ?? NullLogger<TaskRefreshNotifier>.Instance;
    }

    public IDisposable Subscribe(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var id = Guid.NewGuid();
        _handlers[id] = handler;
        return new Subscription(() => _handlers.TryRemove(id, out _));
    }

    public void Publish()
    {
        foreach (var handler in _handlers.Values)
        {
            try
            {
                handler();
            }
            catch (Exception exception)
            {
                SubscriberFailed(_logger, exception);
            }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

internal sealed class TaskEventRelay(
    IServiceScopeFactory scopes,
    IPlatformDatabaseReadiness readiness,
    ITaskRefreshPublisher publisher,
    TaskEventRelayOptions options,
    TimeProvider clock,
    ILogger<TaskEventRelay> logger) : BackgroundService
{
    private static readonly Action<ILogger, int, int, int, int, bool, double?, Exception?> RoundCompleted =
        LoggerMessage.Define<int, int, int, int, bool, double?>(
            LogLevel.Information,
            new EventId(5707),
            "任务事件中继本轮完成：eventsRead={EventsRead}, eventsAcknowledged={EventsAcknowledged}, batches={Batches}, publishCalls={PublishCalls}, hitRoundLimit={HitRoundLimit}, oldestEventDelayMs={OldestEventDelayMs}。");

    private static readonly Action<ILogger, Exception?> Failed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(5702),
        "任务通知暂不可用，将重试未确认的事件。");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = options.PollInterval;
            try
            {
                await readiness.WaitUntilReadyAsync(stoppingToken);
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<ITaskEventStore>();
                var runner = new TaskEventRelayRunner(store, publisher, clock, options);
                var result = await runner.RunOnceAsync(stoppingToken);
                if (result.EventsRead > 0 || result.HitRoundLimit)
                {
                    RoundCompleted(
                        logger,
                        result.EventsRead,
                        result.EventsAcknowledged,
                        result.Batches,
                        result.PublishCalls,
                        result.HitRoundLimit,
                        result.OldestEventDelay?.TotalMilliseconds,
                        null);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                Failed(logger, null);
                delay = options.ErrorBackoff;
            }

            try
            {
                await Task.Delay(delay, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
