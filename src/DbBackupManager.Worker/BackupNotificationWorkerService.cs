using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Notifications;

namespace DbBackupManager.Worker;

internal sealed class BackupNotificationWorkerService(
    IServiceScopeFactory scopes,
    IPlatformDatabaseReadiness readiness,
    ILogger<BackupNotificationWorkerService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> Failed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(5806),
        "Worker 本轮通知投递未完成，等待后重新检查 Outbox；SMTP 未配置时保留待发记录。");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await readiness.WaitUntilReadyAsync(stoppingToken);
                using var scope = scopes.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<NotificationDeliveryRunner>();
                if (await runner.RunOnceAsync(stoppingToken))
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Failed(logger, exception);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
