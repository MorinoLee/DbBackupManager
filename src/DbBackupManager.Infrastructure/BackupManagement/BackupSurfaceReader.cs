using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.BackupManagement;

internal static class BackupSurfaceReader
{
    public static async Task<IReadOnlyList<BackupTaskSummary>> EnrichTasksAsync(
        PlatformDbContext db,
        IReadOnlyList<BackupTaskSummary> tasks,
        CancellationToken token)
    {
        if (tasks.Count == 0)
        {
            return tasks;
        }

        var ids = tasks.Select(task => task.Id).ToArray();
        var identities = await db.BackupTasks.AsNoTracking()
            .Where(task => ids.Contains(task.Id))
            .Select(task => new
            {
                task.Id,
                task.BackupType,
                task.PlanId,
                task.PlanVersionId,
                task.CurrentBackupAttemptId,
            })
            .ToListAsync(token);
        var versionIds = identities.Where(item => item.PlanVersionId != null).Select(item => item.PlanVersionId!.Value).Distinct().ToArray();
        var versionById = new Dictionary<Guid, int>();
        if (versionIds.Length > 0)
        {
            var versions = await db.BackupPlanVersions.AsNoTracking()
                .Where(version => versionIds.Contains(version.Id))
                .Select(version => new { version.Id, version.Number })
                .ToListAsync(token);
            versionById = versions.ToDictionary(item => item.Id, item => item.Number);
        }

        var modes = await db.BackupTaskSnapshots.AsNoTracking()
            .Where(snapshot => ids.Contains(snapshot.TaskId))
            .Select(snapshot => new { snapshot.TaskId, snapshot.StorageMode })
            .ToListAsync(token);
        var operations = await db.BackupPlanExecutionOperations.AsNoTracking()
            .Where(operation => ids.Contains(operation.TaskId))
            .ToListAsync(token);
        var sets = await db.BackupSets.AsNoTracking()
            .Where(set => ids.Contains(set.TaskId))
            .ToListAsync(token);
        var identityById = identities.ToDictionary(item => item.Id);
        var modeByTask = modes.ToDictionary(item => item.TaskId, item => item.StorageMode);
        return tasks.Select(task =>
        {
            if (!identityById.TryGetValue(task.Id, out var identity))
            {
                return task;
            }

            var surface = BackupSurfaceFacts.Describe(
                identity.BackupType,
                identity.PlanId,
                identity.PlanVersionId,
                identity.PlanVersionId is { } versionId && versionById.TryGetValue(versionId, out var number) ? number : null,
                modeByTask.TryGetValue(task.Id, out var mode) ? mode : null,
                identity.CurrentBackupAttemptId,
                operations.Where(operation => operation.TaskId == task.Id)
                    .Select(operation => new BackupOperationFact(
                        operation.Kind,
                        operation.Sequence,
                        operation.State,
                        operation.Facts.Outcome,
                        operation.Facts.SqlSuccessObserved))
                    .ToArray(),
                sets.Where(set => set.TaskId == task.Id).Select(Fact).ToArray());
            return task with
            {
                BackupType = surface.BackupType,
                PlanId = surface.PlanId,
                PlanVersionId = surface.PlanVersionId,
                PlanVersionNumber = surface.PlanVersionNumber,
                SqlStatus = surface.SqlStatus,
                FileVerifyStatus = surface.FileVerifyStatus,
                TransferStatus = surface.TransferStatus,
                DependencyStatus = surface.DependencyStatus,
            };
        }).ToArray();
    }

    public static async Task<IReadOnlyList<BackupFileSummary>> EnrichFilesAsync(
        PlatformDbContext db,
        IReadOnlyList<BackupFileSummary> files,
        DateTimeOffset nowUtc,
        CancellationToken token)
    {
        if (files.Count == 0)
        {
            return files;
        }

        var fileIds = files.Select(file => file.FileId).ToArray();
        var links = await db.BackupFiles.AsNoTracking()
            .Where(file => fileIds.Contains(file.Id))
            .Select(file => new { file.Id, file.BackupSetId, file.DatabaseId })
            .ToListAsync(token);
        var setIds = links.Where(link => link.BackupSetId != null).Select(link => link.BackupSetId!.Value).Distinct().ToArray();
        var sets = setIds.Length == 0
            ? new List<BackupSet>()
            : await db.BackupSets.AsNoTracking().Where(set => setIds.Contains(set.Id)).ToListAsync(token);
        var setById = sets.ToDictionary(set => set.Id);
        var reasons = new Dictionary<Guid, string>();
        foreach (var databaseId in links.Where(link => link.BackupSetId != null).Select(link => link.DatabaseId).Distinct())
        {
            var surface = await PlanLocationRetentionWork.LoadSurfaceAsync(db, databaseId, nowUtc, token);
            foreach (var copy in surface.Assessments.SelectMany(item => item.Copies))
            {
                reasons[copy.FileId] = copy.Reason;
            }
        }

        var linkById = links.ToDictionary(link => link.Id);
        return files.Select(file =>
        {
            if (!linkById.TryGetValue(file.FileId, out var link))
            {
                return file;
            }

            Guid? baseline = null;
            if (link.BackupSetId is { } setId && setById.TryGetValue(setId, out var set))
            {
                baseline = BackupSurfaceFacts.DifferentialBase(set.Metadata.Type.State, set.Metadata.Type.Value, set.BaseBackupSetId);
            }

            return file with
            {
                BackupSetId = link.BackupSetId,
                DifferentialBaseBackupSetId = baseline,
                RetentionReason = reasons.TryGetValue(file.FileId, out var reason) ? reason : null,
            };
        }).ToArray();
    }

    private static BackupDependencyFact Fact(BackupSet set) => new(
        set.AttemptId,
        set.Metadata.Type.State,
        set.Metadata.Type.Value,
        set.BaseBackupSetId,
        set.Assessment.State,
        set.Assessment.Conclusion,
        set.HasMetadataConflict);
}
