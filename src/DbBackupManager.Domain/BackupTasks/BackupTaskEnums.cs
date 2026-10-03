namespace DbBackupManager.Domain.BackupTasks;

public enum BackupStorageMode
{
    LocalOnly = 1,
    LocalAndRemote = 2,
    RemoteOnly = 3
}

public enum BackupTaskStatus
{
    Pending = 1,
    Running = 2,
    NeedsAttention = 3,
    Succeeded = 4,
    Failed = 5,
    Cancelled = 6
}

public enum BackupTaskStage
{
    Backup = 1,
    VerifyLocal = 2,
    Transfer = 3,
    ValidateCopy = 4,
    Cleanup = 5
}

public enum BackupAttemptHandling
{
    CreateNew = 1,
    ReuseExisting = 2
}

public enum BackupTaskTriggerType
{
    Scheduled = 1,
    Manual = 2
}

public enum BackupLeasePurpose
{
    Execution = 1,
    Reconciliation = 2
}

public enum BackupInvocationStatus
{
    Prepared = 1,
    Running = 2,
    Succeeded = 3,
    ConfirmedFailed = 4,
    Indeterminate = 5
}

public enum BackupReconciliationOutcome
{
    Succeeded = 1,
    SafeToRetry = 2,
    Failed = 3,
    Cancelled = 4,
    Inconclusive = 5
}

public enum BackupFileLocation
{
    Local = 1,
    Remote = 2
}

public enum BackupFileStatus
{
    Available = 1,
    DeletePending = 2,
    DeleteFailed = 3,
    Deleted = 4,
    Missing = 5
}
