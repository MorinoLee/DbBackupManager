using DbBackupManager.Infrastructure.Hosting;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class PlatformDatabaseReadinessTests
{
    [Fact]
    public async Task ReadinessBlocksUntilProbeSucceedsAndCreatesNewWaitAfterConnectionLoss()
    {
        var readiness = new PlatformDatabaseReadiness();
        using var cancellation = new CancellationTokenSource();
        var waiting = readiness.WaitUntilReadyAsync(cancellation.Token);
        Assert.False(readiness.IsReady);
        Assert.False(waiting.IsCompleted);
        readiness.SetAvailable(true);
        await waiting;
        Assert.True(readiness.IsReady);
        readiness.SetAvailable(false);
        var waitingAgain = readiness.WaitUntilReadyAsync(cancellation.Token);
        Assert.False(waitingAgain.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingAgain);
        Assert.False(readiness.IsReady);
    }

    [Fact]
    public async Task MonitorLogsTransitionsAndFiveMinuteReminderWithoutRawFailuresOrCancellationNoise()
    {
        var probe = new ControlledProbe();
        var services = new ServiceCollection().AddSingleton<IPlatformDatabaseProbe>(probe);
        using var provider = services.BuildServiceProvider();
        var readiness = new PlatformDatabaseReadiness();
        var clock = new ManualClock();
        var logger = new RecordingLogger();
        using var monitor = new PlatformDatabaseMonitor(provider.GetRequiredService<IServiceScopeFactory>(), readiness, clock, logger);
        await monitor.ProbeOnceAsync(CancellationToken.None);
        await monitor.ProbeOnceAsync(CancellationToken.None);
        Assert.False(readiness.IsReady);
        Assert.Single(logger.Entries);
        clock.Now += TimeSpan.FromMinutes(5);
        await monitor.ProbeOnceAsync(CancellationToken.None);
        Assert.Equal(2, logger.Entries.Count);
        probe.Available = true;
        await monitor.ProbeOnceAsync(CancellationToken.None);
        await monitor.ProbeOnceAsync(CancellationToken.None);
        Assert.True(readiness.IsReady);
        Assert.Equal(6002, logger.Entries[2].Id);
        probe.Available = false;
        await monitor.ProbeOnceAsync(CancellationToken.None);
        Assert.False(readiness.IsReady);
        Assert.Equal(4, logger.Entries.Count);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.ProbeOnceAsync(cancellation.Token));
        Assert.Equal(4, logger.Entries.Count);
        Assert.All(logger.Entries, entry =>
        {
            Assert.Null(entry.Exception);
            Assert.DoesNotContain("private-secret", entry.Message, StringComparison.Ordinal);
        });
    }

    private sealed class ControlledProbe : IPlatformDatabaseProbe
    {
        public bool Available { get; set; }
        public Task CheckAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Available ? Task.CompletedTask : throw new InvalidOperationException("private-secret");
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingLogger : ILogger<PlatformDatabaseMonitor>
    {
        public List<(int Id, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((eventId.Id, formatter(state, exception), exception));
    }
}

public sealed class PlatformDatabaseProbeSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Fact]
    public async Task ProbeUsesIsolatedPlatformDatabaseWithoutChangingBusinessFactoryConnection()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        await using var before = await factory.CreateDbContextAsync();
        var original = before.Database.GetConnectionString();
        await new PlatformDatabaseProbe(factory).CheckAsync(CancellationToken.None);
        await using var after = await factory.CreateDbContextAsync();
        Assert.Equal(original, after.Database.GetConnectionString());
        Assert.Equal(System.Data.ConnectionState.Closed, after.Database.GetDbConnection().State);
    }
}
