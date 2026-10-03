namespace DbBackupManager.Application.BackupTasks;

public interface IWorkerStartupDiagnostics
{
    string? CheckConfiguration();
}
