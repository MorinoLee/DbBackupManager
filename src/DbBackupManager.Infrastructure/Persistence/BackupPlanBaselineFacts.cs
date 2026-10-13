using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal static class BackupPlanBaselineFacts
{
    internal static async Task<BackupPlanBaselineObservation> ReadAsync(
        PlatformDbContext db, Guid databaseId, CancellationToken cancellationToken)
    {
        return new(
            await HasVerifiedManagedOrdinaryFullAsync(db, databaseId, cancellationToken),
            await ReadActiveConclusionAsync(db, databaseId, cancellationToken),
            await HasOutstandingOrdinaryPlanFullAsync(db, databaseId, cancellationToken));
    }

    internal static async Task<bool> HasVerifiedManagedOrdinaryFullAsync(
        PlatformDbContext db, Guid databaseId, CancellationToken cancellationToken)
    {
        var sets = await db.BackupSets.Where(set => set.DatabaseId == databaseId).ToArrayAsync(cancellationToken);
        foreach (var set in sets)
        {
            if (set.Metadata.Type.Value != BackupType.Full || set.Metadata.IsCopyOnly.Value != false)
            {
                continue;
            }

            if (set.Metadata.BackupSetGuid.Value is not Guid guid || guid == Guid.Empty)
            {
                continue;
            }

            var snapshot = await db.BackupTaskSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TaskId == set.TaskId, cancellationToken);
            if (snapshot?.Purpose != BackupRunPurpose.PlanFull || snapshot.UseCopyOnly)
            {
                continue;
            }

            if (await BackupPlanExecutionStore.FindQualifiedFullInContextAsync(db, databaseId, guid, cancellationToken) is not null)
            {
                return true;
            }
        }

        return false;
    }

    internal static async Task<DifferentialBaselineConclusion?> ReadActiveConclusionAsync(
        PlatformDbContext db, Guid databaseId, CancellationToken cancellationToken)
    {
        var latest = await (
            from row in db.BackupSetEvidence.AsNoTracking()
            join snapshot in db.BackupTaskSnapshots.AsNoTracking() on row.TaskId equals snapshot.TaskId
            where snapshot.DatabaseId == databaseId && row.Kind == BackupSetEvidenceKind.ActiveBaseline
            orderby row.ObservedAtUtc descending, row.EntryNumber descending
            select row).FirstOrDefaultAsync(cancellationToken);
        if (latest is null)
        {
            return null;
        }

        var rows = await (
            from row in db.BackupSetEvidence.AsNoTracking()
            join snapshot in db.BackupTaskSnapshots.AsNoTracking() on row.TaskId equals snapshot.TaskId
            where snapshot.DatabaseId == databaseId
                && row.Kind == BackupSetEvidenceKind.ActiveBaseline
                && row.MutationId == latest.MutationId
                && row.ObservedAtUtc == latest.ObservedAtUtc
            select row).ToArrayAsync(cancellationToken);
        var conclusions = rows.Select(row => row.Assessment.Conclusion).Distinct().ToArray();
        if (conclusions.Length != 1 || conclusions[0] is null)
        {
            return DifferentialBaselineConclusion.Unknown;
        }

        return conclusions[0];
    }

    internal static Task<bool> HasOutstandingOrdinaryPlanFullAsync(
        PlatformDbContext db, Guid databaseId, CancellationToken cancellationToken) =>
        (from task in db.BackupTasks.AsNoTracking()
         join snapshot in db.BackupTaskSnapshots.AsNoTracking() on task.Id equals snapshot.TaskId
         where snapshot.DatabaseId == databaseId
             && snapshot.Purpose == BackupRunPurpose.PlanFull
             && !snapshot.UseCopyOnly
             && (task.Status == BackupTaskStatus.Pending
                 || task.Status == BackupTaskStatus.Running
                 || task.Status == BackupTaskStatus.NeedsAttention)
         select task.Id).AnyAsync(cancellationToken);
}
