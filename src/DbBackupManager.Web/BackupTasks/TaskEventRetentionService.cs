using System.Data.Common;
using DbBackupManager.Application.BackupTasks;

namespace DbBackupManager.Web.BackupTasks;

/// <summary>
/// 已发布 TaskEvent 的保留清理循环。与 <see cref="TaskEventRelay"/> 同宿主但不耦合：
/// 清理只删除 Relay 已确认的派生通知事件，不参与投递，也不依赖 Relay 是否在运行。
/// </summary>
internal sealed class TaskEventRetentionService(
    IServiceScopeFactory scopes,
    IPlatformDatabaseReadiness readiness,
    TimeProvider clock,
    TaskEventRetentionOptions options,
    ILogger<TaskEventRetentionService> logger) : BackgroundService
{
    private static readonly Action<ILogger, int, int, int, bool, DateTimeOffset?, DateTimeOffset?, Exception?> Completed =
        LoggerMessage.Define<int, int, int, bool, DateTimeOffset?, DateTimeOffset?>(
            LogLevel.Information,
            new EventId(5705),
            "TaskEvent 清理本轮完成：window={WindowDeleted}, cap={CapDeleted}, batches={Batches}, "
            + "hitLimit={HitRoundLimit}, oldest={OldestOccurredAtUtc}, newest={NewestOccurredAtUtc}。");

    private static readonly Action<ILogger, string, bool, Exception?> Failed =
        LoggerMessage.Define<string, bool>(
            LogLevel.Warning,
            new EventId(5706),
            "TaskEvent 清理失败：reasonCode={ReasonCode}, failureBackoff={FailureBackoff}；"
            + "不影响任务通知与备份执行。");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = options.ScanInterval;
            try
            {
                await readiness.WaitUntilReadyAsync(stoppingToken);
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<ITaskEventRetentionStore>();
                var runner = new TaskEventRetentionRunner(store, clock, options);
                var result = await runner.RunOnceAsync(stoppingToken);
                if (result.TotalDeleted > 0 || result.HitRoundLimit)
                {
                    Completed(
                        logger,
                        result.WindowDeleted,
                        result.CapDeleted,
                        result.Batches,
                        result.HitRoundLimit,
                        result.OldestDeletedOccurredAtUtc,
                        result.NewestDeletedOccurredAtUtc,
                        null);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Failed(logger, FailureReasonCode(exception), true, null);
                delay = options.FailureBackoff;
            }

            try
            {
                await Task.Delay(delay, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static string FailureReasonCode(Exception exception) => exception switch
    {
        TaskEventRetentionOperationException operation => operation.FailureCode switch
        {
            TaskEventRetentionFailureCode.StoreUnavailable => "retention_store_unavailable",
            TaskEventRetentionFailureCode.DeleteFailed => "retention_delete_failed",
            TaskEventRetentionFailureCode.ConfigurationInvalid => "retention_configuration_invalid",
            _ => "retention_unexpected_failure",
        },
        DbException or TimeoutException => "retention_store_unavailable",
        _ => "retention_unexpected_failure",
    };
}
