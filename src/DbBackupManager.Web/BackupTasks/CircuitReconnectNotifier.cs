using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbBackupManager.Web.BackupTasks;

/// <summary>
/// 仅表达“Circuit 已从断线中恢复”的进程内失效提示，不携带任务状态或敏感数据。
/// </summary>
public sealed class CircuitReconnectNotifier
{
    private static readonly Action<ILogger, Exception?> SubscriberFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(5704),
        "电路重连订阅者失败，已隔离并继续通知其余组件。");

    private readonly ConcurrentDictionary<Guid, Action> _handlers = new();
    private readonly ILogger _logger;

    public CircuitReconnectNotifier(ILogger<CircuitReconnectNotifier>? logger = null)
    {
        _logger = logger ?? NullLogger<CircuitReconnectNotifier>.Instance;
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

/// <summary>
/// 按 Circuit 作用域工作：首次连接不发布；仅在实际断线后再恢复时通知监控页面重查事实。
/// </summary>
internal sealed class MonitoringCircuitHandler(CircuitReconnectNotifier notifier) : CircuitHandler, IDisposable
{
    private bool _disconnected;
    private bool _disposed;

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _ = circuit;
        _ = cancellationToken;
        if (!_disposed)
        {
            _disconnected = true;
        }

        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _ = circuit;
        _ = cancellationToken;
        if (_disposed || !_disconnected)
        {
            return Task.CompletedTask;
        }

        _disconnected = false;
        notifier.Publish();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
