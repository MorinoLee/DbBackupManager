using DbBackupManager.Application.BackupTasks;

namespace DbBackupManager.Worker;

internal sealed class BackupRecoveryWorkerService(
    IServiceScopeFactory scopes,
    IPlatformDatabaseReadiness readiness,
    ILogger<BackupRecoveryWorkerService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> Failed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(5702),
        "Worker 本轮只读核对未完成，租约到期后将重新检查；不会自动重发 BACKUP。");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await readiness.WaitUntilReadyAsync(stoppingToken);
                using var scope = scopes.CreateScope();
                var recovery = scope.ServiceProvider.GetRequiredService<IBackupTaskRecovery>();
                if (await recovery.ReconcileOnceAsync(stoppingToken))
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
