using Microsoft.Extensions.Logging;

namespace DbBackupManager.Application.BackupTasks;

public enum BackupPlanWorkerSelectionKind
{
    Idle = 1,
    Wait = 2,
    Execute = 3,
    Reconcile = 4
}

public sealed record BackupPlanWorkerSelection(BackupPlanWorkerSelectionKind Kind, Guid? TaskId);

public interface IBackupPlanWorkerStore
{
    Task<BackupPlanWorkerSelection> SelectAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

public enum BackupPlanWorkerAction
{
    Idle = 1,
    Wait = 2,
    Executed = 3,
    Reconciled = 4
}

public sealed record BackupPlanWorkerRoundResult(
    int PlansConsidered,
    int ScheduleFailures,
    BackupPlanWorkerAction Action,
    Guid? TaskId,
    BackupExecutionContractCode? Code);

/// <summary>
/// 一轮只调度各计划、然后执行或核对至多一个任务。单个计划的调度异常不中断其他计划。
/// 结果未知只走只读核对，不调用执行器。
/// </summary>
public sealed class BackupPlanWorkerRound(
    IBackupPlanSchedulingStore scheduling,
    IBackupPlanWorkerStore queue,
    IBackupPlanTaskRunner runner,
    IBackupPlanTaskReconciler reconciler,
    TimeProvider clock,
    ILogger<BackupPlanWorkerRound>? logger = null)
{
    private static readonly Action<ILogger, Guid, Exception> LogScheduleFailure =
        LoggerMessage.Define<Guid>(LogLevel.Warning, new EventId(5810, "BackupPlanScheduleIsolated"), "计划 {PlanId} 本轮调度失败，已隔离并继续其他计划。");

    private static readonly Action<ILogger, Guid, Exception> LogExecutionFailure =
        LoggerMessage.Define<Guid>(LogLevel.Warning, new EventId(5811, "BackupPlanExecutionIsolated"), "计划任务 {TaskId} 本轮执行失败，已隔离。");

    private static readonly Action<ILogger, Guid, Exception> LogReconciliationFailure =
        LoggerMessage.Define<Guid>(LogLevel.Warning, new EventId(5812, "BackupPlanReconciliationIsolated"), "计划任务 {TaskId} 本轮核对失败，已隔离。");
    public async Task<BackupPlanWorkerRoundResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().ToUniversalTime();
        var ids = await scheduling.ReadPlanIdsAsync(cancellationToken);
        var failures = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await scheduling.ScheduleAsync(id, now, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures++;
                if (logger is not null)
                {
                    LogScheduleFailure(logger, id, exception);
                }
            }
        }

        var selection = await queue.SelectAsync(now, cancellationToken);
        switch (selection.Kind)
        {
            case BackupPlanWorkerSelectionKind.Execute when selection.TaskId is Guid taskId:
                return new(ids.Count, failures, BackupPlanWorkerAction.Executed, taskId, await ExecuteAsync(taskId, cancellationToken));
            case BackupPlanWorkerSelectionKind.Reconcile when selection.TaskId is Guid taskId:
                return new(ids.Count, failures, BackupPlanWorkerAction.Reconciled, taskId, await ReconcileAsync(taskId, cancellationToken));
            case BackupPlanWorkerSelectionKind.Wait:
                return new(ids.Count, failures, BackupPlanWorkerAction.Wait, null, null);
            default:
                return new(ids.Count, failures, BackupPlanWorkerAction.Idle, null, null);
        }
    }

    private async Task<BackupExecutionContractCode?> ExecuteAsync(Guid taskId, CancellationToken cancellationToken)
    {
        try
        {
            return await runner.RunTaskOnceAsync(new RunBackupPlanTaskRequest(taskId, Guid.NewGuid()), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (logger is not null)
            {
                LogExecutionFailure(logger, taskId, exception);
            }

            return null;
        }
    }

    private async Task<BackupExecutionContractCode?> ReconcileAsync(Guid taskId, CancellationToken cancellationToken)
    {
        try
        {
            return await reconciler.ReconcilePlanTaskOnceAsync(new ReconcileBackupPlanTaskRequest(taskId, Guid.NewGuid()), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (logger is not null)
            {
                LogReconciliationFailure(logger, taskId, exception);
            }

            return null;
        }
    }
}
