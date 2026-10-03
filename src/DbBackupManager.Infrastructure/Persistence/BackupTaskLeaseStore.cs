using System.Data;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using Microsoft.EntityFrameworkCore;

using static DbBackupManager.Infrastructure.Persistence.BackupTaskPersistence;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupTaskLeaseStore(BackupTaskPersistence persistence)
{
    private const string ClaimedReason = "execution.claimed";

    private const string PreparationFailedReason = "execution.preparation_failed";

    private const string InvalidPathCode = "backup_path_invalid";

    private const string ExpiredReason = "execution.lease_expired";

    public Task<BackupTaskStoreResult<BackupExecutionWorkItem>> ClaimNextAsync(
        ClaimNextBackupTaskCommand command,
        CancellationToken cancellationToken = default) =>
        ClaimAsync(null, command, cancellationToken);

    public Task<BackupTaskStoreResult<BackupExecutionWorkItem>> ClaimTaskAsync(
        Guid taskId,
        ClaimNextBackupTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("任务标识不能为空。", nameof(taskId));
        return ClaimAsync(taskId, command, cancellationToken);
    }

    private async Task<BackupTaskStoreResult<BackupExecutionWorkItem>> ClaimAsync(
        Guid? requiredTaskId,
        ClaimNextBackupTaskCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await persistence.ExecuteWithStrategyAsync(async context =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);
                var replay = await context.BackupTaskStateChanges.SingleOrDefaultAsync(
                    change => change.MutationId == command.MutationId,
                    cancellationToken);
                if (replay is not null)
                {
                    if (requiredTaskId is { } replayTaskId && replay.TaskId != replayTaskId)
                    {
                        await transaction.CommitAsync(cancellationToken);
                        return new BackupTaskStoreResult<BackupExecutionWorkItem>(
                            BackupTaskStoreResultCode.StateMismatch);
                    }

                    var replayResult = await ReplayClaimAsync(
                        context,
                        replay,
                        command,
                        cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return replayResult;
                }

                var tasks = requiredTaskId is { } exactTaskId
                    ? context.BackupTasks.FromSqlInterpolated(
                        $"SELECT TOP (1) * FROM [BackupTasks] WITH (UPDLOCK, READPAST, ROWLOCK) WHERE [Id] = {exactTaskId} AND [Status] = 'Pending'")
                    : context.BackupTasks.FromSqlRaw(
                        "SELECT TOP (1) * FROM [BackupTasks] WITH (UPDLOCK, READPAST, ROWLOCK) "
                        + "WHERE [Status] = 'Pending' ORDER BY [CreatedAtUtc], [Id]");
                var task = await tasks
                    .AsTracking()
                    .SingleOrDefaultAsync(cancellationToken);
                if (task is null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new BackupTaskStoreResult<BackupExecutionWorkItem>(
                        BackupTaskStoreResultCode.NotFound);
                }

                var snapshot = await context.BackupTaskSnapshots.SingleAsync(
                    item => item.TaskId == task.Id,
                    cancellationToken);
                BackupAttempt attempt;
                if (task.CurrentStage == BackupTaskStage.Backup)
                {
                    if (!BackupTaskPathFactory.TryCreate(
                            snapshot, command.BackupAttemptId, command.AcquiredAtUtc, out var paths))
                    {
                        const string message = "备份路径规则或完整路径无效，任务未开始执行。";
                        task.RejectPendingBackup(snapshot.StorageMode, command.AcquiredAtUtc, InvalidPathCode, message);
                        context.AddRange(
                            CreateStateChange(command.MutationId, task, BackupTaskStatus.Pending,
                                BackupTaskStage.Backup, PreparationFailedReason, message, command.AcquiredAtUtc),
                            CreateAudit(null, task.Id, "backup.task.prepare", "failed", InvalidPathCode));
                        NotificationOutboxWriter.EnqueueTaskStage(context, command.MutationId, task.Id,
                            BackupTaskStage.Backup, BackupStageOutcome.ConfirmedFailed, command.AcquiredAtUtc, InvalidPathCode);
                        await context.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        return new BackupTaskStoreResult<BackupExecutionWorkItem>(
                            BackupTaskStoreResultCode.ConfigurationUnavailable);
                    }

                    var maximumAttemptNumber = await context.BackupAttempts
                        .Where(item => item.TaskId == task.Id)
                        .Select(item => (int?)item.AttemptNumber)
                        .MaxAsync(cancellationToken) ?? 0;
                    attempt = new BackupAttempt(
                        command.BackupAttemptId,
                        task.Id,
                        checked(maximumAttemptNumber + 1),
                        command.AcquiredAtUtc,
                        paths);
                    context.BackupAttempts.Add(attempt);
                }
                else
                {
                    if (task.CurrentBackupAttemptId is null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                        return new BackupTaskStoreResult<BackupExecutionWorkItem>(
                            BackupTaskStoreResultCode.StateMismatch);
                    }

                    attempt = await context.BackupAttempts.AsTracking().SingleAsync(
                        item => item.Id == task.CurrentBackupAttemptId.Value,
                        cancellationToken);
                }

                var fromStatus = task.Status;
                var fromStage = task.CurrentStage;
                task.ClaimExecution(
                    snapshot.StorageMode,
                    attempt.Id,
                    command.LeaseToken,
                    command.LeaseOwner,
                    command.AcquiredAtUtc,
                    command.ExpiresAtUtc);
                context.AddRange(
                    CreateStateChange(
                        command.MutationId,
                        task,
                        fromStatus,
                        fromStage,
                        ClaimedReason,
                        null,
                        command.AcquiredAtUtc),
                    CreateAudit(null, task.Id, "backup.task.claim", "succeeded", null));
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return new BackupTaskStoreResult<BackupExecutionWorkItem>(
                    BackupTaskStoreResultCode.Succeeded,
                    CreateWorkItem(task, snapshot, attempt));
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new BackupTaskStoreResult<BackupExecutionWorkItem>(
                BackupTaskStoreResultCode.ConcurrencyConflict);
        }
        catch (DbUpdateException)
        {
            await using var context = await persistence.CreateDbContextAsync(cancellationToken);
            var replay = await context.BackupTaskStateChanges.SingleOrDefaultAsync(
                change => change.MutationId == command.MutationId,
                cancellationToken);
            return replay is null
                ? new BackupTaskStoreResult<BackupExecutionWorkItem>(
                    BackupTaskStoreResultCode.ConcurrencyConflict)
                : await ReplayClaimAsync(context, replay, command, cancellationToken);
        }
    }

    public async Task<BackupTaskStoreResult<LeaseHandle>> RenewLeaseAsync(
        LeaseHandle lease,
        DateTimeOffset utcNow,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

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

                if (HasSameLeaseIdentity(task, lease)
                    && task.LeaseExpiresAtUtc == expiresAtUtc
                    && expiresAtUtc > utcNow)
                {
                    return new BackupTaskStoreResult<LeaseHandle>(
                        BackupTaskStoreResultCode.AlreadyApplied,
                        task.ToLeaseHandle());
                }

                var leaseError = ValidateLease(task, lease, utcNow);
                if (leaseError is not null)
                {
                    return new BackupTaskStoreResult<LeaseHandle>(leaseError.Value);
                }

                task.RenewLease(lease.LeaseToken, lease.Purpose, utcNow, expiresAtUtc);
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

    public async Task<BackupTaskStoreResult<BackupExecutionWorkItem>> RefreshLeaseWorkItemAsync(
        Guid taskId,
        Guid leaseToken,
        BackupLeasePurpose purpose,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        await using var context = await persistence.CreateDbContextAsync(cancellationToken);
        var task = await context.BackupTasks.SingleOrDefaultAsync(
            item => item.Id == taskId,
            cancellationToken);
        if (task is null)
        {
            return new BackupTaskStoreResult<BackupExecutionWorkItem>(
                BackupTaskStoreResultCode.NotFound);
        }

        if (task.LeaseToken != leaseToken
            || task.LeasePurpose != purpose
            || task.LeaseExpiresAtUtc is null
            || task.LeaseExpiresAtUtc <= utcNow)
        {
            return new BackupTaskStoreResult<BackupExecutionWorkItem>(
                BackupTaskStoreResultCode.LeaseLost);
        }

        var workItem = await LoadWorkItemAsync(context, task, cancellationToken);
        return workItem is null
            ? new BackupTaskStoreResult<BackupExecutionWorkItem>(
                BackupTaskStoreResultCode.StateMismatch)
            : new BackupTaskStoreResult<BackupExecutionWorkItem>(
                BackupTaskStoreResultCode.Succeeded,
                workItem);
    }

    public async Task<IReadOnlyList<Guid>> FindExpiredExecutionTaskIdsAsync(
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
            .Where(task => task.Status == BackupTaskStatus.Running
                && task.LeasePurpose == BackupLeasePurpose.Execution
                && task.LeaseExpiresAtUtc <= utcNow)
            .OrderBy(task => task.LeaseExpiresAtUtc)
            .ThenBy(task => task.Id)
            .Select(task => task.Id)
            .Take(maximumCount)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<BackupTaskStoreResult<BackupTaskStateModel>> ExpireExecutionLeaseAsync(
        ExpireExecutionLeaseCommand command,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await persistence.ExecuteWithStrategyAsync(async context =>
            {
                var replay = await ReadMutationAsync(
                    context,
                    command.MutationId,
                    command.TaskId,
                    ExpiredReason,
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
                    task.RecordExpiredExecutionLease(
                        snapshot.StorageMode,
                        command.UtcNow,
                        command.ErrorCode,
                        command.ErrorMessage);
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
                        ExpiredReason,
                        command.ErrorMessage,
                        command.UtcNow),
                    CreateAudit(null, task.Id, "backup.task.lease.expire", "succeeded", ExpiredReason));
                NotificationOutboxWriter.EnqueueLeaseExpired(
                    context,
                    command.MutationId,
                    task.Id,
                    fromStage ?? throw new InvalidOperationException("过期执行租约缺少阶段。"),
                    command.UtcNow,
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
                command.TaskId,
                command.MutationId,
                ExpiredReason,
                cancellationToken);
        }
    }

    private static async Task<BackupTaskStoreResult<BackupExecutionWorkItem>> ReplayClaimAsync(
        PlatformDbContext context,
        BackupTaskStateChange replay,
        ClaimNextBackupTaskCommand command,
        CancellationToken cancellationToken)
    {
        if (string.Equals(replay.ReasonCode, PreparationFailedReason, StringComparison.Ordinal))
        {
            return new BackupTaskStoreResult<BackupExecutionWorkItem>(
                BackupTaskStoreResultCode.ConfigurationUnavailable);
        }

        if (!string.Equals(replay.ReasonCode, ClaimedReason, StringComparison.Ordinal)
            || replay.BackupAttemptId != command.BackupAttemptId)
        {
            return new BackupTaskStoreResult<BackupExecutionWorkItem>(
                BackupTaskStoreResultCode.StateMismatch);
        }

        var task = await context.BackupTasks.SingleAsync(
            item => item.Id == replay.TaskId,
            cancellationToken);
        if (task.LeaseToken != command.LeaseToken
            || task.CurrentBackupAttemptId != replay.BackupAttemptId)
        {
            return new BackupTaskStoreResult<BackupExecutionWorkItem>(
                BackupTaskStoreResultCode.AlreadyApplied);
        }

        var workItem = await LoadWorkItemAsync(context, task, cancellationToken);
        return workItem is null
            ? new BackupTaskStoreResult<BackupExecutionWorkItem>(
                BackupTaskStoreResultCode.AlreadyApplied)
            : new BackupTaskStoreResult<BackupExecutionWorkItem>(
                BackupTaskStoreResultCode.AlreadyApplied,
                workItem);
    }
}
