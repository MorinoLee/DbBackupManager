using DbBackupManager.Web.BackupTasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbBackupManager.Web.Tests;

public sealed class TaskRefreshNotifierTests
{
    [Fact]
    public void PublishIsolatesSubscriberFailuresAndStillNotifiesOthers()
    {
        var notifier = new TaskRefreshNotifier(NullLogger<TaskRefreshNotifier>.Instance);
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
        var notifier = new TaskRefreshNotifier();
        var calls = 0;
        var subscription = notifier.Subscribe(() => calls++);
        subscription.Dispose();

        notifier.Publish();

        Assert.Equal(0, calls);
    }
}
