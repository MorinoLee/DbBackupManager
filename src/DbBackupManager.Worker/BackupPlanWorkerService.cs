using DbBackupManager.Application.BackupTasks;
using Microsoft.Extensions.Options;

namespace DbBackupManager.Worker;

internal sealed class BackupPlanWorkerOptions
{
    public const string SectionName = "BackupPlans";

    public int LoopIntervalSeconds { get; set; } = 15;

    public TimeSpan LoopInterval => TimeSpan.FromSeconds(LoopIntervalSeconds is >= 1 and <= 3600 ? LoopIntervalSeconds : 15);
}

internal sealed class BackupPlanWorkerService(
    IServiceScopeFactory scopes,
    IPlatformDatabaseReadiness readiness,
    IOptions<BackupPlanWorkerOptions> options,
    ILogger<BackupPlanWorkerService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> Failed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(5807),
        "Worker 本轮计划调度或执行未完成，等待后继续；结果未知只核对，不重发 BACKUP。");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var continued = false;
            try
            {
                await readiness.WaitUntilReadyAsync(stoppingToken);
                using var scope = scopes.CreateScope();
                var round = scope.ServiceProvider.GetRequiredService<BackupPlanWorkerRound>();
                var result = await round.RunOnceAsync(stoppingToken);
                continued = result.Action == BackupPlanWorkerAction.Executed
                    && result.Code == BackupExecutionContractCode.Succeeded;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Failed(logger, exception);
            }

            if (continued)
            {
                continue;
            }

            try
            {
                await Task.Delay(options.Value.LoopInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
