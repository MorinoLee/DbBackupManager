using DbBackupManager.Web.BackupTasks;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbBackupManager.Web.Tests;

public sealed class CircuitReconnectNotifierTests
{
    [Fact]
    public void PublishIsolatesSubscriberFailuresAndStillNotifiesOthers()
    {
        var notifier = new CircuitReconnectNotifier(NullLogger<CircuitReconnectNotifier>.Instance);
        var first = 0;
        var second = 0;
        notifier.Subscribe(() =>
        {
            first++;
            throw new InvalidOperationException("subscriber");
        });
        notifier.Subscribe(() => second++);

        notifier.Publish();

        Assert.Equal(1, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public void DisposedSubscriptionIsNotCalled()
    {
        var notifier = new CircuitReconnectNotifier();
        var calls = 0;
        var subscription = notifier.Subscribe(() => calls++);
        subscription.Dispose();

        notifier.Publish();

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task HandlerDoesNotPublishOnFirstConnection()
    {
        var notifier = new CircuitReconnectNotifier();
        var calls = 0;
        notifier.Subscribe(() => calls++);
        var handler = new MonitoringCircuitHandler(notifier);

        await handler.OnConnectionUpAsync(null!, CancellationToken.None);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task HandlerPublishesOnlyAfterDisconnectThenReconnect()
    {
        var notifier = new CircuitReconnectNotifier();
        var calls = 0;
        notifier.Subscribe(() => calls++);
        var handler = new MonitoringCircuitHandler(notifier);

        await handler.OnConnectionDownAsync(null!, CancellationToken.None);
        await handler.OnConnectionUpAsync(null!, CancellationToken.None);
        await handler.OnConnectionDownAsync(null!, CancellationToken.None);
        await handler.OnConnectionUpAsync(null!, CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DisposedHandlerDoesNotPublishAfterReconnect()
    {
        var notifier = new CircuitReconnectNotifier();
        var calls = 0;
        notifier.Subscribe(() => calls++);
        var handler = new MonitoringCircuitHandler(notifier);

        handler.Dispose();
        await handler.OnConnectionDownAsync(null!, CancellationToken.None);
        await handler.OnConnectionUpAsync(null!, CancellationToken.None);

        Assert.Equal(0, calls);
    }
}
