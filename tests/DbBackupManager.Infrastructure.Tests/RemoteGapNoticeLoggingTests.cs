using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class RemoteGapNoticeLoggingTests
{
    [Fact]
    public async Task ProjectionFailureLogsTheExceptionTypeWithoutItsText()
    {
        const string secret = "Password=synthetic-secret;Data Source=synthetic-host;D:\\synthetic-path";
        var logger = new ListLogger();
        await RemoteGapNotices.TryProjectAsync(
            new ThrowingFactory(secret),
            new RemoteGapNotices.Observation(
                Guid.NewGuid(),
                Guid.NewGuid(),
                BackupTaskStage.Cleanup,
                DateTimeOffset.UnixEpoch,
                [Guid.NewGuid()]),
            CancellationToken.None,
            logger);

        var message = Assert.Single(logger.Messages);
        Assert.Contains("远端缺口投影失败", message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret", message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-host", message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-path", message, StringComparison.Ordinal);
        Assert.Null(logger.Exceptions.Single());
    }

    private sealed class ThrowingFactory(string detail) : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => throw new InvalidOperationException(detail);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<PlatformDbContext>(new InvalidOperationException(detail));
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public List<Exception?> Exceptions { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }

        private sealed class EmptyScope : IDisposable
        {
            public static readonly EmptyScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
