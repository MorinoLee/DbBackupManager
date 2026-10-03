using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Domain.Tests.BackupTasks;

internal static class P57ReconciliationTaskFactory
{
    public static BackupTask CreateNeedsAttention(
        BackupStorageMode storageMode,
        DateTimeOffset now,
        out Guid executionToken)
    {
        var task = new BackupTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            BackupTaskTriggerType.Manual,
            null);
        executionToken = Guid.NewGuid();
        task.ClaimExecution(
            storageMode,
            Guid.NewGuid(),
            executionToken,
            "worker-a",
            now,
            now.AddMinutes(1));
        task.RecordIndeterminateResult(
            storageMode,
            executionToken,
            now.AddSeconds(1),
            "result_unknown",
            "合成的不确定结果");
        return task;
    }
}
