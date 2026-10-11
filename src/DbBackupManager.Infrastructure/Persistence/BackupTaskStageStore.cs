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
                var snapshot = await context.BackupTaskSnapshots.SingleOrDefaultAsync(x => x.TaskId == lease.TaskId, cancellationToken);
                if (snapshot is null) return new BackupTaskStoreResult<LeaseHandle>(BackupTaskStoreResultCode.NotFound);
                if (snapshot.Purpose is not null || snapshot.FileNameRuleVersion == "v3")
                    return new BackupTaskStoreResult<LeaseHandle>(BackupTaskStoreResultCode.StateMismatch);
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                await BackupInvocationAuthorizationStore.LockDatabaseAsync(context, snapshot.DatabaseId, cancellationToken);
                var saved = await context.BackupInvocationAuthorizations.SingleOrDefaultAsync(x => x.TaskId == lease.TaskId && x.AttemptId == lease.BackupAttemptId, cancellationToken);
                var command = new AuthorizeBackupInvocation(lease.BackupAttemptId, lease.BackupAttemptId,
                    lease.BackupAttemptId, snapshot.DatabaseId, new() { CallerIncarnationId = lease.LeaseToken }, startedAtUtc, attemptRowVersion);
                if (saved is not null) return new BackupTaskStoreResult<LeaseHandle>(
                    BackupInvocationAuthorizationStore.Matches(BackupInvocationAuthorizationStore.Model(saved), lease, command)
                        ? BackupTaskStoreResultCode.AlreadyApplied : BackupTaskStoreResultCode.ConcurrencyConflict);
                var result = await BackupInvocationAuthorizationStore.AuthorizeCoreAsync(context, lease,
                    command, DateTimeOffset.UtcNow, cancellationToken);
                if (result.Code == BackupExecutionContractCode.Succeeded)
                {
                    await context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new BackupTaskStoreResult<LeaseHandle>(BackupTaskStoreResultCode.Succeeded, result.Value!.Lease);
                }
                return new BackupTaskStoreResult<LeaseHandle>(result.Code switch
                {
                    BackupExecutionContractCode.DatabaseBlocked => BackupTaskStoreResultCode.DatabaseBlocked,
                    BackupExecutionContractCode.LeaseLost => BackupTaskStoreResultCode.LeaseLost,
                    BackupExecutionContractCode.NotFound => BackupTaskStoreResultCode.NotFound,
                    _ => BackupTaskStoreResultCode.ConcurrencyConflict
                });
            }, cancellationToken);
        }
        catch (DbUpdateException)
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
                var bindingSnapshot = await context.BackupTaskSnapshots.SingleOrDefaultAsync(x => x.TaskId == lease.TaskId, cancellationToken);
                if (bindingSnapshot is null) return new BackupTaskStoreResult<BackupTaskTransitionModel>(BackupTaskStoreResultCode.NotFound);
                if (bindingSnapshot.Purpose is not null || bindingSnapshot.FileNameRuleVersion == "v3")
                    return new BackupTaskStoreResult<BackupTaskTransitionModel>(BackupTaskStoreResultCode.StateMismatch);
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                await BackupInvocationAuthorizationStore.LockDatabaseAsync(context, bindingSnapshot.DatabaseId, cancellationToken);
                _ = await context.BackupTasks.FromSqlInterpolated(
                    $"SELECT * FROM [BackupTasks] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {lease.TaskId}").AsTracking().SingleOrDefaultAsync(cancellationToken);
                if (lease.Stage == BackupTaskStage.Backup)
                {
                    var savedOperation = await context.BackupPlanExecutionOperations.SingleOrDefaultAsync(x => x.Id == lease.BackupAttemptId, cancellationToken);
                    if (savedOperation is { State: BackupExecutionOperationState.Applied or BackupExecutionOperationState.Frozen }
                        && savedOperation.Facts != LegacySqlFacts(command))
                        return new BackupTaskStoreResult<BackupTaskTransitionModel>(BackupTaskStoreResultCode.StateMismatch);
                }
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
                    if (lease.Stage == BackupTaskStage.Backup)
                    {
                        var authorization = await context.BackupInvocationAuthorizations.AsTracking().SingleOrDefaultAsync(
                            x => x.TaskId == task.Id && x.AttemptId == attempt.Id, cancellationToken);
                        if (authorization is not null)
                        {
                            var operation = await context.BackupPlanExecutionOperations.AsTracking().SingleAsync(x => x.Id == authorization.SqlOperationId, cancellationToken);
                            var facts = LegacySqlFacts(command);
                            operation.Freeze(facts);
                            // 在同一事务中先落实冻结边界，再应用阶段；任一步失败均回滚。
                            await context.SaveChangesAsync(cancellationToken);
                            if (facts.OriginalCallTerminated)
                                authorization.RecordTermination(command.MutationId, operation.Id,
                                    command.Outcome == BackupStageOutcome.Succeeded ? BackupInvocationTerminationKind.PlatformCompleted : BackupInvocationTerminationKind.PlatformConfirmedFailed,
                                    command.EvidenceAtUtc ?? command.OccurredAtUtc, true, true);
                            operation.MarkApplied();
                        }
                    }
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
                await transaction.CommitAsync(cancellationToken);
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
            await using var replayContext = await persistence.CreateDbContextAsync(cancellationToken);
            if (lease.Stage == BackupTaskStage.Backup)
            {
                var saved = await replayContext.BackupPlanExecutionOperations.SingleOrDefaultAsync(x => x.Id == lease.BackupAttemptId, cancellationToken);
                if (saved is { State: BackupExecutionOperationState.Frozen or BackupExecutionOperationState.Applied }
                    && saved.Facts != LegacySqlFacts(command))
                    return new(BackupTaskStoreResultCode.StateMismatch);
            }
            return await persistence.ResolveTransitionReplayAsync(
                lease.TaskId,
                command.MutationId,
                reason,
                lease.LeaseToken,
                cancellationToken);
        }
    }

    private static BackupExecutionFacts LegacySqlFacts(BackupStageCommitCommand command)
    {
        var terminal = command.Outcome is BackupStageOutcome.Succeeded or BackupStageOutcome.ConfirmedFailed
            || command.Outcome == BackupStageOutcome.Cancelled && command.SqlOutcomeSource == BackupSqlOutcomeSource.NotInvoked;
        return new()
        {
            Outcome = command.Outcome switch
            {
                BackupStageOutcome.Succeeded => BackupExecutionOutcome.Succeeded,
                BackupStageOutcome.ConfirmedFailed => BackupExecutionOutcome.ConfirmedFailed,
                BackupStageOutcome.Indeterminate => BackupExecutionOutcome.Indeterminate,
                _ => BackupExecutionOutcome.Cancelled
            },
            SqlOutcomeSource = command.SqlOutcomeSource ?? (terminal ? BackupSqlOutcomeSource.PlatformResponse : BackupSqlOutcomeSource.Unknown),
            UsedCopyOnly = command.UsedCopyOnly,
            UsedChecksum = command.UsedChecksum,
            UsedCompression = command.UsedCompression,
            SqlSuccessObserved = command.Outcome == BackupStageOutcome.Succeeded,
            PlatformCompletedAtUtc = command.Outcome == BackupStageOutcome.Succeeded ? command.EvidenceAtUtc ?? command.OccurredAtUtc : null,
            EvidenceAtUtc = command.EvidenceAtUtc ?? command.OccurredAtUtc,
            OriginalCallTerminated = terminal,
            OriginalCallerCannotInvoke = terminal,
            ReasonCode = command.ErrorCode
        };
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
