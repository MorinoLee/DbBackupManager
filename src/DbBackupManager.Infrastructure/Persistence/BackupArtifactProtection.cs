using DbBackupManager.Domain.BackupTasks;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal static class BackupArtifactProtection
{
    internal static IQueryable<BackupFile> EligibleLegacyFiles(PlatformDbContext context) =>
        context.BackupFiles.Where(file => file.BackupSetId == null
            && context.BackupTasks.Any(task => task.Id == file.TaskId && task.PlanId == null && task.PolicyId != null)
            && context.BackupTaskSnapshots.Any(snapshot => snapshot.TaskId == file.TaskId
                && snapshot.Purpose == null && snapshot.FileNameRuleVersion != "v3" && snapshot.DatabaseId == file.DatabaseId
                && (file.Location == BackupFileLocation.Local && file.DatabaseServerId == snapshot.ServerId
                    || file.Location == BackupFileLocation.Remote && file.StorageTargetId == snapshot.StorageTargetId))
            && context.BackupAttempts.Any(attempt => attempt.Id == file.AttemptId && attempt.TaskId == file.TaskId));

    internal static async Task<bool> IsProtectedAsync(PlatformDbContext context, Guid fileId, CancellationToken token) =>
        await context.BackupFiles.AnyAsync(file => file.Id == fileId, token)
        && !await EligibleLegacyFiles(context).AnyAsync(file => file.Id == fileId, token);
}
