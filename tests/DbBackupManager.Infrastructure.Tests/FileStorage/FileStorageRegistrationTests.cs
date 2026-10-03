using DbBackupManager.Application.FileStorage;
using DbBackupManager.Infrastructure.FileStorage;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests.FileStorage;

public sealed class FileStorageRegistrationTests
{
    [Fact]
    public void RegistersSeparatedCapabilitiesWithoutOpeningDatabaseOrKeyRing()
    {
        var keyRingPath = Path.Combine(
            Path.GetTempPath(),
            $"DbBackupManagerP58RegistrationOnly_{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{PlatformDatabaseServiceCollectionExtensions.ConnectionStringName}"] =
                    "Server=localhost;Database=DbBackupManagerP58RegistrationOnly;Integrated Security=true;Encrypt=true",
                [BusinessCredentialDataProtector.KeyRingPathConfigurationKey] = keyRingPath,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPlatformDatabase(configuration);
        services.AddBackupFileStorage();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var probe = scope.ServiceProvider.GetRequiredService<IBackupFileStorageProbe>();
        var directories = scope.ServiceProvider.GetRequiredService<IBackupDirectoryPreparer>();
        var transfer = scope.ServiceProvider.GetRequiredService<IBackupFileTransferExecutor>();
        var deletion = scope.ServiceProvider.GetRequiredService<IBackupFileDeletionExecutor>();
        var protocolFactories = scope.ServiceProvider
            .GetServices<IFileStorageProtocolSessionFactory>()
            .OrderBy(factory => factory.Protocol)
            .ToArray();

        Assert.IsType<BackupFileStorageAdapter>(probe);
        Assert.Same(probe, directories);
        Assert.Same(probe, transfer);
        Assert.Same(probe, deletion);
        Assert.Collection(
            protocolFactories,
            factory => Assert.IsType<SmbFileStorageSessionFactory>(factory),
            factory => Assert.IsType<SftpFileStorageSessionFactory>(factory));
        Assert.False(Directory.Exists(keyRingPath));
    }
}
