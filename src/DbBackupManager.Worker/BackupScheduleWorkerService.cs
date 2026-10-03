using DbBackupManager.Application.BackupTasks;

namespace DbBackupManager.Worker;

internal sealed class BackupScheduleWorkerService(
    IServiceScopeFactory scopes,
    IPlatformDatabaseReadiness readiness,
    TimeProvider clock,
    BackupScheduleWorkerOptions options,
    ILogger<BackupScheduleWorkerService> logger) : BackgroundService
{
    private static readonly Action<ILogger, int, int, int, Exception?> Completed = LoggerMessage.Define<int, int, int>(
        LogLevel.Information,
        new EventId(5901),
        "Worker 本轮计划调度完成：created={Created}, existing={Existing}, skipped={Skipped}。");

    private static readonly Action<ILogger, Guid, string, Exception?> PolicyFailed = LoggerMessage.Define<Guid, string>(
        LogLevel.Warning,
        new EventId(5902),
        "Worker 跳过单个计划策略 {PolicyId}，原因 {ReasonCode}。");

    private static readonly Action<ILogger, Exception?> RoundFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(5903),
        "Worker 本轮计划调度因 Platform DB 读取失败未完成，将按失败退避后重试。");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();
        while (!stoppingToken.IsCancellationRequested)
        {
            var failed = false;
            try
            {
                await readiness.WaitUntilReadyAsync(stoppingToken);
                using var scope = scopes.CreateScope();
                var scheduler = scope.ServiceProvider.GetRequiredService<IBackupTaskScheduler>();
                var result = await scheduler.RunOnceAsync(stoppingToken);
                var created = result.Policies.Count(item => item.Code == BackupSchedulePolicyResultCode.Created);
                var existing = result.Policies.Count(item => item.Code == BackupSchedulePolicyResultCode.AlreadyExists);
                Completed(logger, created, existing, result.Policies.Count - created - existing, null);
                foreach (var policy in result.Policies.Where(item => item.Code
                             is BackupSchedulePolicyResultCode.InvalidTimeZone
                             or BackupSchedulePolicyResultCode.ConfigurationUnavailable
                             or BackupSchedulePolicyResultCode.CreateFailed))
                {
                    PolicyFailed(logger, policy.PolicyId, policy.Code.ToString(), null);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                RoundFailed(logger, exception);
                failed = true;
            }

            try
            {
                await Task.Delay(failed ? options.FailureBackoff : options.ScanInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
