using DbBackupManager.Application.TargetSql;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class TargetSqlAdapterRegistrationTests
{
    [Fact]
    public void RegistersSeparateReadOnlyAndBackupCapabilitiesWithoutOpeningDatabases()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{PlatformDatabaseServiceCollectionExtensions.ConnectionStringName}"] =
                    "Server=localhost;Database=DbBackupManagerRegistrationOnly;Integrated Security=true;Encrypt=true",
                [BusinessCredentialDataProtector.KeyRingPathConfigurationKey] =
                    Path.Combine(Path.GetTempPath(), "DbBackupManagerP56RegistrationOnly"),
            })
            .Build();
        var services = new ServiceCollection();
        services.AddPlatformDatabase(configuration);
        services.AddTargetSqlAdapter(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var probe = scope.ServiceProvider.GetRequiredService<ITargetSqlReadOnlyProbe>();
        var evidence = scope.ServiceProvider.GetRequiredService<ITargetSqlBackupEvidenceProbe>();
        var executor = scope.ServiceProvider.GetRequiredService<ITargetSqlBackupExecutor>();
        var planExecutor = scope.ServiceProvider.GetRequiredService<ITargetSqlPlanBackupExecutor>();

        Assert.IsType<SqlClientTargetSqlReadOnlyProbe>(probe);
        Assert.IsType<SqlClientTargetSqlBackupExecutor>(executor);
        Assert.Same(probe, evidence);
        Assert.NotSame(probe, executor);
        Assert.Same(executor, planExecutor);
        Assert.False(Directory.Exists(configuration[
            BusinessCredentialDataProtector.KeyRingPathConfigurationKey]!));
    }
}
