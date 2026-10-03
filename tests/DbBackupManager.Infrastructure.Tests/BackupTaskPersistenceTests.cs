using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupTaskPersistenceTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulOperationAndTransientRetryDisposeEveryContext(bool transientFailure)
    {
        using var provider = database.CreateServiceProvider();
        var factory = new RecordingFactory(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>());
        var persistence = new BackupTaskPersistence(factory);
        var operations = new List<PlatformDbContext>();

        var result = await persistence.ExecuteWithStrategyAsync(async context =>
        {
            foreach (var previous in operations)
            {
                AssertDisposed(previous);
            }

            operations.Add(context);
            await context.BackupTasks.CountAsync();
            if (transientFailure && operations.Count == 1)
            {
                throw new TimeoutException("合成的瞬时数据库故障。");
            }

            return "completed";
        }, CancellationToken.None);

        Assert.Equal("completed", result);
        Assert.Equal(transientFailure ? 2 : 1, operations.Count);
        Assert.Equal(operations.Count, operations.Select(context => context.ContextId.InstanceId).Distinct().Count());
        Assert.All(factory.Contexts, AssertDisposed);
    }

    [Fact]
    public async Task PermanentFailurePropagatesAndDisposesEveryContextWithoutRetry()
    {
        using var provider = database.CreateServiceProvider();
        var factory = new RecordingFactory(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>());
        var persistence = new BackupTaskPersistence(factory);
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => persistence.ExecuteWithStrategyAsync<int>(async context =>
        {
            calls++;
            await context.BackupTasks.CountAsync();
            throw new InvalidOperationException("合成的非瞬时故障。");
        }, CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.NotEmpty(factory.Contexts);
        Assert.All(factory.Contexts, AssertDisposed);
    }

    private static void AssertDisposed(PlatformDbContext context) =>
        Assert.Throws<ObjectDisposedException>(() => context.ChangeTracker.Entries().ToArray());

    private sealed class RecordingFactory(IDbContextFactory<PlatformDbContext> inner)
        : IDbContextFactory<PlatformDbContext>
    {
        public List<PlatformDbContext> Contexts { get; } = [];

        public PlatformDbContext CreateDbContext()
        {
            var context = inner.CreateDbContext();
            Contexts.Add(context);
            return context;
        }

        public async Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            var context = await inner.CreateDbContextAsync(cancellationToken);
            Contexts.Add(context);
            return context;
        }
    }
}
