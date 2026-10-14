using System.Data;
using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using Microsoft.EntityFrameworkCore;

using static DbBackupManager.Infrastructure.Persistence.BackupTaskPersistence;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupPlanWorkerStore(IDbContextFactory<PlatformDbContext> factory) : IBackupPlanWorkerStore
{
    internal const string BaselineRequiredCode = "baseline_required";

    public async Task<BackupPlanWorkerSelection> SelectAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("选择时刻必须是 UTC。", nameof(nowUtc));
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await CloseScheduledDifferentialsWithoutBaselineAsync(db, nowUtc, cancellationToken);
        var liveRunning = await db.BackupTasks.AsNoTracking().AnyAsync(
            task => task.PlanId != null
                && task.Status == BackupTaskStatus.Running
                && task.LeaseExpiresAtUtc > nowUtc,
            cancellationToken);
        if (liveRunning)
        {
            return new(BackupPlanWorkerSelectionKind.Wait, null);
        }

        var reconcileId = await db.BackupTasks.AsNoTracking()
            .Where(task => task.PlanId != null && (
                (task.Status == BackupTaskStatus.NeedsAttention
                    && task.NextReconciliationAtUtc <= nowUtc
                    && (task.LeaseToken == null || task.LeaseExpiresAtUtc <= nowUtc))
                || (task.Status == BackupTaskStatus.Running && task.LeaseExpiresAtUtc <= nowUtc)))
            .OrderBy(task => task.UpdatedAtUtc)
            .Select(task => (Guid?)task.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (reconcileId is Guid attentionId)
        {
            return new(BackupPlanWorkerSelectionKind.Reconcile, attentionId);
        }

        var blocked = await db.BackupInvocationAuthorizations.AsNoTracking()
                .AnyAsync(item => item.TerminalObservedAtUtc == null, cancellationToken)
            || await db.BackupAttempts.AsNoTracking().AnyAsync(
                attempt => attempt.BackupInvocationStatus == BackupInvocationStatus.Running
                    || attempt.BackupInvocationStatus == BackupInvocationStatus.Indeterminate,
                cancellationToken);
        if (blocked)
        {
            return new(BackupPlanWorkerSelectionKind.Wait, null);
        }

        // 已恢复或已推进到后续阶段的任务同样继续执行，不新建 BACKUP。
        var fullId = await Eligible(db, BackupRunPurpose.PlanFull).FirstOrDefaultAsync(cancellationToken);
        if (fullId != Guid.Empty)
        {
            return new(BackupPlanWorkerSelectionKind.Execute, fullId);
        }

        var copyId = await Eligible(db, BackupRunPurpose.AdHocCopyOnlyFull).FirstOrDefaultAsync(cancellationToken);
        if (copyId != Guid.Empty)
        {
            return new(BackupPlanWorkerSelectionKind.Execute, copyId);
        }

        // 只有仍未调用 BACKUP 的差异任务需要当前基线门禁；已过 Backup 的按历史依赖续跑。
        var differentialDatabases = await (
            from task in db.BackupTasks.AsNoTracking()
            join snapshot in db.BackupTaskSnapshots.AsNoTracking() on task.Id equals snapshot.TaskId
            where task.PlanId != null
                && task.Status == BackupTaskStatus.Pending
                && task.CurrentStage == BackupTaskStage.Backup
                && snapshot.Purpose == BackupRunPurpose.PlanDifferential
            select snapshot.DatabaseId).Distinct().ToListAsync(cancellationToken);
        var allowed = new List<Guid>();
        foreach (var databaseId in differentialDatabases)
        {
            var observation = await BackupPlanBaselineFacts.ReadAsync(db, databaseId, cancellationToken);
            if (BackupPlanBaselineRules.AllowsDifferentialExecution(
                    observation.HasVerifiedManagedOrdinaryFull, observation.ActiveBaselineConclusion))
            {
                allowed.Add(databaseId);
            }
        }

        var differentialId = await (
            from task in db.BackupTasks.AsNoTracking()
            join snapshot in db.BackupTaskSnapshots.AsNoTracking() on task.Id equals snapshot.TaskId
            where task.PlanId != null
                && task.Status == BackupTaskStatus.Pending
                && snapshot.FileNameRuleVersion == "v3"
                && snapshot.Purpose == BackupRunPurpose.PlanDifferential
                && (task.CurrentStage != BackupTaskStage.Backup || allowed.Contains(snapshot.DatabaseId))
                && (snapshot.StorageMode == BackupStorageMode.LocalOnly
                    || snapshot.StorageMode == BackupStorageMode.LocalAndRemote
                    || snapshot.StorageMode == BackupStorageMode.RemoteOnly)
            orderby task.CreatedAtUtc
            select task.Id).FirstOrDefaultAsync(cancellationToken);
        return differentialId == Guid.Empty
            ? new(BackupPlanWorkerSelectionKind.Idle, null)
            : new(BackupPlanWorkerSelectionKind.Execute, differentialId);
    }

    internal static void CloseWithoutBaseline(
        PlatformDbContext db, BackupTask task, BackupStorageMode storageMode, DateTimeOffset nowUtc, Guid? mutationId = null)
    {
        var before = task.Status;
        var stage = task.CurrentStage;
        var key = mutationId ?? BaselineRejectionMutationId(db, task.Id);
        task.RejectPendingBackup(
            storageMode,
            nowUtc,
            BaselineRequiredCode,
            "还没有已核对的受管完整备份基线，该差异时隙已结束，不会改成完整备份，也不会当作未调用失败重试。");
        db.AddRange(
            CreateStateChange(key, task, before, stage, "plan.baseline_required", BaselineRequiredCode, nowUtc),
            CreateAudit(null, task.Id, "backup.plan.task.baseline_required", "closed", BaselineRequiredCode));
        // 明确失败与状态、历史、审计在同一次提交里入队；核对确认成功不补发，这里只处理明确失败。
        NotificationOutboxWriter.EnqueuePlanOutcome(db, key, task.Id, task.CurrentStage, task.Status, nowUtc, BaselineRequiredCode);
    }

    /// <summary>
    /// 补充基线失败通知键：沿用首次收口的状态变更标识，重复选择不会重复入队，
    /// 也与状态变更、历史、审计同一次事务提交。
    /// </summary>
    private static Guid BaselineRejectionMutationId(PlatformDbContext db, Guid taskId)
    {
        var existing = db.BackupTaskStateChanges.Local
            .FirstOrDefault(change => change.TaskId == taskId && change.ReasonCode == "plan.baseline_required");
        return existing?.MutationId
            ?? db.BackupTaskStateChanges.Where(change => change.TaskId == taskId && change.ReasonCode == "plan.baseline_required")
                .Select(change => (Guid?)change.MutationId).FirstOrDefault()
            ?? Guid.NewGuid();
    }

    private async Task CloseScheduledDifferentialsWithoutBaselineAsync(
        PlatformDbContext db, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var pending = await (
            from task in db.BackupTasks.AsNoTracking()
            join snapshot in db.BackupTaskSnapshots.AsNoTracking() on task.Id equals snapshot.TaskId
            where task.PlanId != null
                && task.Status == BackupTaskStatus.Pending
                && task.CurrentStage == BackupTaskStage.Backup
                && task.TriggerType == BackupTaskTriggerType.Scheduled
                && snapshot.Purpose == BackupRunPurpose.PlanDifferential
            select new PendingDifferential(task.Id, snapshot.DatabaseId, snapshot.StorageMode)).ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        var blocked = new HashSet<Guid>();
        foreach (var databaseId in pending.Select(item => item.DatabaseId).Distinct())
        {
            var observation = await BackupPlanBaselineFacts.ReadAsync(db, databaseId, cancellationToken);
            if (!BackupPlanBaselineRules.AllowsDifferentialExecution(
                    observation.HasVerifiedManagedOrdinaryFull, observation.ActiveBaselineConclusion))
            {
                blocked.Add(databaseId);
            }
        }

        var closing = pending.Where(item => blocked.Contains(item.DatabaseId)).ToArray();
        if (closing.Length == 0)
        {
            return;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var write = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await write.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            foreach (var item in closing)
            {
                var task = await write.BackupTasks.FromSqlInterpolated(
                        $"SELECT * FROM [BackupTasks] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {item.Id}")
                    .AsTracking()
                    .SingleOrDefaultAsync(cancellationToken);
                if (task is null || task.Status != BackupTaskStatus.Pending || task.TriggerType != BackupTaskTriggerType.Scheduled)
                {
                    continue;
                }

                CloseWithoutBaseline(write, task, item.StorageMode, nowUtc);
            }

            await write.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    /// <summary>
    /// 可执行候选：与认领时的配置复核保持同一口径，在 SQL 里直接排除已停用的源、实例、凭据和存储目标。
    /// 这样停用的旧任务不会占住队首，同一夹具里的健康任务仍能取得执行机会；
    /// 未终止调用等其他全局阻塞不在这里放宽。
    /// </summary>
    private static IQueryable<Guid> Eligible(PlatformDbContext db, BackupRunPurpose purpose) =>
        from task in db.BackupTasks.AsNoTracking()
        join snapshot in db.BackupTaskSnapshots.AsNoTracking() on task.Id equals snapshot.TaskId
        where task.PlanId != null
            && task.Status == BackupTaskStatus.Pending
            && snapshot.FileNameRuleVersion == "v3"
            && snapshot.Purpose == purpose
            && (snapshot.StorageMode == BackupStorageMode.LocalOnly
                || snapshot.StorageMode == BackupStorageMode.LocalAndRemote
                || snapshot.StorageMode == BackupStorageMode.RemoteOnly)
            && db.ManagedDatabases.Any(database => database.Id == snapshot.DatabaseId
                && database.IsManaged && database.IsAvailable && !database.IsSystemDatabase)
            && db.DatabaseInstances.Any(instance => instance.Id == snapshot.InstanceId && instance.IsEnabled)
            && db.DatabaseServers.Any(server => server.Id == snapshot.ServerId && server.IsEnabled)
            && db.CredentialReferences.Any(credential => credential.Id == snapshot.SqlCredentialReferenceId && credential.IsEnabled)
            && db.CredentialReferences.Any(credential => credential.Id == snapshot.SourceCredentialReferenceId && credential.IsEnabled)
            && (snapshot.StorageTargetId == null
                || db.CredentialReferences.Any(credential => credential.Id == snapshot.RemoteCredentialReferenceId && credential.IsEnabled)
                    && db.StorageTargets.Any(target => target.Id == snapshot.StorageTargetId && target.IsEnabled))
        orderby task.CreatedAtUtc
        select task.Id;

    private sealed record PendingDifferential(Guid Id, Guid DatabaseId, BackupStorageMode StorageMode);
}
