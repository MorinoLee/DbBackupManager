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
        var candidates = await (
            from set in db.BackupSets.AsNoTracking()
            join snapshot in db.BackupTaskSnapshots.AsNoTracking() on set.TaskId equals snapshot.TaskId
            where set.DatabaseId == databaseId
                && snapshot.Purpose == BackupRunPurpose.PlanFull
                && !snapshot.UseCopyOnly
                && set.Metadata.Type.Value == BackupType.Full
                && set.Metadata.IsCopyOnly.Value == false
                && set.Metadata.BackupSetGuid.Value != null
                && set.Metadata.BackupSetGuid.Value != Guid.Empty
            orderby set.CreatedAtUtc descending
            select set.Metadata.BackupSetGuid.Value).ToListAsync(cancellationToken);
        foreach (var guid in candidates)
        {
            if (guid is not Guid backupSetGuid || backupSetGuid == Guid.Empty)
            {
                continue;
            }

            if (await BackupPlanExecutionStore.FindQualifiedFullInContextAsync(
                    db, databaseId, backupSetGuid, cancellationToken, asNoTracking: true) is not null)
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
