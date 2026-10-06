namespace DbBackupManager.Domain.Configuration;

public enum CredentialKind
{
    SqlPassword = 1,
    SmbPassword = 2,
    SftpPassword = 3,
    SftpPrivateKey = 4,
    SmtpPassword = 5
}

public enum FileTransferProtocol
{
    Smb = 1,
    Sftp = 2
}

public enum SqlConnectionStatus
{
    Unknown = 1,
    Connected = 2,
    Failed = 3
}

public enum BackupType
{
    Full = 1,
    Differential = 2,
    Log = 3
}

public enum BackupPlanMode
{
    Full = 1,
    FullAndDifferential = 2,
    FullAndDifferentialAndLog = 3
}

public enum DatabaseRecoveryModel
{
    Full = 1,
    BulkLogged = 2,
    Simple = 3,
    Unknown = 4
}

public enum BackupScheduleType
{
    Daily = 1,
    Weekly = 2
}

[Flags]
public enum BackupWeekdays
{
    None = 0,
    Monday = 1 << 0,
    Tuesday = 1 << 1,
    Wednesday = 1 << 2,
    Thursday = 1 << 3,
    Friday = 1 << 4,
    Saturday = 1 << 5,
    Sunday = 1 << 6
}
