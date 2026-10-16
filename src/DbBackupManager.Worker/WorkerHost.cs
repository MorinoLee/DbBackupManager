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
        // 显式配置 EventLog provider 的事件源与阈值：事件源用部署脚本注册的服务名，阈值必须给出，
        // 否则恢复事件 6002（Information）会被 EventLog 默认阈值过滤掉。
        if (OperatingSystem.IsWindows())
        {
            ConfigureEventLog(builder, WindowsServiceName);
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

    /// <summary>平台连接监测的日志类别：故障记 6001（Warning），恢复记 6002（Information）。</summary>
    internal const string PlatformDatabaseMonitorCategory = "DbBackupManager.Infrastructure.Hosting.PlatformDatabaseMonitor";

    /// <summary>
    /// 显式配置 EventLog provider 的事件源与过滤阈值。
    /// 事件源固定为部署脚本注册的服务名，使事件日志中的归属确定、不依赖框架回退。
    /// 阈值必须显式给出：EventLog provider 的默认阈值不继承 Logging:LogLevel:Default，
    /// 沿用默认值时恢复事件 6002（Information）会被过滤，只剩故障事件 6001。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void ConfigureEventLog(HostApplicationBuilder builder, string sourceName)
    {
        builder.Services.Configure<EventLogSettings>(settings =>
        {
            settings.SourceName ??= sourceName;
            settings.LogName ??= "Application";
        });
        builder.Logging.AddFilter<EventLogLoggerProvider>(static (_, level) => level >= LogLevel.Warning);
        builder.Logging.AddFilter<EventLogLoggerProvider>(PlatformDatabaseMonitorCategory, LogLevel.Information);
    }
}



