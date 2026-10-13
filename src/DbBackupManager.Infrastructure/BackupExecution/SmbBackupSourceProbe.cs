using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Infrastructure.FileStorage;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.BackupExecution;

public static class BackupExecutionRegistration
{
    public static IServiceCollection AddBackupExecution(this IServiceCollection services)
    {
        services.AddBackupFileStorage();
        services.AddSingleton<IWorkerStartupDiagnostics, WorkerStartupDiagnostics>();
        services.AddScoped<IBackupFileRetentionStore, BackupFileRetentionStore>();
        services.AddScoped<BackupFileRetentionRunner>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(BackupFileRetentionOptions.Default);
        return services;
    }
}
