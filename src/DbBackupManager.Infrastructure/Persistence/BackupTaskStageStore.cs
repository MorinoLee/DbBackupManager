using System.Data;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using Microsoft.EntityFrameworkCore;

using static DbBackupManager.Infrastructure.Persistence.BackupTaskPersistence;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupTaskStageStore(BackupTaskPersistence persistence)
{
    public async Task<BackupTaskStoreResult<LeaseHandle>> MarkBackupInvocationStartedAsync(
        LeaseHandle lease,
        byte[] attemptRowVersion,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(attemptRowVersion);

        try
        {
            return await persistence.ExecuteWithStrategyAsync(async context =>
            {
                var task = await context.BackupTasks.AsTracking().SingleOrDefaultAsync(
                    item => item.Id == lease.TaskId,
                    cancellationToken);
                if (task is null)
                {
                    return new BackupTaskStoreResult<LeaseHandle>(BackupTaskStoreResultCode.NotFound);
                }

                var leaseError = ValidateLease(task, lease, startedAtUtc);
                if (leaseError is not null)
                {
                    return new BackupTaskStoreResult<LeaseHandle>(leaseError.Value);
                }

                if (task.CurrentStage != BackupTaskStage.Backup)
                {
                    return new BackupTaskStoreResult<LeaseHandle>(
                        BackupTaskStoreResultCode.StateMismatch);
                }

                var attempt = await context.BackupAttempts.AsTracking().SingleAsync(
                    item => item.Id == lease.BackupAttemptId && item.TaskId == lease.TaskId,
                    cancellationToken);
                if (attempt.BackupInvocationStatus == BackupInvocationStatus.Running)
                {
                    return new BackupTaskStoreResult<LeaseHandle>(
                        BackupTaskStoreResultCode.AlreadyApplied,
                        task.ToLeaseHandle());
                }

                if (!attempt.RowVersion.SequenceEqual(attemptRowVersion)
                    || attempt.BackupInvocationStatus != BackupInvocationStatus.Prepared)
                {
                    return new BackupTaskStoreResult<LeaseHandle>(
                        BackupTaskStoreResultCode.ConcurrencyConflict);
                }

                attempt.MarkBackupRunning(startedAtUtc);
                await context.SaveChangesAsync(cancellationToken);
                return new BackupTaskStoreResult<LeaseHandle>(
                    BackupTaskStoreResultCode.Succeeded,
                    task.ToLeaseHandle());
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new BackupTaskStoreResult<LeaseHandle>(
                BackupTaskStoreResultCode.ConcurrencyConflict);
        }
    }

    public async Task<BackupTaskStoreResult<BackupTaskTransitionModel>> CommitStageAsync(
        LeaseHandle lease,
        BackupStageCommitCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var reason = GetStageReason(command.Outcome);

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
                    return MutationTransitionResult(replay, lease.LeaseToken);
                }

                var task = await context.BackupTasks.AsTracking().SingleOrDefaultAsync(
                    item => item.Id == lease.TaskId,
                    cancellationToken);
                if (task is null)
                {
                    return new BackupTaskStoreResult<BackupTaskTransitionModel>(
                        BackupTaskStoreResultCode.NotFound);
                }

                var leaseError = ValidateLease(task, lease, command.OccurredAtUtc);
                if (leaseError is not null)
                {
                    return new BackupTaskStoreResult<BackupTaskTransitionModel>(leaseError.Value);
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
                    ApplyStageOutcome(task, snapshot, attempt, lease, command);
                    RegisterSuccessfulStageFile(
                        context,
                        task,
                        snapshot,
                        attempt,
                        lease.Stage,
                        command.Outcome == BackupStageOutcome.Succeeded,
                        command.MutationId,
                        command.OccurredAtUtc);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    return new BackupTaskStoreResult<BackupTaskTransitionModel>(
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
                    CreateAudit(null, task.Id, "backup.task.stage.commit", "succeeded", reason));
                NotificationOutboxWriter.EnqueueTaskStage(
                    context,
                    command.MutationId,
                    task.Id,
                    lease.Stage,
                    command.Outcome,
                    command.OccurredAtUtc,
                    command.ErrorCode);
                await context.SaveChangesAsync(cancellationToken);
                var nextLease = task.LeaseToken == lease.LeaseToken ? task.ToLeaseHandle() : null;
                return new BackupTaskStoreResult<BackupTaskTransitionModel>(
                    BackupTaskStoreResultCode.Succeeded,
                    new BackupTaskTransitionModel(task.ToStateModel(), nextLease));
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new BackupTaskStoreResult<BackupTaskTransitionModel>(
                BackupTaskStoreResultCode.ConcurrencyConflict);
        }
        catch (DbUpdateException)
        {
            return await persistence.ResolveTransitionReplayAsync(
                lease.TaskId,
                command.MutationId,
                reason,
                lease.LeaseToken,
                cancellationToken);
        }
    }

    private static void ApplyStageOutcome(
        BackupTask task,
        BackupTaskSnapshot snapshot,
        BackupAttempt attempt,
        LeaseHandle lease,
        BackupStageCommitCommand command)
    {
        var evidenceAt = command.EvidenceAtUtc ?? command.OccurredAtUtc;
        switch (command.Outcome)
        {
            case BackupStageOutcome.Succeeded:
                ApplySuccessfulEvidence(lease.Stage, attempt, evidenceAt, command.SourceLengthBytes);
                task.CompleteRunningStage(snapshot.StorageMode, lease.LeaseToken, command.OccurredAtUtc);
                break;

            case BackupStageOutcome.ConfirmedFailed:
                if (lease.Stage == BackupTaskStage.Backup)
                {
                    if (attempt.BackupInvocationStatus != BackupInvocationStatus.Prepared)
                    {
                        attempt.RecordBackupConfirmedFailed(evidenceAt, command.ErrorCode!);
                    }
                }

                task.RecordConfirmedFailure(
                    snapshot.StorageMode,
                    lease.LeaseToken,
                    command.OccurredAtUtc,
                    command.ErrorCode!,
                    command.ErrorMessage!);
                break;

            case BackupStageOutcome.Indeterminate:
                if (lease.Stage == BackupTaskStage.Backup)
                {
                    attempt.RecordBackupIndeterminate(command.EvidenceAtUtc, command.ErrorCode!);
                }

                task.RecordIndeterminateResult(
                    snapshot.StorageMode,
                    lease.LeaseToken,
                    command.OccurredAtUtc,
                    command.ErrorCode!,
                    command.ErrorMessage!);
                break;

            case BackupStageOutcome.Cancelled:
                if (lease.Stage == BackupTaskStage.Backup
                    && attempt.BackupInvocationStatus != BackupInvocationStatus.Prepared)
                {
                    throw new InvalidOperationException("Backup 调用开始后不能按安全边界直接取消。");
                }

                task.CancelRunningAtSafeBoundary(
                    snapshot.StorageMode,
                    lease.LeaseToken,
                    command.OccurredAtUtc);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private static string GetStageReason(BackupStageOutcome outcome)
    {
        return outcome switch
        {
            BackupStageOutcome.Succeeded => "stage.succeeded",
            BackupStageOutcome.ConfirmedFailed => "stage.confirmed_failed",
            BackupStageOutcome.Indeterminate => "stage.indeterminate",
            BackupStageOutcome.Cancelled => "task.cancelled_safe_boundary",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
    }
}
