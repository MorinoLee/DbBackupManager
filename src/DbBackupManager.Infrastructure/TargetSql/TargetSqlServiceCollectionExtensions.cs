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

        services.AddLogging();
        // 注册时校验配置，避免缺失原因的受控例外延迟到 BACKUP 执行时才暴露。
        _ = new DifferentialBackupAllowance(
            configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger<DifferentialBackupAllowance>.Instance);
        services.TryAddSingleton(provider => new DifferentialBackupAllowance(
            configuration, provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DifferentialBackupAllowance>>()));

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
        services.TryAddScoped<SqlClientTargetSqlBackupExecutor>();
        services.TryAddScoped<ITargetSqlBackupExecutor>(provider => provider.GetRequiredService<SqlClientTargetSqlBackupExecutor>());
        services.TryAddScoped<ITargetSqlPlanBackupExecutor>(provider => provider.GetRequiredService<SqlClientTargetSqlBackupExecutor>());

        return services;
    }
}

internal sealed class NoOpTargetSqlBackupCommandObserver : ITargetSqlBackupCommandObserver
{
    public ValueTask OnCommandStartedAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
