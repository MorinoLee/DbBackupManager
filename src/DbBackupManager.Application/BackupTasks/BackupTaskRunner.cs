using DbBackupManager.Application.FileStorage;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupTasks;
using static DbBackupManager.Application.BackupTasks.BackupRemoteStageExecutor;
using static DbBackupManager.Application.BackupTasks.BackupStageResult;

namespace DbBackupManager.Application.BackupTasks;

public sealed record BackupFileProbeResult(bool Succeeded, bool Exists, long? Length, string? ErrorCode = null);
public interface IBackupSourceProbe
{
    Task<BackupFileProbeResult> InspectAsync(BackupTaskSnapshotModel snapshot, BackupAttemptModel attempt,
        CancellationToken cancellationToken);
}
public interface IBackupExecutionGuard
{
    Task<bool> IsEnabledAsync(BackupTaskSnapshotModel snapshot, CancellationToken cancellationToken);
}
public sealed record BackupRunnerOptions(TimeSpan HeartbeatInterval, TimeSpan LeaseDuration)
{
    public static BackupRunnerOptions Default { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
}

public sealed class BackupTaskRunner(
    IBackupTaskExecutionStore store,
    ITargetSqlBackupExecutor backup,
    ITargetSqlReadOnlyProbe sql,
    IBackupSourceProbe files,
    IBackupFileStorageProbe remoteProbe,
    IBackupDirectoryPreparer directoryPreparation,
    IBackupFileTransferExecutor transfer,
    IBackupFileDeletionExecutor deletion,
    IBackupExecutionGuard guard,
    TimeProvider clock,
    BackupRunnerOptions options)
{
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private readonly BackupLeaseHeartbeat _heartbeat = new(store, clock, options.HeartbeatInterval, options.LeaseDuration);
    private readonly BackupRemoteStageExecutor _remoteStages = new(
        files, remoteProbe, directoryPreparation, transfer, deletion, guard);

    public Task<bool> RunOnceAsync(CancellationToken stoppingToken) =>
        RunOnceAsync(null, null, stoppingToken);

    public Task<bool> RunTaskOnceAsync(Guid taskId, CancellationToken stoppingToken = default)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("任务标识不能为空。", nameof(taskId));
        return RunOnceAsync(taskId, null, stoppingToken);
    }

    public Task<bool> RunTaskOnceAsync(
        Guid taskId,
        Guid backupAttemptId,
        CancellationToken stoppingToken = default)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("任务标识不能为空。", nameof(taskId));
        if (backupAttemptId == Guid.Empty)
            throw new ArgumentException("备份尝试标识不能为空。", nameof(backupAttemptId));
        return RunOnceAsync(taskId, backupAttemptId, stoppingToken);
    }

    private async Task<bool> RunOnceAsync(
        Guid? requiredTaskId,
        Guid? requiredBackupAttemptId,
        CancellationToken stoppingToken)
    {
        try
        {
            return await RunCoreAsync(requiredTaskId, requiredBackupAttemptId, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (stoppingToken.IsCancellationRequested)
        {
            // 宿主停止或调用方取消时数据库访问可能抛出被包装的 SqlException；统一映射为取消，不误报为存储故障。
            throw new OperationCanceledException("宿主停止，任务执行已取消。", exception, stoppingToken);
        }
    }

    private async Task<bool> RunCoreAsync(
        Guid? requiredTaskId,
        Guid? requiredBackupAttemptId,
        CancellationToken stoppingToken)
    {
        if (options.HeartbeatInterval <= TimeSpan.Zero || options.LeaseDuration < options.HeartbeatInterval * 3)
            throw new InvalidOperationException("Worker 租约必须至少覆盖三个心跳间隔。");
        var now = clock.GetUtcNow();
        if (requiredTaskId is { } taskId)
        {
            await store.ExpireExecutionLeaseAsync(
                new(taskId, Guid.NewGuid(), now, "worker_lease_expired", "执行租约已过期，需要核对外部结果。"),
                stoppingToken);
        }
        else
        {
            foreach (var id in await store.FindExpiredExecutionTaskIdsAsync(now, 100, stoppingToken))
                await store.ExpireExecutionLeaseAsync(new(id, Guid.NewGuid(), now, "worker_lease_expired", "执行租约已过期，需要核对外部结果。"), stoppingToken);
        }

        var claimCommand = new ClaimNextBackupTaskCommand(
            Guid.NewGuid(),
            requiredBackupAttemptId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            _owner,
            now,
            now + options.LeaseDuration);
        var claim = requiredTaskId is { } exactTaskId
            ? await store.ClaimTaskAsync(exactTaskId, claimCommand, stoppingToken)
            : await store.ClaimNextAsync(claimCommand, stoppingToken);
        if (!claim.IsSucceeded || claim.Value is null)
            return claim.Code == BackupTaskStoreResultCode.ConfigurationUnavailable;
        var work = claim.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            var refreshed = await RefreshAsync(work, stoppingToken);
            if (!refreshed.IsSucceeded || refreshed.Value is null) return true;
            work = refreshed.Value;
            if (work.Snapshot.Purpose is not null || work.Snapshot.FileNameRuleVersion == "v3") return true;
            if (work.Task.CancellationRequestedAtUtc is not null
                && !MustCompleteLocalVerification(work))
            {
                await store.CommitStageAsync(work.Lease, new(Guid.NewGuid(), BackupStageOutcome.Cancelled, clock.GetUtcNow()), stoppingToken);
                return true;
            }
            if (work.Lease.Stage == BackupTaskStage.Backup)
            {
                var preflight = await RunWithHeartbeatAsync(
                    work,
                    token => PrepareBackupAsync(work, token),
                    stoppingToken);
                if (!preflight.LeaseRetained || preflight.Result is null)
                {
                    return true;
                }

                work = preflight.Work;
                if (preflight.Result.Outcome != BackupStageOutcome.Succeeded)
                {
                    var preflightCommit = await CommitResultAsync(
                        work,
                        preflight.Result,
                        stoppingToken);
                    if (preflightCommit is null)
                    {
                        return true;
                    }

                    work = preflightCommit;
                    continue;
                }

                // 只有本轮成功的新标记可以触发外部 BACKUP；重放标记不授予再次调用权。
                var marked = await store.MarkBackupInvocationStartedAsync(work.Lease, work.Attempt.RowVersion, clock.GetUtcNow(), stoppingToken);
                if (marked.Code != BackupTaskStoreResultCode.Succeeded || marked.Value is null) return true;
                work = work with { Lease = marked.Value };
            }
            var execution = await RunWithHeartbeatAsync(
                work,
                token => ExecuteStageAsync(work, token),
                stoppingToken);
            if (!execution.LeaseRetained || execution.Result is null)
            {
                return true;
            }

            work = execution.Work;
            var committedWork = await CommitResultAsync(work, execution.Result, stoppingToken);
            if (committedWork is null)
            {
                return true;
            }

            work = committedWork;
        }
        return true;
    }

    private Task<BackupLeaseOperation<BackupStageResult>> RunWithHeartbeatAsync(
        BackupExecutionWorkItem initialWork,
        Func<CancellationToken, Task<BackupStageResult>> operationFactory,
        CancellationToken stoppingToken) =>
        _heartbeat.RunAsync(
            initialWork,
            operationFactory,
            stoppingToken,
            work => work.Task.CancellationRequestedAtUtc is not null && !MustCompleteLocalVerification(work));

    private async Task<BackupExecutionWorkItem?> CommitResultAsync(
        BackupExecutionWorkItem initialWork,
        BackupStageResult result,
        CancellationToken stoppingToken)
    {
        var work = initialWork;
        var evidenceAt = clock.GetUtcNow();
        var mutation = Guid.NewGuid();
        BackupTaskStoreResult<BackupTaskTransitionModel>? committed = null;
        for (var retry = 0; retry < 3; retry++)
        {
            var current = await RefreshAsync(work, stoppingToken);
            if (!current.IsSucceeded || current.Value is null)
            {
                return null;
            }

            work = current.Value;
            committed = await store.CommitStageAsync(
                work.Lease,
                new(
                    mutation,
                    result.Outcome,
                    clock.GetUtcNow(),
                    evidenceAt,
                    result.Length,
                    result.ErrorCode,
                    result.ErrorCode is null
                        ? null
                        : "备份阶段未完成，请根据错误分类检查后处理。",
                    result.SqlOutcomeSource, result.UsedCopyOnly, result.UsedChecksum, result.UsedCompression),
                stoppingToken);
            if (committed.Code != BackupTaskStoreResultCode.ConcurrencyConflict)
            {
                break;
            }
        }

        return committed is { IsSucceeded: true, Value.Lease: not null }
            ? work with
            {
                Lease = committed.Value.Lease,
                Task = committed.Value.Task,
            }
            : null;
    }

    private Task<BackupTaskStoreResult<BackupExecutionWorkItem>> RefreshAsync(BackupExecutionWorkItem work, CancellationToken token) =>
        store.RefreshLeaseWorkItemAsync(work.Lease.TaskId, work.Lease.LeaseToken, BackupLeasePurpose.Execution, clock.GetUtcNow(), token);

    private static bool MustCompleteLocalVerification(BackupExecutionWorkItem work) =>
        work.Lease.Stage == BackupTaskStage.VerifyLocal
        && work.Attempt.InvocationStatus == BackupInvocationStatus.Succeeded;

    private async Task<BackupStageResult> PrepareBackupAsync(
        BackupExecutionWorkItem work,
        CancellationToken token)
    {
        try
        {
            var snapshot = work.Snapshot;
            if (!await guard.IsEnabledAsync(snapshot, token))
            {
                return new(
                    BackupStageOutcome.ConfirmedFailed,
                    "execution_configuration_disabled");
            }

            var prepared = await directoryPreparation.PrepareParentAsync(
                new(
                    Endpoint(snapshot.WorkerSourceEndpoint),
                    work.Attempt.WorkerSourceFilePath,
                    TimeoutSeconds(work)),
                token);
            if (!prepared.IsSucceeded)
            {
                return prepared.Failure!.Code == BackupFileStorageFailureCode.Cancelled
                    ? new(BackupStageOutcome.Cancelled, "execution_cancelled")
                    : MapFile(prepared);
            }

            var existing = await files.InspectAsync(snapshot, work.Attempt, token);
            if (!existing.Succeeded)
            {
                return new(
                    BackupStageOutcome.ConfirmedFailed,
                    existing.ErrorCode ?? "source_unavailable");
            }

            if (existing.Exists)
            {
                return new(
                    BackupStageOutcome.ConfirmedFailed,
                    "backup_file_already_exists");
            }

            token.ThrowIfCancellationRequested();
            return await guard.IsEnabledAsync(snapshot, token)
                ? new(BackupStageOutcome.Succeeded)
                : new(
                    BackupStageOutcome.ConfirmedFailed,
                    "execution_configuration_disabled");
        }
        catch (OperationCanceledException)
        {
            return new(BackupStageOutcome.Cancelled, "execution_cancelled");
        }
        catch (Exception)
        {
            return new(BackupStageOutcome.ConfirmedFailed, "stage_adapter_failed");
        }
    }

    private async Task<BackupStageResult> ExecuteStageAsync(BackupExecutionWorkItem work, CancellationToken token)
    {
        var invoked = false;
        try
        {
            var s = work.Snapshot;
            var target = new TargetSqlConnectionInput(s.SqlTarget.ConnectionAddress, s.SqlTarget.SqlCredentialReferenceId,
                s.SqlTarget.EncryptConnection, s.SqlTarget.TrustServerCertificate, s.SqlTarget.CertificateTrustReason, s.SqlTarget.ConnectionTimeoutSeconds, s.SqlTarget.AllowLegacyTls, s.SqlTarget.LegacyTlsReason);
            if (work.Lease.Stage == BackupTaskStage.Backup)
            {
                token.ThrowIfCancellationRequested();
                invoked = true;
                var result = await backup.ExecuteFullBackupAsync(target, new(s.Identity.DatabaseName, work.Attempt.LocalSqlFilePath,
                    s.Policy.UseCopyOnly, s.Policy.UseChecksum, s.Policy.UseCompression, s.Policy.BackupTimeoutMinutes * 60), token);
                var mapped = Map(result);
                return mapped with
                {
                    SqlOutcomeSource = result.Outcome == TargetSqlOutcome.Indeterminate
                    ? BackupSqlOutcomeSource.Unknown : BackupSqlOutcomeSource.PlatformResponse,
                    UsedCopyOnly = result.Value?.UsedCopyOnly,
                    UsedChecksum = result.Value?.UsedChecksum,
                    UsedCompression = result.Value?.UsedCompression
                };
            }

            if (!await guard.IsEnabledAsync(s, token))
            {
                return new(
                    BackupStageOutcome.ConfirmedFailed,
                    "execution_configuration_disabled");
            }

            if (work.Lease.Stage == BackupTaskStage.VerifyLocal)
            {
                var result = await sql.VerifyBackupAsync(target, new(work.Attempt.LocalSqlFilePath, s.Policy.UseChecksum,
                    s.Policy.VerifyTimeoutMinutes * 60), token);
                if (!result.IsSucceeded) return Map(result);
                var file = await files.InspectAsync(s, work.Attempt, token);
                return file.Succeeded && file.Exists && file.Length > 0
                    ? new(BackupStageOutcome.Succeeded, Length: file.Length)
                    : new(BackupStageOutcome.ConfirmedFailed, file.ErrorCode ?? "verified_file_unavailable");
            }

            if (work.Lease.Stage is BackupTaskStage.Transfer or BackupTaskStage.ValidateCopy or BackupTaskStage.Cleanup)
            {
                return await _remoteStages.ExecuteAsync(work, token);
            }

            return new(BackupStageOutcome.ConfirmedFailed, "stage_not_supported");
        }
        catch (OperationCanceledException)
        {
            return new(invoked ? BackupStageOutcome.Indeterminate : BackupStageOutcome.Cancelled, "execution_cancelled",
            SqlOutcomeSource: invoked ? BackupSqlOutcomeSource.Unknown : BackupSqlOutcomeSource.NotInvoked);
        }
        catch (Exception)
        {
            return new(invoked ? BackupStageOutcome.Indeterminate : BackupStageOutcome.ConfirmedFailed, "stage_adapter_failed",
            SqlOutcomeSource: invoked ? BackupSqlOutcomeSource.Unknown : BackupSqlOutcomeSource.NotInvoked);
        }
    }

    private static BackupStageResult Map<T>(TargetSqlResult<T> result) where T : class => result.IsSucceeded
        ? new(BackupStageOutcome.Succeeded)
        : new(result.Outcome == TargetSqlOutcome.Indeterminate ? BackupStageOutcome.Indeterminate : BackupStageOutcome.ConfirmedFailed,
            result.Failure!.Code.ToString());
}
