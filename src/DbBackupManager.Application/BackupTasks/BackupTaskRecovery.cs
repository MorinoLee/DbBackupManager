using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.BackupTasks;

public sealed record BackupReconciliationOptions(
    TimeSpan HeartbeatInterval,
    TimeSpan LeaseDuration,
    TimeSpan FileStabilityInterval,
    int CandidateBatchSize)
{
    public static BackupReconciliationOptions Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(2),
        100);

    public void Validate()
    {
        if (HeartbeatInterval <= TimeSpan.Zero
            || LeaseDuration < HeartbeatInterval * 3
            || FileStabilityInterval <= TimeSpan.Zero
            || CandidateBatchSize is < 1 or > 1000)
        {
            throw new InvalidOperationException("核对租约、心跳、文件稳定窗口或候选批次配置无效。");
        }
    }
}

public sealed class BackupTaskRecovery(
    IBackupTaskExecutionStore store,
    IBackupReconciliationEvidenceProbe evidenceProbe,
    TimeProvider clock,
    BackupReconciliationOptions options) : IBackupTaskRecovery
{
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private readonly BackupLeaseHeartbeat _heartbeat = new(store, clock, options.HeartbeatInterval, options.LeaseDuration);

    public async Task<bool> ReconcileOnceAsync(CancellationToken cancellationToken = default)
    {
        options.Validate();
        var now = clock.GetUtcNow();
        var candidates = await store.FindReconciliationCandidateTaskIdsAsync(
            now,
            options.CandidateBatchSize,
            cancellationToken);
        foreach (var taskId in candidates)
        {
            var acquired = await store.AcquireReconciliationLeaseAsync(
                new AcquireReconciliationLeaseCommand(
                    taskId,
                    Guid.NewGuid(),
                    _owner,
                    now,
                    now + options.LeaseDuration),
                cancellationToken);
            if (!acquired.IsSucceeded || acquired.Value is null)
            {
                continue;
            }

            await ReconcileAsync(acquired.Value, cancellationToken);
            return true;
        }

        return false;
    }

    public async Task<bool> ReconcileTaskOnceAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        if (taskId == Guid.Empty)
        {
            throw new ArgumentException("任务标识不能为空。", nameof(taskId));
        }

        options.Validate();
        var now = clock.GetUtcNow();
        var acquired = await store.AcquireReconciliationLeaseAsync(
            new AcquireReconciliationLeaseCommand(
                taskId,
                Guid.NewGuid(),
                _owner,
                now,
                now + options.LeaseDuration),
            cancellationToken);
        if (!acquired.IsSucceeded || acquired.Value is null)
        {
            return false;
        }

        await ReconcileAsync(acquired.Value, cancellationToken);
        return true;
    }

    private async Task ReconcileAsync(
        BackupExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        var inspection = await _heartbeat.RunAsync(
            workItem,
            token => InspectSafelyAsync(workItem, token),
            cancellationToken);
        if (!inspection.LeaseRetained || inspection.Result is null)
        {
            return;
        }

        workItem = inspection.Work;
        var evidence = EnsureEvidenceMatches(workItem, inspection.Result);
        await CommitAsync(workItem, evidence, cancellationToken);
    }

    private async Task<BackupReconciliationEvidence> InspectSafelyAsync(
        BackupExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        try
        {
            return await evidenceProbe.InspectAsync(workItem, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Inconclusive(workItem, "reconciliation.probe_cancelled");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Inconclusive(workItem, "reconciliation.probe_failed");
        }
    }

    private async Task CommitAsync(
        BackupExecutionWorkItem workItem,
        BackupReconciliationEvidence evidence,
        CancellationToken cancellationToken)
    {
        var mutationId = Guid.NewGuid();
        for (var retry = 0; retry < 3; retry++)
        {
            var current = await RefreshAsync(workItem, cancellationToken);
            if (!current.IsSucceeded || current.Value is null)
            {
                return;
            }

            workItem = current.Value;
            var outcome = evidence.Conclusion switch
            {
                BackupReconciliationConclusion.Succeeded => BackupReconciliationOutcome.Succeeded,
                BackupReconciliationConclusion.ConfirmedFailed => BackupReconciliationOutcome.Failed,
                BackupReconciliationConclusion.Inconclusive => BackupReconciliationOutcome.Inconclusive,
                _ => throw new ArgumentOutOfRangeException(nameof(evidence)),
            };
            var errorCode = outcome == BackupReconciliationOutcome.Succeeded
                ? null
                : evidence.ReasonCode;
            var errorMessage = outcome switch
            {
                BackupReconciliationOutcome.Failed => "只读证据确认当前备份结果无效。",
                BackupReconciliationOutcome.Inconclusive => "核对证据不足，任务保持待处理。",
                _ => null,
            };
            var committed = await store.CommitReconciliationAsync(
                workItem.Lease,
                new ReconciliationCommitCommand(
                    mutationId,
                    outcome,
                    clock.GetUtcNow(),
                    evidence.ObservedAtUtc,
                    evidence.Stage == BackupTaskStage.VerifyLocal
                        ? evidence.SourceLengthBytes
                        : null,
                    errorCode,
                    errorMessage),
                cancellationToken);
            if (committed.Code != BackupTaskStoreResultCode.ConcurrencyConflict)
            {
                return;
            }
        }
    }

    private Task<BackupTaskStoreResult<BackupExecutionWorkItem>> RefreshAsync(
        BackupExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        return store.RefreshLeaseWorkItemAsync(
            workItem.Lease.TaskId,
            workItem.Lease.LeaseToken,
            BackupLeasePurpose.Reconciliation,
            clock.GetUtcNow(),
            cancellationToken);
    }

    private BackupReconciliationEvidence EnsureEvidenceMatches(
        BackupExecutionWorkItem workItem,
        BackupReconciliationEvidence evidence)
    {
        return evidence.TaskId == workItem.Task.TaskId
            && evidence.BackupAttemptId == workItem.Attempt.Id
            && evidence.Stage == workItem.Lease.Stage
                ? evidence
                : Inconclusive(workItem, "reconciliation.evidence_mismatch");
    }

    private BackupReconciliationEvidence Inconclusive(
        BackupExecutionWorkItem workItem,
        string reasonCode)
    {
        return new BackupReconciliationEvidence(
            workItem.Task.TaskId,
            workItem.Attempt.Id,
            workItem.Lease.Stage,
            clock.GetUtcNow(),
            BackupReconciliationConclusion.Inconclusive,
            BackupArtifactObservation.Unknown,
            TargetBackupObservation.Unknown,
            BackupVerificationObservation.NotAttempted,
            reasonCode);
    }
}
