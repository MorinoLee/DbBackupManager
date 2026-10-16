using System.Runtime.Versioning;
using DbBackupManager.Infrastructure.BackupExecution;
using DbBackupManager.Infrastructure.Hosting;
using DbBackupManager.Infrastructure.Notifications;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.Extensions.Logging.EventLog;

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
        // AddWindowsService 只挂 EventLog provider，并不填充 SourceName。空源名会让 EventLogLogger
        // 写入失败，6001/6002/6101 等应用日志因此进不了 Windows Application 日志。这里显式指定
        // 部署脚本已注册的服务名作为事件源，保证服务身份下的运维日志能够持久留存。
        if (OperatingSystem.IsWindows())
        {
            ConfigureEventLogSource(builder, WindowsServiceName);
        }
        builder.Services.AddPlatformDatabase(builder.Configuration);
        builder.Services.AddTargetSqlAdapter(builder.Configuration);
        builder.Services.AddBackupExecution();
        builder.Services.AddSmtpNotificationManagement();
        builder.Services.AddHostedService<WorkerStartupService>();
        builder.Services.AddPlatformDatabaseReadiness();
        builder.Services.AddHostedService<WorkerHeartbeatService>();
        builder.Services.Configure<BackupPlanWorkerOptions>(
            builder.Configuration.GetSection(BackupPlanWorkerOptions.SectionName));
        builder.Services.AddHostedService<BackupPlanWorkerService>();
        builder.Services.AddHostedService<BackupRetentionWorkerService>();
        builder.Services.AddHostedService<BackupNotificationWorkerService>();
        return builder;
    }

    /// <summary>
    /// 为 EventLog provider 指定已由部署脚本注册的服务名作为事件源。AddWindowsService 只挂
    /// provider 而不填 SourceName，空源名会让 EventLogLogger 写入失败，6001/6002/6101 等应用
    /// 日志因此进不了 Windows Application 日志。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void ConfigureEventLogSource(HostApplicationBuilder builder, string sourceName) =>
        builder.Services.Configure<EventLogSettings>(settings =>
        {
            settings.SourceName ??= sourceName;
            settings.LogName ??= "Application";
        });
}

