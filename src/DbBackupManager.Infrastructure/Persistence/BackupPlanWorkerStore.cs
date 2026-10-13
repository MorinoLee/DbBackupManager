using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupPlanWorkerStore(IDbContextFactory<PlatformDbContext> factory) : IBackupPlanWorkerStore
{
    public async Task<BackupPlanWorkerSelection> SelectAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("选择时刻必须是 UTC。", nameof(nowUtc));
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
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

        var pending = await (
            from task in db.BackupTasks.AsNoTracking()
            join snapshot in db.BackupTaskSnapshots.AsNoTracking() on task.Id equals snapshot.TaskId
            where task.PlanId != null
                && task.Status == BackupTaskStatus.Pending
                && task.CurrentStage == BackupTaskStage.Backup
                && snapshot.FileNameRuleVersion == "v3"
                && (snapshot.Purpose == BackupRunPurpose.PlanFull
                    || snapshot.Purpose == BackupRunPurpose.PlanDifferential
                    || snapshot.Purpose == BackupRunPurpose.AdHocCopyOnlyFull)
                && (snapshot.StorageMode == BackupStorageMode.LocalOnly
                    || snapshot.StorageMode == BackupStorageMode.LocalAndRemote
                    || snapshot.StorageMode == BackupStorageMode.RemoteOnly)
            orderby task.CreatedAtUtc
            select new PendingTask(task.Id, snapshot.Purpose, snapshot.DatabaseId)).Take(50).ToListAsync(cancellationToken);
        var facts = new Dictionary<Guid, BackupPlanBaselineObservationCache>();
        foreach (var task in pending)
        {
            if (task.Purpose == BackupRunPurpose.PlanDifferential)
            {
                if (!facts.TryGetValue(task.DatabaseId, out var known))
                {
                    var observation = await BackupPlanBaselineFacts.ReadAsync(db, task.DatabaseId, cancellationToken);
                    known = new(observation.HasVerifiedManagedOrdinaryFull, observation.ActiveBaselineConclusion);
                    facts.Add(task.DatabaseId, known);
                }

                if (!BackupPlanBaselineRules.AllowsDifferentialExecution(known.HasVerified, known.Conclusion))
                {
                    continue;
                }
            }

            return new(BackupPlanWorkerSelectionKind.Execute, task.Id);
        }

        return new(BackupPlanWorkerSelectionKind.Idle, null);
    }

    private sealed record PendingTask(Guid Id, BackupRunPurpose? Purpose, Guid DatabaseId);

    private sealed record BackupPlanBaselineObservationCache(bool HasVerified, DifferentialBaselineConclusion? Conclusion);
}
