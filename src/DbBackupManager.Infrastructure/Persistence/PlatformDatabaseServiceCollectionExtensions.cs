using DbBackupManager.Application.BackupSets;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DbBackupManager.Infrastructure.Persistence;

public static class PlatformDatabaseServiceCollectionExtensions
{
    public const string ConnectionStringName = "PlatformDatabase";

    public const int CompatibilityLevel = 150;

    public static IServiceCollection AddPlatformDatabase(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDbContextFactory<PlatformDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString(ConnectionStringName);

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"缺少 ConnectionStrings:{ConnectionStringName}，请通过 User Secrets 或部署环境安全配置。");
            }

            options
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .UseSqlServer(connectionString, sqlServer =>
                {
                    sqlServer.UseCompatibilityLevel(CompatibilityLevel);
                    sqlServer.EnableRetryOnFailure(3);
                });
        });
        services.RemoveAll<PlatformDbContext>();
        services.TryAddScoped<IAdminIdentityStore, AdminIdentityStore>();
        services.TryAddScoped<IBackupTaskExecutionStore, BackupTaskExecutionStore>();
        services.TryAddScoped<IBackupSetRegistrationStore, BackupSetRegistrationStore>();
        services.TryAddScoped<BackupSetRegistrationService>();
        services.TryAddScoped<IWorkerHeartbeatStore, BackupManagement.WorkerHeartbeatStore>();

        return services;
    }
}
