using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Notifications;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;

namespace DbBackupManager.Worker.Tests;

public sealed class WorkerHostTests
{
    [Fact]
    public async Task InvalidStartupConfigurationStopsBeforeQueueConsumption()
    {
        var builder = WorkerHost.CreateBuilder([]);
        builder.Services.AddSingleton<IWorkerStartupDiagnostics>(new RejectedDiagnostics());
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Equal("Worker 配置检查失败：worker_business_keys_missing。", error.Message);
    }

    private sealed class RejectedDiagnostics : IWorkerStartupDiagnostics
    {
        public string? CheckConfiguration() => "worker_business_keys_missing";
    }
    [Fact]
    public async Task WorkerHostRegistersPlatformDbContextFactoryWithoutOpeningDatabase()
    {
        var builder = WorkerHost.CreateBuilder([]);
        builder.Configuration[$"ConnectionStrings:{PlatformDatabaseServiceCollectionExtensions.ConnectionStringName}"] =
            "Server=localhost;Database=DbBackupManagerWorkerHostOnly;Integrated Security=true;Encrypt=true";
        using var host = builder.Build();
        var factory = host.Services.GetRequiredService<IDbContextFactory<PlatformDbContext>>();

        await using var context = await factory.CreateDbContextAsync();

        Assert.NotNull(context);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
        using var scope = host.Services.CreateScope();
        var evidenceProbe = scope.ServiceProvider.GetRequiredService<ITargetSqlBackupEvidenceProbe>();
        Assert.False(evidenceProbe is ITargetSqlBackupExecutor);
        Assert.DoesNotContain(
            host.Services.GetServices<IHostedService>(),
            service => service.GetType().Name is "BackupRecoveryWorkerService" or "BackupWorkerService");
        Assert.Contains(
            host.Services.GetServices<IHostedService>(),
            service => service.GetType().Name == "BackupPlanWorkerService");
        Assert.Contains(
            host.Services.GetServices<IHostedService>(),
            service => service.GetType().Name == "BackupRetentionWorkerService");
        Assert.Contains(
            host.Services.GetServices<IHostedService>(),
            service => service.GetType().Name == "BackupNotificationWorkerService");
        Assert.DoesNotContain(
            host.Services.GetServices<IHostedService>(),
            service => service.GetType().Name == "BackupScheduleWorkerService");
        Assert.DoesNotContain(
            host.Services.GetServices<IHostedService>(),
            service => service.GetType().Name is "PlanLocationRetentionRunner" or "PlanRemoteStagingCleaner");
        using var retentionScope = host.Services.CreateScope();
        Assert.IsType<BackupFileRetentionRunner>(
            retentionScope.ServiceProvider.GetRequiredService<BackupFileRetentionRunner>());
        Assert.IsType<PlanLocationRetentionRunner>(
            retentionScope.ServiceProvider.GetRequiredService<PlanLocationRetentionRunner>());
        Assert.IsType<PlanRemoteStagingCleaner>(
            retentionScope.ServiceProvider.GetRequiredService<PlanRemoteStagingCleaner>());
        foreach (var service in host.Services.GetServices<IHostedService>())
        {
            Assert.DoesNotContain(
                service.GetType().GetConstructors().SelectMany(constructor => constructor.GetParameters()),
                parameter => parameter.ParameterType == typeof(PlanRemoteStagingCleaner)
                    || parameter.ParameterType == typeof(IPlanLocationRetentionStore));
        }
        using var notificationScope = host.Services.CreateScope();
        Assert.IsType<NotificationDeliveryRunner>(
            notificationScope.ServiceProvider.GetRequiredService<NotificationDeliveryRunner>());
    }

    [Fact]
    public void WorkerHostedServicesDoNotReferenceStagingCleanup()
    {
        var root = FindRepositoryRoot();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "DbBackupManager.Worker"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("PlanRemoteStagingCleaner", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ClaimStagingAsync", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// E03-01 回归：宿主必须把部署脚本注册的服务名固定为 EventLog 事件源，使事件归属确定、
    /// 不依赖框架回退到 .NET Runtime。
    /// </summary>
    [Fact]
    public void WorkerHostConfiguresWindowsEventLogSourceName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var builder = WorkerHost.CreateBuilder([]);
        builder.Configuration[$"ConnectionStrings:{PlatformDatabaseServiceCollectionExtensions.ConnectionStringName}"] =
            "Server=localhost;Database=DbBackupManagerWorkerHostOnly;Integrated Security=true;Encrypt=true";
        using var host = builder.Build();

        var settings = host.Services.GetRequiredService<IOptions<EventLogSettings>>().Value;

        Assert.Equal(WorkerHost.WindowsServiceName, settings.SourceName);
        Assert.Equal("Application", settings.LogName);
    }

    /// <summary>
    /// E03-01 R01 回归：EventLog provider 的默认阈值不继承 Logging:LogLevel:Default。
    /// 平台连接监测用 Warning 记 6001、用 Information 记恢复事件 6002；只设置事件源而不设阈值时
    /// 6002 会被过滤掉。这里用框架自身的 LoggerRuleSelector 走真实过滤规则验证两个级别都放行，
    /// 不以 IOptions 的 SourceName 断言代替。
    /// </summary>
    [Fact]
    public void WorkerHostEventLogRulesAdmitBothPlatformDatabaseMonitorLevels()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var builder = WorkerHost.CreateBuilder([]);
        builder.Configuration[$"ConnectionStrings:{PlatformDatabaseServiceCollectionExtensions.ConnectionStringName}"] =
            "Server=localhost;Database=DbBackupManagerWorkerHostOnly;Integrated Security=true;Encrypt=true";
        using var host = builder.Build();
        var options = host.Services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value;
        var providerName = typeof(EventLogLoggerProvider).FullName!;

        Assert.True(
            Allows(options, typeof(EventLogLoggerProvider), WorkerHost.PlatformDatabaseMonitorCategory, LogLevel.Warning),
            "EventLog 应放行平台连接监测的 6001（Warning）。");
        Assert.True(
            Allows(options, typeof(EventLogLoggerProvider), WorkerHost.PlatformDatabaseMonitorCategory, LogLevel.Information),
            "EventLog 应放行平台连接监测的 6002（Information），否则恢复事件不可见。");
        Assert.False(
            Allows(options, typeof(EventLogLoggerProvider), "DbBackupManager.Infrastructure.Persistence.BackupPlanWorkerStore", LogLevel.Information),
            "其他类别应保持 EventLog 的 Warning 阈值，不被放宽到 Information。");
    }

    /// <summary>用框架自身的规则选择器判定某个提供程序/类别/级别是否放行。</summary>
    private static bool Allows(LoggerFilterOptions options, Type providerType, string category, LogLevel level)
    {
        var selectorType = typeof(LoggerFilterOptions).Assembly
            .GetType("Microsoft.Extensions.Logging.LoggerRuleSelector")
            ?? throw new InvalidOperationException("未找到 LoggerRuleSelector。");
        var select = selectorType.GetMethod("Select", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("未找到 LoggerRuleSelector.Select。");
        object?[] arguments = [options, providerType, category, null, null];
        select.Invoke(null, arguments);
        var minLevel = arguments[3] as LogLevel?;
        if (arguments[4] is Delegate filter)
        {
            var count = filter.Method.GetParameters().Length;
            return count == 3
                ? (bool)filter.DynamicInvoke(providerType.FullName, category, level)!
                : (bool)filter.DynamicInvoke(category, level)!;
        }

        return minLevel is null || level >= minLevel;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DbBackupManager.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}




