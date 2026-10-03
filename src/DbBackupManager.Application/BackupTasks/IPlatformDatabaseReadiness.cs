namespace DbBackupManager.Application.BackupTasks;

/// <summary>当前进程的平台数据库可用性；不代表备份目标或 Worker 心跳健康。</summary>
public interface IPlatformDatabaseReadiness
{
    bool IsReady { get; }

    Task WaitUntilReadyAsync(CancellationToken cancellationToken);
}
