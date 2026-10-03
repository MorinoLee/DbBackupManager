using DbBackupManager.Application.BackupTasks;
namespace DbBackupManager.Worker;

internal sealed class BackupWorkerService(IServiceScopeFactory scopes, IPlatformDatabaseReadiness readiness, ILogger<BackupWorkerService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> Failed = LoggerMessage.Define(LogLevel.Warning, new EventId(5701), "Worker 本轮未完成，等待后重新检查队列；旧任务由租约恢复流程处理。");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await readiness.WaitUntilReadyAsync(stoppingToken);
                using var scope = scopes.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<BackupTaskRunner>();
                if (await runner.RunOnceAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { Failed(logger, null); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
