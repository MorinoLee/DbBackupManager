namespace DbBackupManager.Domain.BackupTasks;

// 单活动 Worker 的运行观测，不参与任务所有权和租约判断。
public sealed class WorkerHeartbeat
{
    public int Id { get; private set; }
    public Guid ProcessId { get; private set; }
    public DateTimeOffset LastSeenAtUtc { get; private set; }
    public bool IsStopped { get; private set; }
}
