using DbBackupManager.Application.FileStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DbBackupManager.Infrastructure.FileStorage;

public static class FileStorageServiceCollectionExtensions
{
    public static IServiceCollection AddBackupFileStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IFileStorageCredentialResolver, FileStorageCredentialResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IFileStorageProtocolSessionFactory, SmbFileStorageSessionFactory>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IFileStorageProtocolSessionFactory, SftpFileStorageSessionFactory>());
        services.TryAddScoped<BackupFileStorageAdapter>();
        services.TryAddScoped<IBackupFileStorageProbe>(provider =>
            provider.GetRequiredService<BackupFileStorageAdapter>());
        services.TryAddScoped<IBackupDirectoryPreparer>(provider =>
            provider.GetRequiredService<BackupFileStorageAdapter>());
        services.TryAddScoped<IBackupFileTransferExecutor>(provider =>
            provider.GetRequiredService<BackupFileStorageAdapter>());
        services.TryAddScoped<IBackupFileDeletionExecutor>(provider =>
            provider.GetRequiredService<BackupFileStorageAdapter>());
        return services;
    }
}
