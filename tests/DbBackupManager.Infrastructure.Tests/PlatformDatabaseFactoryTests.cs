using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class PlatformDatabaseFactoryTests
{
    [Fact]
    public async Task FactoryCreatesIndependentNonPooledContexts()
    {
        using var provider = CreateServices(
            "Server=localhost;Database=DbBackupManagerFactoryOnly;Integrated Security=true;Encrypt=true");
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();

        Assert.Null(provider.GetService<PlatformDbContext>());

        await using var first = await factory.CreateDbContextAsync();
        await using var second = await factory.CreateDbContextAsync();

        Assert.NotSame(first, second);
        Assert.Empty(first.ChangeTracker.Entries());
        Assert.Equal(QueryTrackingBehavior.NoTracking, first.ChangeTracker.QueryTrackingBehavior);
        Assert.DoesNotContain("Pooled", factory.GetType().Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolvingFactoryWithoutConnectionStringFailsWithoutLeakingAValue()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddPlatformDatabase(configuration);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>());

        Assert.Contains("ConnectionStrings:PlatformDatabase", exception.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider CreateServices(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{PlatformDatabaseServiceCollectionExtensions.ConnectionStringName}"] =
                    connectionString,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddPlatformDatabase(configuration);
        return services.BuildServiceProvider();
    }
}
