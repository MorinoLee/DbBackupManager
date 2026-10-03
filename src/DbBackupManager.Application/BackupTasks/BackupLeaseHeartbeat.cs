namespace DbBackupManager.Application.BackupTasks;

internal sealed class BackupLeaseHeartbeat(
    IBackupTaskExecutionStore store,
    TimeProvider clock,
    TimeSpan interval,
    TimeSpan leaseDuration)
{
    public async Task<BackupLeaseOperation<T>> RunAsync<T>(
        BackupExecutionWorkItem initialWork,
        Func<CancellationToken, Task<T>> operationFactory,
        CancellationToken stoppingToken,
        Func<BackupExecutionWorkItem, bool>? shouldCancelOperation = null)
        where T : class
    {
        var work = initialWork;
        using var ioCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var tickCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var operation = operationFactory(ioCancellation.Token);
        try
        {
            while (!operation.IsCompleted)
            {
                var tick = Task.Delay(interval, clock, tickCancellation.Token);
                if (await Task.WhenAny(operation, tick) == operation)
                {
                    break;
                }

                stoppingToken.ThrowIfCancellationRequested();
                var current = await store.RefreshLeaseWorkItemAsync(
                    work.Lease.TaskId,
                    work.Lease.LeaseToken,
                    work.Lease.Purpose,
                    clock.GetUtcNow(),
                    stoppingToken);
                if (!current.IsSucceeded || current.Value is null)
                {
                    ioCancellation.Cancel();
                    Observe(operation);
                    return new(false, work, null);
                }

                work = current.Value;
                if (shouldCancelOperation?.Invoke(work) == true)
                {
                    ioCancellation.Cancel();
                }

                var heartbeatAt = clock.GetUtcNow();
                var renewed = await store.RenewLeaseAsync(
                    work.Lease,
                    heartbeatAt,
                    heartbeatAt + leaseDuration,
                    stoppingToken);
                // RowVersion 竞争需要下一轮重读，不能解释为租约丢失或再次调用外部操作。
                if (renewed.Code == BackupTaskStoreResultCode.ConcurrencyConflict)
                {
                    continue;
                }

                if (!renewed.IsSucceeded || renewed.Value is null)
                {
                    ioCancellation.Cancel();
                    Observe(operation);
                    return new(false, work, null);
                }

                work = work with { Lease = renewed.Value };
            }

            return new(true, work, await operation.WaitAsync(stoppingToken));
        }
        catch
        {
            ioCancellation.Cancel();
            Observe(operation);
            // 宿主停止、存储异常或租约丢失不代表外部操作没有发生。
            throw;
        }
        finally
        {
            // 外部操作提前结束时释放尚未到期的计时器。
            tickCancellation.Cancel();
        }
    }

    private static void Observe(Task operation)
    {
        _ = operation.ContinueWith(
            task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

internal sealed record BackupLeaseOperation<T>(
    bool LeaseRetained,
    BackupExecutionWorkItem Work,
    T? Result)
    where T : class;
