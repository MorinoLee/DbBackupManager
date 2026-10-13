using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Notifications;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
