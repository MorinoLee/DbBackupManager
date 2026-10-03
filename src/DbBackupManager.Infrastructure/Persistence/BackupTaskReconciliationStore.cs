using System.Data;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using Microsoft.EntityFrameworkCore;

using static DbBackupManager.Infrastructure.Persistence.BackupTaskPersistence;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupTaskReconciliationStore(BackupTaskPersistence persistence)
{
    private const string AdminConfirmedFailedReason = "admin.confirmed_failed";

    private const string AdminConfirmedCancelledReason = "admin.confirmed_cancelled";

    public async Task<IReadOnlyList<Guid>> FindReconciliationCandidateTaskIdsAsync(
        DateTimeOffset utcNow,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using var context = await persistence.CreateDbContextAsync(cancellationToken);
        return await context.BackupTasks
            .Where(task => task.Status == BackupTaskStatus.NeedsAttention
                && task.NextReconciliationAtUtc <= utcNow
                && (task.LeaseToken == null
                    || (task.LeasePurpose == BackupLeasePurpose.Reconciliation
                        && task.LeaseExpiresAtUtc <= utcNow)))
            .OrderBy(task => task.NextReconciliationAtUtc)
            .ThenBy(task => task.LeaseExpiresAtUtc ?? task.UpdatedAtUtc)
            .ThenBy(task => task.Id)
            .Select(task => task.Id)
            .Take(maximumCount)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<BackupTaskStoreResult<BackupExecutionWorkItem>> AcquireReconciliationLeaseAsync(
        AcquireReconciliationLeaseCommand command,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await persistence.ExecuteWithStrategyAsync(async context =>
            {
                var task = await context.BackupTasks.AsTracking().SingleOrDefaultAsync(
                    item => item.Id == command.TaskId,
                    cancellationToken);
                if (task is null)
                {
                    return new BackupTaskStoreResult<BackupExecutionWorkItem>(
                        BackupTaskStoreResultCode.NotFound);
                }

                if (task.LeasePurpose == BackupLeasePurpose.Reconciliation
                    && task.LeaseToken == command.LeaseToken)
                {
                    var existing = await LoadWorkItemAsync(context, task, cancellationToken);
                    return existing is null
                        ? new BackupTaskStoreResult<BackupExecutionWorkItem>(
                            BackupTaskStoreResultCode.StateMismatch)
                        : new BackupTaskStoreResult<BackupExecutionWorkItem>(
                            BackupTaskStoreResultCode.AlreadyApplied,
                            existing);
                }

                if (task.Status != BackupTaskStatus.NeedsAttention
                    || task.NextReconciliationAtUtc is null
                    || task.NextReconciliationAtUtc > command.AcquiredAtUtc
                    || (task.LeaseToken is not null
                        && (task.LeasePurpose != BackupLeasePurpose.Reconciliation
                            || task.LeaseExpiresAtUtc > command.AcquiredAtUtc)))
                {
                    return new BackupTaskStoreResult<BackupExecutionWorkItem>(
                        BackupTaskStoreResultCode.StateMismatch);
                }

                task.AcquireReconciliationLease(
                    command.LeaseToken,
                    command.LeaseOwner,
                    command.AcquiredAtUtc,
                    command.ExpiresAtUtc);
                await context.SaveChangesAsync(cancellationToken);
                var workItem = await LoadWorkItemAsync(context, task, cancellationToken);
                return workItem is null
                    ? new BackupTaskStoreResult<BackupExecutionWorkItem>(
                        BackupTaskStoreResultCode.StateMismatch)
                    : new BackupTaskStoreResult<BackupExecutionWorkItem>(
                        BackupTaskStoreResultCode.Succeeded,
                        workItem);
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new BackupTaskStoreResult<BackupExecutionWorkItem>(
                BackupTaskStoreResultCode.ConcurrencyConflict);
        }
        catch (DbUpdateException)
        {
            return new BackupTaskStoreResult<BackupExecutionWorkItem>(
                BackupTaskStoreResultCode.ConcurrencyConflict);
        }
    }

    public async Task<BackupTaskStoreResult<BackupTaskStateModel>> CommitReconciliationAsync(
        LeaseHandle lease,
        ReconciliationCommitCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var reason = GetReconciliationReason(command.Outcome);

        try
        {
            return await persistence.ExecuteWithStrategyAsync(async context =>
            {
                var replay = await ReadMutationAsync(
                    context,
                    command.MutationId,
                    lease.TaskId,
                    reason,
                    cancellationToken);
                if (replay.Found)
                {
                    return MutationStateResult(replay);
                }

                var task = await context.BackupTasks.AsTracking().SingleOrDefaultAsync(
                    item => item.Id == lease.TaskId,
                    cancellationToken);
                if (task is null)
                {
                    return new BackupTaskStoreResult<BackupTaskStateModel>(
                        BackupTaskStoreResultCode.NotFound);
                }

                var leaseError = ValidateLease(task, lease, command.OccurredAtUtc);
                if (leaseError is not null)
                {
                    return new BackupTaskStoreResult<BackupTaskStateModel>(leaseError.Value);
                }

                var snapshot = await context.BackupTaskSnapshots.SingleAsync(
                    item => item.TaskId == task.Id,
                    cancellationToken);
                var attempt = await context.BackupAttempts.AsTracking().SingleAsync(
                    item => item.Id == lease.BackupAttemptId && item.TaskId == lease.TaskId,
                    cancellationToken);
                var fromStatus = task.Status;
                var fromStage = task.CurrentStage;
                try
                {
                    ApplyReconciliationOutcome(task, snapshot, attempt, lease, command);
                    RegisterSuccessfulStageFile(
                        context,
                        task,
                        snapshot,
                        attempt,
                        lease.Stage,
                        command.Outcome == BackupReconciliationOutcome.Succeeded,
                        command.MutationId,
                        command.OccurredAtUtc);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    return new BackupTaskStoreResult<BackupTaskStateModel>(
                        BackupTaskStoreResultCode.StateMismatch);
                }

                context.AddRange(
                    CreateStateChange(
                        command.MutationId,
                        task,
                        fromStatus,
                        fromStage,
                        reason,
                        command.ErrorMessage,
                        command.OccurredAtUtc),
                    CreateAudit(null, task.Id, "backup.task.reconcile", "succeeded", reason));
                NotificationOutboxWriter.EnqueueReconciliation(
                    context,
                    command.MutationId,
                    task.Id,
                    lease.Stage,
                    command.Outcome,
                    command.OccurredAtUtc,
                    command.ErrorCode);
                await context.SaveChangesAsync(cancellationToken);
                return new BackupTaskStoreResult<BackupTaskStateModel>(
                    BackupTaskStoreResultCode.Succeeded,
                    task.ToStateModel());
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new BackupTaskStoreResult<BackupTaskStateModel>(
                BackupTaskStoreResultCode.ConcurrencyConflict);
        }
        catch (DbUpdateException)
        {
            return await persistence.ResolveStateReplayAsync(
                lease.TaskId,
                command.MutationId,
                reason,
                cancellationToken);
        }
    }

    public async Task<BackupTaskStoreResult<BackupTaskStateModel>> ConfirmNeedsAttentionAsync(
        ConfirmNeedsAttentionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Outcome is not (BackupReconciliationOutcome.Failed
            or BackupReconciliationOutcome.Cancelled))
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "管理员确认只允许失败或可安全取消。");
        }

        var reason = command.Outcome == BackupReconciliationOutcome.Failed
            ? AdminConfirmedFailedReason
            : AdminConfirmedCancelledReason;
        var auditAction = command.Outcome == BackupReconciliationOutcome.Failed
            ? "backup.task.reconciliation.confirm_failed"
            : "backup.task.reconciliation.confirm_cancelled";
        try
        {
            return await persistence.ExecuteWithStrategyAsync(async context =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
                if (!await IsActorValidAsync(
                    context,
                    command.ActorAdminUserId,
                    command.ActorSecurityStamp,
                    cancellationToken))
                {
                    return new BackupTaskStoreResult<BackupTaskStateModel>(
                        BackupTaskStoreResultCode.AuthenticationRequired);
                }

                var replay = await ReadMutationAsync(
                    context,
                    command.MutationId,
                    command.TaskId,
                    reason,
                    cancellationToken);
                if (replay.Found)
                {
                    return MutationStateResult(replay);
                }

                var task = await context.BackupTasks.AsTracking().SingleOrDefaultAsync(
                    item => item.Id == command.TaskId,
                    cancellationToken);
                if (task is null)
                {
                    return new BackupTaskStoreResult<BackupTaskStateModel>(
                        BackupTaskStoreResultCode.NotFound);
                }

                var snapshot = await context.BackupTaskSnapshots.SingleAsync(
                    item => item.TaskId == task.Id,
                    cancellationToken);
                var fromStatus = task.Status;
                var fromStage = task.CurrentStage;
                try
                {
                    if (command.Outcome == BackupReconciliationOutcome.Failed)
                    {
                        task.ConfirmFailed(
                            snapshot.StorageMode,
                            command.OccurredAtUtc,
                            command.ErrorCode!,
                            command.ErrorMessage!);
                    }
                    else
                    {
                        task.ConfirmCancelled(snapshot.StorageMode, command.OccurredAtUtc);
                    }

                    if (command.Outcome == BackupReconciliationOutcome.Failed
                        && fromStage == BackupTaskStage.Backup
                        && task.CurrentBackupAttemptId is { } attemptId)
                    {
                        var attempt = await context.BackupAttempts.AsTracking().SingleAsync(
                            item => item.Id == attemptId && item.TaskId == task.Id,
                            cancellationToken);
                        if (attempt.BackupInvocationStatus is BackupInvocationStatus.Running
                            or BackupInvocationStatus.Indeterminate)
                        {
                            attempt.ReconcileBackupConfirmedFailed(
                                command.OccurredAtUtc,
                                command.ErrorCode!);
                        }
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    return new BackupTaskStoreResult<BackupTaskStateModel>(
                        BackupTaskStoreResultCode.StateMismatch);
                }

                context.AddRange(
                    CreateStateChange(
                        command.MutationId,
                        task,
                        fromStatus,
                        fromStage,
                        reason,
                        command.ErrorMessage,
                        command.OccurredAtUtc),
                    CreateAudit(
                        command.ActorAdminUserId,
                        task.Id,
                        auditAction,
                        "succeeded",
                        reason));
                if (command.Outcome == BackupReconciliationOutcome.Failed)
                {
                    NotificationOutboxWriter.EnqueueReconciliation(
                        context,
                        command.MutationId,
                        task.Id,
                        task.CurrentStage!.Value,
                        BackupReconciliationOutcome.Failed,
                        command.OccurredAtUtc,
                        command.ErrorCode);
                }

                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new BackupTaskStoreResult<BackupTaskStateModel>(
                    BackupTaskStoreResultCode.Succeeded,
                    task.ToStateModel());
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new BackupTaskStoreResult<BackupTaskStateModel>(
                BackupTaskStoreResultCode.ConcurrencyConflict);
        }
        catch (DbUpdateException)
        {
            return await persistence.ResolveStateReplayAsync(
                command.TaskId,
                command.MutationId,
                reason,
                cancellationToken);
        }
    }

    private static void ApplyReconciliationOutcome(
        BackupTask task,
        BackupTaskSnapshot snapshot,
        BackupAttempt attempt,
        LeaseHandle lease,
        ReconciliationCommitCommand command)
    {
        if (command.SourceLengthBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "核对文件长度必须为正数。");
        }

        var evidenceAt = command.EvidenceAtUtc ?? command.OccurredAtUtc;
        if (command.Outcome == BackupReconciliationOutcome.Succeeded)
        {
            if (lease.Stage == BackupTaskStage.Backup)
            {
                attempt.ReconcileBackupSucceeded(evidenceAt);
            }
            else
            {
                ApplySuccessfulEvidence(lease.Stage, attempt, evidenceAt, command.SourceLengthBytes);
            }
        }
        else if (command.Outcome == BackupReconciliationOutcome.Failed
            && lease.Stage == BackupTaskStage.Backup)
        {
            attempt.ReconcileBackupConfirmedFailed(evidenceAt, command.ErrorCode!);
        }

        task.CompleteReconciliation(
            snapshot.StorageMode,
            lease.LeaseToken,
            command.OccurredAtUtc,
            command.Outcome,
            command.ErrorCode,
            command.ErrorMessage);
    }

    private static string GetReconciliationReason(BackupReconciliationOutcome outcome)
    {
        return outcome switch
        {
            BackupReconciliationOutcome.Succeeded => "reconciliation.succeeded",
            BackupReconciliationOutcome.SafeToRetry => "reconciliation.safe_to_retry",
            BackupReconciliationOutcome.Failed => "reconciliation.failed",
            BackupReconciliationOutcome.Cancelled => "reconciliation.cancelled",
            BackupReconciliationOutcome.Inconclusive => "reconciliation.inconclusive",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
    }
}
