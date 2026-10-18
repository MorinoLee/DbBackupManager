using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Infrastructure.Persistence;

/// <summary>
/// 区分「原调用仍可能运行」与「结果未知但调用已终止」。只有 Running/Indeterminate 且没有
/// 已记录终止的授权才算仍可能运行；无授权的遗留调用没有终止证据，继续按仍可能运行处理。
/// </summary>
internal static class BackupInvocationLiveness
{
    internal static IQueryable<BackupAttempt> StillPossiblyRunning(PlatformDbContext db) =>
        db.BackupAttempts.Where(attempt =>
            (attempt.BackupInvocationStatus == BackupInvocationStatus.Running
                || attempt.BackupInvocationStatus == BackupInvocationStatus.Indeterminate)
            && !db.BackupInvocationAuthorizations.Any(authorization =>
                authorization.TaskId == attempt.TaskId
                && authorization.AttemptId == attempt.Id
                && authorization.TerminalObservedAtUtc != null));
}
