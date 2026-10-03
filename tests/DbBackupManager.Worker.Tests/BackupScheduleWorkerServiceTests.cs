using System.Data.Common;
using DbBackupManager.Application.BackupTasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DbBackupManager.Worker.Tests;

public sealed class BackupScheduleWorkerServiceTests
{
    [Fact]
    public void OptionsRejectInvalidIntervals()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new BackupScheduleWorkerOptions(TimeSpan.Zero, TimeSpan.FromSeconds(60)).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new BackupScheduleWorkerOptions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)).Validate());
        BackupScheduleWorkerOptions.Default.Validate();
        Assert.Equal(TimeSpan.FromSeconds(30), BackupScheduleWorkerOptions.Default.ScanInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), BackupScheduleWorkerOptions.Default.FailureBackoff);
    }

    [Fact]
    public async Task SuccessfulRoundUsesScanIntervalWithoutRealWait()
    {
        var clock = new RecordingTimeProvider();
        var scheduler = new FakeScheduler();
        using var host = CreateHost(clock, scheduler);
        using var cts = new CancellationTokenSource();
        await host.StartAsync(cts.Token);
        await WaitUntil(() => clock.Delays.Count > 0);
        await cts.CancelAsync();
        await host.StopAsync();

        Assert.Contains(TimeSpan.FromSeconds(30), clock.Delays);
        Assert.True(scheduler.Calls >= 1);
    }

    [Fact]
    public async Task PlatformFailureUsesBackoffAndCancellationIsNotFailure()
    {
        var clock = new RecordingTimeProvider();
        var scheduler = new FakeScheduler { Throw = true };
        using var host = CreateHost(clock, scheduler);
        using var cts = new CancellationTokenSource();
        await host.StartAsync(cts.Token);
        await WaitUntil(() => clock.Delays.Count > 0);
        await cts.CancelAsync();
        await host.StopAsync();

        Assert.Contains(TimeSpan.FromSeconds(60), clock.Delays);
        Assert.DoesNotContain(TimeSpan.FromSeconds(30), clock.Delays);
    }

    [Fact]
    public async Task UnavailablePlatformBlocksSchedulingAndStopsWithoutWaitingForSql()
    {
        var readiness = new ControlledReadiness();
        var scheduler = new FakeScheduler();
        using var host = CreateHost(TimeProvider.System, scheduler, readiness);
        await host.StartAsync();
        await readiness.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, scheduler.Calls);
        readiness.Ready.TrySetResult();
        await WaitUntil(() => scheduler.Calls > 0);
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var stillUnavailable = new ControlledReadiness();
        using var waitingHost = CreateHost(TimeProvider.System, new FakeScheduler(), stillUnavailable);
        await waitingHost.StartAsync();
        await stillUnavailable.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await waitingHost.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stillUnavailable.Ready.Task.IsCompleted);
    }

    private static IHost CreateHost(TimeProvider clock, IBackupTaskScheduler scheduler,
        IPlatformDatabaseReadiness? readiness = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(BackupScheduleWorkerOptions.Default);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<IBackupTaskScheduler>(scheduler);
        builder.Services.AddSingleton<IPlatformDatabaseReadiness>(readiness ?? new ControlledReadiness(true));
        builder.Services.AddHostedService<BackupScheduleWorkerService>();
        return builder.Build();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class RecordingTimeProvider : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
            callback(state);
            return new CompletedTimer();
        }

        private sealed class CompletedTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeScheduler : IBackupTaskScheduler
    {
        public bool Throw { get; set; }
        public int Calls { get; private set; }

        public Task<BackupScheduleCycleResult> RunOnceAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Throw)
            {
                throw new FakeDbException();
            }

            return Task.FromResult(new BackupScheduleCycleResult([]));
        }
    }

    private sealed class FakeDbException() : DbException("platform-db");

    private sealed class ControlledReadiness : IPlatformDatabaseReadiness
    {
        public ControlledReadiness(bool ready = false)
        {
            if (ready) Ready.TrySetResult();
        }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsReady => Ready.Task.IsCompletedSuccessfully;
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            Waiting.TrySetResult();
            return Ready.Task.WaitAsync(cancellationToken);
        }
    }
}
