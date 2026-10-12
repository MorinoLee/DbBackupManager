using System.Data;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class PlanLocationRetentionStore(IDbContextFactory<PlatformDbContext> contextFactory)
    : IPlanLocationRetentionStore
{
    public async Task<IReadOnlyList<PlanLocationAssessment>> AssessAsync(
        Guid databaseId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        RequireDatabase(databaseId);
        RequireUtc(nowUtc, nameof(nowUtc));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await PlanLocationRetentionWork.LoadAsync(context, databaseId, nowUtc, cancellationToken);
    }

    public async Task<PlanStagingRelease> ReadOwnStagingAsync(
        Guid taskId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (taskId == Guid.Empty)
        {
            throw new ArgumentException("任务标识不能为空。", nameof(taskId));
        }

        var now = RequireUtc(nowUtc, nameof(nowUtc));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await context.BackupTaskSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TaskId == taskId, cancellationToken);
        if (snapshot is null)
        {
            return new PlanStagingRelease(false, null, PlanLocationRecovery.RemoteStagingDeferred);
        }

        var assessments = await PlanLocationRetentionWork.LoadAsync(context, snapshot.DatabaseId, now, cancellationToken);
        var locals = assessments
            .Where(item => item.Location == BackupFileLocation.Local)
            .SelectMany(item => item.Copies)
            .Where(copy => copy.BackupSetId is not null)
            .ToArray();
        var files = await context.BackupFiles.AsNoTracking()
            .Where(file => file.TaskId == taskId && file.Location == BackupFileLocation.Local && file.Status != BackupFileStatus.Deleted)
            .Select(file => file.Id)
            .ToListAsync(cancellationToken);
        var matches = locals.Where(copy => files.Contains(copy.FileId)).ToArray();
        if (matches.Length != 1)
        {
            return new PlanStagingRelease(false, null, PlanLocationRecovery.DependencyUnproven);
        }

        var decision = matches[0];
        return new PlanStagingRelease(PlanLocationRecovery.IsStagingReleasable(decision), decision.FileId, decision.Reason);
    }

    public Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ClaimNextAsync(
        Guid databaseId,
        ClaimBackupFileDeletionCommand command,
        CancellationToken cancellationToken = default) =>
        ClaimAsync(databaseId, command, staging: false, onlyTaskId: null, cancellationToken);

    public Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ClaimStagingAsync(
        Guid databaseId,
        ClaimBackupFileDeletionCommand command,
        Guid? onlyTaskId = null,
        CancellationToken cancellationToken = default) =>
        ClaimAsync(databaseId, command, staging: true, onlyTaskId, cancellationToken);

    private Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ClaimAsync(
        Guid databaseId,
        ClaimBackupFileDeletionCommand command,
        bool staging,
        Guid? onlyTaskId,
        CancellationToken cancellationToken)
    {
        RequireDatabase(databaseId);
        ValidateClaim(command);
        var claimedReason = staging
            ? PlanLocationRetentionWork.StagingClaimedReason
            : PlanLocationRetentionWork.ClaimedReason;
        return ExecuteAsync(databaseId, async (context, token) =>
        {
            // 未指定任务时不领取仍在 Running 的暂存。所属任务自己的 Cleanup 会带 onlyTaskId 来领。
            var runningOwners = staging && onlyTaskId is null
                ? (await (
                    from task in context.BackupTasks.AsNoTracking()
                    join snapshot in context.BackupTaskSnapshots.AsNoTracking() on task.Id equals snapshot.TaskId
                    where snapshot.DatabaseId == databaseId
                        && task.Status == BackupTaskStatus.Running
                        && task.LeasePurpose == BackupLeasePurpose.Execution
                    select task.Id).ToListAsync(token)).ToHashSet()
                : new HashSet<Guid>();
            bool Selected(BackupFile file, IReadOnlyDictionary<Guid, PlanCopyRetentionDecision> decisions) =>
                (!onlyTaskId.HasValue || file.TaskId == onlyTaskId.Value)
                && !runningOwners.Contains(file.TaskId)
                && file.BackupSetId is not null
                && decisions.TryGetValue(file.Id, out var decision)
                && (staging
                    ? PlanLocationRecovery.IsStagingReleasable(decision)
                    : PlanLocationRecovery.IsDeletable(decision));

            var replay = await context.BackupFileStateChanges.AsNoTracking()
                .SingleOrDefaultAsync(change => change.MutationId == command.MutationId, token);
            if (replay is not null)
            {
                if (replay.ReasonCode != claimedReason
                    || replay.ToStatus != BackupFileStatus.DeletePending)
                {
                    return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.StateMismatch);
                }

                var existing = await context.BackupFiles.AsNoTracking()
                    .SingleOrDefaultAsync(file => file.Id == replay.FileId, token);
                if (existing is null || existing.DeletionLeaseToken != command.LeaseToken)
                {
                    return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.StateMismatch);
                }

                var endpoint = await BackupFileRetentionStore.TryCreateEndpointAsync(context, existing, true, token);
                return endpoint is null
                    ? new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.ConfigurationUnavailable)
                    : new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                        BackupTaskStoreResultCode.AlreadyApplied,
                        WorkItem(existing, endpoint));
            }

            await PlanLocationRetentionWork.ReleaseSupersededAsync(context, databaseId, command.AcquiredAtUtc, token);
            var assessments = await PlanLocationRetentionWork.LoadAsync(context, databaseId, command.AcquiredAtUtc, token);
            var decisions = PlanLocationRetentionWork.Decisions(assessments);
            var planCopies = await PlanLocationRetentionWork.PlanCopyIdsAsync(context, databaseId, token);
            var expired = await context.BackupFiles.AsTracking()
                .Where(file => file.DatabaseId == databaseId
                    && file.Status == BackupFileStatus.DeletePending
                    && file.DeletionLeaseExpiresAtUtc <= command.AcquiredAtUtc)
                .OrderBy(file => file.Id)
                .ToListAsync(token);
            foreach (var file in expired)
            {
                if (!planCopies.Contains(file.Id) || !Selected(file, decisions))
                {
                    continue;
                }

                var endpoint = await BackupFileRetentionStore.TryCreateEndpointAsync(context, file, true, token);
                if (endpoint is null)
                {
                    continue;
                }

                file.TakeOverExpiredDeletionLease(command.LeaseToken, command.LeaseOwner, command.AcquiredAtUtc, command.ExpiresAtUtc);
                context.BackupFileStateChanges.Add(Change(
                    command.MutationId,
                    file,
                    BackupFileStatus.DeletePending,
                    BackupFileStatus.DeletePending,
                    claimedReason,
                    command.AcquiredAtUtc));
                context.AuditRecords.Add(Audit(file.Id, staging ? "backup.file.plan_staging.claim" : "backup.file.plan_retention.claim", claimedReason));
                await context.SaveChangesAsync(token);
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                    BackupTaskStoreResultCode.Succeeded,
                    WorkItem(file, endpoint));
            }

            var candidates = await context.BackupFiles.AsTracking()
                .Where(file => file.DatabaseId == databaseId
                    && (file.Status == BackupFileStatus.Available
                        || file.Status == BackupFileStatus.DeleteFailed
                            && file.NextDeletionAttemptAtUtc <= command.AcquiredAtUtc))
                .ToListAsync(token);
            var completions = (await context.BackupSets.AsNoTracking()
                .Where(set => set.DatabaseId == databaseId)
                .ToListAsync(token))
                .ToDictionary(set => set.Id, set => set.Completion.CompletedAtUtc);
            var selected = candidates
                .Where(file => Selected(file, decisions))
                .OrderBy(file => completions.GetValueOrDefault(file.BackupSetId!.Value) ?? DateTimeOffset.MaxValue)
                .ThenBy(file => file.Id)
                .FirstOrDefault();
            if (selected is null)
            {
                await context.SaveChangesAsync(token);
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.NotFound);
            }

            var selectedEndpoint = await BackupFileRetentionStore.TryCreateEndpointAsync(context, selected, true, token);
            if (selectedEndpoint is null)
            {
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.ConfigurationUnavailable);
            }

            var from = selected.Status;
            selected.ClaimDeletion(command.LeaseToken, command.LeaseOwner, command.AcquiredAtUtc, command.ExpiresAtUtc);
            context.BackupFileStateChanges.Add(Change(
                command.MutationId,
                selected,
                from,
                BackupFileStatus.DeletePending,
                claimedReason,
                command.AcquiredAtUtc));
            context.AuditRecords.Add(Audit(selected.Id, staging ? "backup.file.plan_staging.claim" : "backup.file.plan_retention.claim", claimedReason));
            await context.SaveChangesAsync(token);
            return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                BackupTaskStoreResultCode.Succeeded,
                WorkItem(selected, selectedEndpoint));
        }, cancellationToken);
    }

    public Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> RefreshAsync(
        Guid fileId,
        Guid leaseToken,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (fileId == Guid.Empty || leaseToken == Guid.Empty)
        {
            throw new ArgumentException("刷新删除租约必须提供副本和租约标识。");
        }

        var now = RequireUtc(nowUtc, nameof(nowUtc));
        return RefreshCoreAsync(fileId, leaseToken, now, cancellationToken);
    }

    private async Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> RefreshCoreAsync(
        Guid fileId,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var databaseId = await LookupDatabaseIdAsync(fileId, cancellationToken);
        if (databaseId is null)
        {
            return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.NotFound);
        }

        return await ExecuteAsync(databaseId.Value, async (context, token) =>
        {
            var file = await context.BackupFiles.AsTracking().SingleOrDefaultAsync(item => item.Id == fileId, token);
            if (file is null)
            {
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.NotFound);
            }

            if (file.DeletionLeaseToken != leaseToken || file.Status != BackupFileStatus.DeletePending)
            {
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.StateMismatch);
            }

            if (file.DeletionLeaseExpiresAtUtc is null || file.DeletionLeaseExpiresAtUtc <= now)
            {
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.LeaseLost);
            }

            var planCopies = await PlanLocationRetentionWork.PlanCopyIdsAsync(context, file.DatabaseId, token);
            if (!planCopies.Contains(file.Id))
            {
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.StateMismatch);
            }

            var decisions = PlanLocationRetentionWork.Decisions(
                await PlanLocationRetentionWork.LoadAsync(context, file.DatabaseId, now, token));
            if (!decisions.TryGetValue(file.Id, out var decision) || !PlanLocationRecovery.RetainsPhysicalLease(decision))
            {
                file.CancelDeletionLease(now);
                context.BackupFileStateChanges.Add(Change(
                    Guid.NewGuid(),
                    file,
                    BackupFileStatus.DeletePending,
                    BackupFileStatus.Available,
                    PlanLocationRetentionWork.ReleasedReason,
                    now));
                context.AuditRecords.Add(Audit(file.Id, "backup.file.plan_retention.release", PlanLocationRetentionWork.ReleasedReason));
                await context.SaveChangesAsync(token);
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.Protected);
            }

            var endpoint = await BackupFileRetentionStore.TryCreateEndpointAsync(context, file, true, token);
            return endpoint is null
                ? new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.ConfigurationUnavailable)
                : new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.Succeeded, WorkItem(file, endpoint));
        }, cancellationToken);
    }

    public Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> CommitAsync(
        BackupFileRetentionWorkItem work,
        BackupFileRetentionCommitCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(command);
        if (command.MutationId == Guid.Empty)
        {
            throw new ArgumentException("删除提交必须提供 MutationId。", nameof(command));
        }

        var occurredAt = RequireUtc(command.OccurredAtUtc, nameof(command));
        if (!Enum.IsDefined(command.Outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        if (command.Outcome == BackupFileRetentionOutcome.Failed && string.IsNullOrWhiteSpace(command.ErrorCode))
        {
            throw new ArgumentException("删除失败必须提供稳定错误码。", nameof(command));
        }

        return ExecuteAsync(work.DatabaseId, async (context, token) =>
        {
            var replay = await context.BackupFileStateChanges.AsNoTracking()
                .SingleOrDefaultAsync(change => change.MutationId == command.MutationId, token);
            if (replay is not null)
            {
                return replay.FileId == work.FileId
                    ? new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.AlreadyApplied)
                    : new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.StateMismatch);
            }

            var file = await context.BackupFiles.AsTracking().SingleOrDefaultAsync(item => item.Id == work.FileId, token);
            if (file is null)
            {
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.NotFound);
            }

            if (file.Status != BackupFileStatus.DeletePending
                || file.DeletionLeaseToken != work.LeaseToken
                || file.DeletionLeaseExpiresAtUtc is null
                || file.DeletionLeaseExpiresAtUtc <= occurredAt)
            {
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.LeaseLost);
            }

            if (!file.RowVersion.SequenceEqual(work.RowVersion))
            {
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.ConcurrencyConflict);
            }

            var decisions = PlanLocationRetentionWork.Decisions(
                await PlanLocationRetentionWork.LoadAsync(context, file.DatabaseId, occurredAt, token));
            var deletable = decisions.TryGetValue(file.Id, out var decision) && PlanLocationRecovery.RetainsPhysicalLease(decision);
            if (command.Outcome == BackupFileRetentionOutcome.Deleted && !deletable)
            {
                file.CancelDeletionLease(occurredAt);
                context.BackupFileStateChanges.Add(Change(
                    command.MutationId,
                    file,
                    BackupFileStatus.DeletePending,
                    BackupFileStatus.Available,
                    PlanLocationRetentionWork.ReleasedReason,
                    occurredAt));
                await context.SaveChangesAsync(token);
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.Protected);
            }

            var from = file.Status;
            switch (command.Outcome)
            {
                case BackupFileRetentionOutcome.Deleted:
                    file.RecordDeleted(work.LeaseToken, occurredAt);
                    await PlanLocationRetentionWork.NoteStagingCleanedAsync(context, file, occurredAt, token);
                    break;
                case BackupFileRetentionOutcome.Missing:
                    file.RecordMissing(work.LeaseToken, occurredAt);
                    break;
                case BackupFileRetentionOutcome.Failed:
                    file.RecordDeletionFailure(
                        work.LeaseToken,
                        occurredAt,
                        command.ErrorCode!,
                        occurredAt + BackupFileRetentionPolicy.DeletionBackoff(file.DeletionAttemptCount));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command));
            }

            context.BackupFileStateChanges.Add(Change(command.MutationId, file, from, file.Status, Reason(command.Outcome), occurredAt));
            context.AuditRecords.Add(Audit(file.Id, "backup.file.plan_retention.commit", Reason(command.Outcome)));
            await context.SaveChangesAsync(token);
            var endpoint = await BackupFileRetentionStore.TryCreateEndpointAsync(context, file, false, token);
            // 已删除、缺失和失败都会清掉文件上的租约。返回项沿用领取时的租约标识，避免提交成功后回滚。
            return endpoint is null
                ? new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.Succeeded)
                : new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                    BackupTaskStoreResultCode.Succeeded,
                    WorkItem(file, endpoint, work.LeaseToken, work.LeaseExpiresAtUtc));
        }, cancellationToken);
    }

    public Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ReadExpiredProtectedAsync(
        Guid databaseId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        RequireDatabase(databaseId);
        var now = RequireUtc(nowUtc, nameof(nowUtc));
        return ExecuteAsync(databaseId, async (context, token) =>
        {
            var planCopies = await PlanLocationRetentionWork.PlanCopyIdsAsync(context, databaseId, token);
            var decisions = PlanLocationRetentionWork.Decisions(
                await PlanLocationRetentionWork.LoadAsync(context, databaseId, now, token));
            var expired = await context.BackupFiles.AsNoTracking()
                .Where(file => file.DatabaseId == databaseId
                    && file.Status == BackupFileStatus.DeletePending
                    && file.DeletionLeaseExpiresAtUtc <= now)
                .OrderBy(file => file.Id)
                .ToListAsync(token);
            foreach (var file in expired)
            {
                if (!planCopies.Contains(file.Id))
                {
                    continue;
                }

                if (decisions.TryGetValue(file.Id, out var decision) && PlanLocationRecovery.RetainsPhysicalLease(decision))
                {
                    continue;
                }

                var endpoint = await BackupFileRetentionStore.TryCreateEndpointAsync(context, file, true, token);
                if (endpoint is null)
                {
                    continue;
                }

                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                    BackupTaskStoreResultCode.Succeeded,
                    WorkItem(file, endpoint));
            }

            return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.NotFound);
        }, cancellationToken);
    }

    public async Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ResolveExpiredAsync(
        Guid fileId,
        Guid leaseToken,
        bool stillPresent,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (fileId == Guid.Empty || leaseToken == Guid.Empty)
        {
            throw new ArgumentException("收口过期删除必须提供副本和租约标识。");
        }

        var now = RequireUtc(nowUtc, nameof(nowUtc));
        var databaseId = await LookupDatabaseIdAsync(fileId, cancellationToken);
        if (databaseId is null)
        {
            return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.NotFound);
        }

        return await ExecuteAsync(databaseId.Value, async (context, token) =>
        {
            var file = await context.BackupFiles.AsTracking().SingleOrDefaultAsync(item => item.Id == fileId, token);
            if (file is null)
            {
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.NotFound);
            }

            if (file.Status != BackupFileStatus.DeletePending || file.DeletionLeaseToken != leaseToken)
            {
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.StateMismatch);
            }

            if (file.DeletionLeaseExpiresAtUtc is null || file.DeletionLeaseExpiresAtUtc > now)
            {
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.StateMismatch);
            }

            var planCopies = await PlanLocationRetentionWork.PlanCopyIdsAsync(context, file.DatabaseId, token);
            if (!planCopies.Contains(file.Id))
            {
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.StateMismatch);
            }

            var decisions = PlanLocationRetentionWork.Decisions(
                await PlanLocationRetentionWork.LoadAsync(context, file.DatabaseId, now, token));
            var deletable = decisions.TryGetValue(file.Id, out var decision) && PlanLocationRecovery.RetainsPhysicalLease(decision);
            if (stillPresent && deletable)
            {
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.Protected);
            }

            var from = file.Status;
            if (stillPresent)
            {
                file.ResolveExpiredDeletion(leaseToken, now, true);
            }
            else
            {
                file.ResolveExpiredDeletion(leaseToken, now, false);
            }

            var reason = stillPresent ? PlanLocationRetentionWork.ReleasedReason : "file.missing";
            context.BackupFileStateChanges.Add(Change(Guid.NewGuid(), file, from, file.Status, reason, now));
            context.AuditRecords.Add(Audit(file.Id, "backup.file.plan_retention.resolve", reason));
            await context.SaveChangesAsync(token);
            return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.Succeeded);
        }, cancellationToken);
    }

    private async Task<Guid?> LookupDatabaseIdAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await using var lookup = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await lookup.BackupFiles.AsNoTracking()
            .Where(file => file.Id == fileId)
            .Select(file => (Guid?)file.DatabaseId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ExecuteAsync(
        Guid databaseId,
        Func<PlatformDbContext, CancellationToken, Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAsync(async (context, token) =>
            {
                if (!await context.ManagedDatabases.AnyAsync(item => item.Id == databaseId, token))
                {
                    return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.NotFound);
                }

                if (!await PlanRetentionLock.TryAcquireAsync(context, databaseId, token))
                {
                    return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.DatabaseBlocked);
                }

                await BackupInvocationAuthorizationStore.LockDatabaseAsync(context, databaseId, token);
                return await operation(context, token);
            }, cancellationToken);
        }
        catch (Exception exception) when (IsDeadlock(exception))
        {
            return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.DatabaseBlocked);
        }
        catch (DbUpdateException)
        {
            return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.ConcurrencyConflict);
        }
    }

    private async Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ExecuteAsync(
        Func<PlatformDbContext, CancellationToken, Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>>> operation,
        CancellationToken cancellationToken)
    {
        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var result = await operation(context, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    private static bool IsDeadlock(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql && sql.Number == 1205)
            {
                return true;
            }
        }

        return false;
    }

    private static BackupFileRetentionWorkItem WorkItem(BackupFile file, BackupFileEndpointInput endpoint) =>
        WorkItem(
            file,
            endpoint,
            file.DeletionLeaseToken ?? throw new InvalidOperationException("删除租约缺少标识。"),
            file.DeletionLeaseExpiresAtUtc ?? throw new InvalidOperationException("删除租约缺少到期时间。"));

    private static BackupFileRetentionWorkItem WorkItem(
        BackupFile file,
        BackupFileEndpointInput endpoint,
        Guid leaseToken,
        DateTimeOffset leaseExpiresAtUtc) =>
        new(
            file.Id,
            file.TaskId,
            file.DatabaseId,
            file.Location,
            file.StorageTargetId,
            file.DatabaseServerId,
            file.Protocol,
            file.Path,
            file.LengthBytes,
            file.Status,
            leaseToken,
            leaseExpiresAtUtc,
            file.RowVersion,
            endpoint);

    private static BackupFileStateChange Change(
        Guid mutationId,
        BackupFile file,
        BackupFileStatus from,
        BackupFileStatus to,
        string reason,
        DateTimeOffset occurredAt) =>
        new(mutationId, file.Id, file.TaskId, from, to, reason, occurredAt);

    private static AuditRecord Audit(Guid fileId, string action, string reason) =>
        new(null, action, "BackupFile", fileId.ToString("N"), "succeeded", reason);

    private static string Reason(BackupFileRetentionOutcome outcome) => outcome switch
    {
        BackupFileRetentionOutcome.Deleted => PlanLocationRetentionWork.DeletedReason,
        BackupFileRetentionOutcome.Missing => "file.missing",
        BackupFileRetentionOutcome.Failed => "file.delete_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static void ValidateClaim(ClaimBackupFileDeletionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.LeaseToken == Guid.Empty || command.MutationId == Guid.Empty)
        {
            throw new ArgumentException("删除领取必须提供租约和 MutationId。", nameof(command));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command.LeaseOwner);
        var acquired = RequireUtc(command.AcquiredAtUtc, nameof(command));
        var expires = RequireUtc(command.ExpiresAtUtc, nameof(command));
        if (expires <= acquired || command.LeaseOwner.Length > 200)
        {
            throw new ArgumentException("删除租约时间或持有者无效。", nameof(command));
        }
    }

    private static void RequireDatabase(Guid databaseId)
    {
        if (databaseId == Guid.Empty)
        {
            throw new ArgumentException("数据库标识不能为空。", nameof(databaseId));
        }
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName) =>
        value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentException("时间必须使用 UTC。", parameterName);
}
