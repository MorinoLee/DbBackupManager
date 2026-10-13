using System.Data;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using Microsoft.EntityFrameworkCore;

using static DbBackupManager.Infrastructure.Persistence.BackupTaskPersistence;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupTaskCommandStore(BackupTaskPersistence persistence)
{
    internal const string CancellationReason = "task.cancellation_requested";

    private const string RetryReason = "task.retry_requested";

    private const string AdminReconciliationReason = "admin_reconciliation_requested";

    public Task<BackupTaskStoreResult<BackupTaskStateModel>> RequestCancellationAsync(
        BackupTaskMutationCommand command,
        CancellationToken cancellationToken = default)
    {
        return MutateTaskAsync(
            command,
            CancellationReason,
            "backup.task.cancel.request",
            (task, storageMode) =>
            {
                if (task.CancellationRequestedAtUtc is not null)
                {
                    return BackupTaskStoreResultCode.AlreadyExists;
                }

                task.RequestCancellation(command.OccurredAtUtc, storageMode);
                return BackupTaskStoreResultCode.Succeeded;
            },
            cancellationToken);
    }

    public Task<BackupTaskStoreResult<BackupTaskStateModel>> RetryFailedAsync(
        BackupTaskMutationCommand command,
        CancellationToken cancellationToken = default)
    {
        return MutateTaskAsync(
            command,
            RetryReason,
            "backup.task.retry",
            (task, storageMode) =>
            {
                if (task.PlanId is not null) return BackupTaskStoreResultCode.StateMismatch;
                task.RetryFailed(storageMode, command.OccurredAtUtc);
                return BackupTaskStoreResultCode.Succeeded;
            },
            cancellationToken);
    }

    public Task<BackupTaskStoreResult<BackupTaskStateModel>> RequestReconciliationAsync(
        BackupTaskMutationCommand command,
        CancellationToken cancellationToken = default)
    {
        return MutateTaskAsync(
            command,
            AdminReconciliationReason,
            "backup.task.reconciliation.request",
            (task, _) =>
            {
                task.RequestReconciliation(command.OccurredAtUtc);
                return BackupTaskStoreResultCode.Succeeded;
            },
            cancellationToken,
            requireActor: true);
    }

    public async Task<BackupTaskStateModel?> FindTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await persistence.CreateDbContextAsync(cancellationToken);
        var task = await context.BackupTasks.SingleOrDefaultAsync(
            item => item.Id == taskId,
            cancellationToken);
        return task?.ToStateModel();
    }

    private async Task<BackupTaskStoreResult<BackupTaskStateModel>> MutateTaskAsync(
        BackupTaskMutationCommand command,
        string reason,
        string auditAction,
        Func<BackupTask, BackupStorageMode, BackupTaskStoreResultCode> mutate,
        CancellationToken cancellationToken,
        bool requireActor = false)
    {
        try
        {
            return await persistence.ExecuteWithStrategyAsync(async context =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                if ((requireActor && command.ActorAdminUserId is null)
                    || !await IsActorValidAsync(context, command.ActorAdminUserId, command.ActorSecurityStamp, cancellationToken))
                    return new BackupTaskStoreResult<BackupTaskStateModel>(BackupTaskStoreResultCode.AuthenticationRequired);
                if (reason != CancellationReason && await context.BackupTasks.AnyAsync(
                    x => x.Id == command.TaskId && x.PlanId != null, cancellationToken))
                    return new BackupTaskStoreResult<BackupTaskStateModel>(BackupTaskStoreResultCode.StateMismatch);
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
                BackupTaskStoreResultCode mutationResult;
                try
                {
                    mutationResult = mutate(task, snapshot.StorageMode);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    return new BackupTaskStoreResult<BackupTaskStateModel>(
                        BackupTaskStoreResultCode.StateMismatch);
                }

                if (mutationResult == BackupTaskStoreResultCode.AlreadyExists)
                {
                    return new BackupTaskStoreResult<BackupTaskStateModel>(
                        mutationResult,
                        task.ToStateModel());
                }

                if (mutationResult != BackupTaskStoreResultCode.Succeeded)
                    return new BackupTaskStoreResult<BackupTaskStateModel>(mutationResult);
                context.AddRange(
                    CreateStateChange(
                        command.MutationId,
                        task,
                        fromStatus,
                        fromStage,
                        reason,
                        null,
                        command.OccurredAtUtc),
                    CreateAudit(command.ActorAdminUserId, task.Id, auditAction, "succeeded", reason));
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

}
