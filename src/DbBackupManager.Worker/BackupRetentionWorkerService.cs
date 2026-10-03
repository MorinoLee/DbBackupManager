using DbBackupManager.Application.BackupTasks;

namespace DbBackupManager.Worker;

internal sealed class BackupRetentionWorkerService(
    IServiceScopeFactory scopes,
    IPlatformDatabaseReadiness readiness,
    ILogger<BackupRetentionWorkerService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> Failed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(5805),
        "Worker 本轮 Retention 未完成，等待后重新检查候选；不会扫描目录或删除未登记文件。");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await readiness.WaitUntilReadyAsync(stoppingToken);
                using var scope = scopes.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<BackupFileRetentionRunner>();
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
