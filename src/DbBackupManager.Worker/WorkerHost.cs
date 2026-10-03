using DbBackupManager.Infrastructure.BackupExecution;
using DbBackupManager.Infrastructure.Hosting;
using DbBackupManager.Infrastructure.Notifications;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.TargetSql;

namespace DbBackupManager.Worker;

public static class WorkerHost
{
    public const string WindowsServiceName = "DbBackupManager.Worker";

    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        ExternalJsonConfiguration.AddIfConfigured(builder.Configuration, AppContext.BaseDirectory);
        builder.Services.AddWindowsService(options =>
            options.ServiceName = WindowsServiceName);
        builder.Services.AddPlatformDatabase(builder.Configuration);
        builder.Services.AddTargetSqlAdapter(builder.Configuration);
        builder.Services.AddBackupExecution();
        builder.Services.AddSmtpNotificationManagement();
        builder.Services.AddHostedService<WorkerStartupService>();
        builder.Services.AddPlatformDatabaseReadiness();
        builder.Services.AddHostedService<WorkerHeartbeatService>();
        builder.Services.AddHostedService<BackupRecoveryWorkerService>();
        builder.Services.AddHostedService<BackupWorkerService>();
        builder.Services.AddHostedService<BackupRetentionWorkerService>();
        builder.Services.AddHostedService<BackupNotificationWorkerService>();
        builder.Services.AddHostedService<BackupScheduleWorkerService>();
        return builder;
    }
}
