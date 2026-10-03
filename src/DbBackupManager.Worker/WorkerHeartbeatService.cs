using DbBackupManager.Application.BackupTasks;

namespace DbBackupManager.Worker;

internal sealed class WorkerHeartbeatService(IServiceScopeFactory scopes, IPlatformDatabaseReadiness readiness, ILogger<WorkerHeartbeatService> logger) : BackgroundService
{
    private readonly Guid _processId = Guid.NewGuid();
    private bool _registered;
    private static readonly Action<ILogger, Exception?> Failed = LoggerMessage.Define(LogLevel.Warning,
        new EventId(5704), "Worker 心跳未能写入，请检查平台连接及 Migration；工作台将在心跳过期后显示离线。");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await readiness.WaitUntilReadyAsync(stoppingToken);
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IWorkerHeartbeatStore>();
                if (!_registered) { await store.RegisterAsync(_processId, stoppingToken); _registered = true; }
                else await store.PulseAsync(_processId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { Failed(logger, null); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (!_registered || !readiness.IsReady) return;
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IWorkerHeartbeatStore>().StopAsync(_processId, cancellationToken);
        }
        catch (Exception) { Failed(logger, null); }
    }
}
