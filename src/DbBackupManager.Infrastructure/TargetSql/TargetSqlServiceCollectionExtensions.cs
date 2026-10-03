using DbBackupManager.Application.TargetSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DbBackupManager.Infrastructure.TargetSql;

public static class TargetSqlServiceCollectionExtensions
{
    public static IServiceCollection AddTargetSqlAdapter(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<IBusinessCredentialProtector>(_ =>
            new BusinessCredentialDataProtector(
                configuration[BusinessCredentialDataProtector.KeyRingPathConfigurationKey]));
        services.TryAddScoped<ITargetSqlCredentialResolver, SqlPasswordCredentialResolver>();
        services.TryAddSingleton<ITargetSqlClientSessionFactory, SqlClientTargetSqlSessionFactory>();
        services.TryAddSingleton<ITargetSqlBackupClientSessionFactory, SqlClientTargetSqlBackupSessionFactory>();
        services.TryAddSingleton<ITargetSqlBackupCommandObserver, NoOpTargetSqlBackupCommandObserver>();
        services.TryAddScoped<SqlClientTargetSqlReadOnlyProbe>();
        services.TryAddScoped<ITargetSqlReadOnlyProbe>(provider =>
            provider.GetRequiredService<SqlClientTargetSqlReadOnlyProbe>());
        services.TryAddScoped<ITargetSqlBackupEvidenceProbe>(provider =>
            provider.GetRequiredService<SqlClientTargetSqlReadOnlyProbe>());
        services.TryAddScoped<ITargetSqlBackupExecutor, SqlClientTargetSqlBackupExecutor>();

        return services;
    }
}

internal sealed class NoOpTargetSqlBackupCommandObserver : ITargetSqlBackupCommandObserver
{
    public ValueTask OnCommandStartedAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
