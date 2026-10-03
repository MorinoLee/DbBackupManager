namespace DbBackupManager.Domain.Notifications;

public enum SmtpSecurityMode
{
    StartTls = 1,
    TlsOnConnect = 2,
    Plaintext = 3
}

public enum NotificationType
{
    TaskFailed = 1,
    TaskIndeterminate = 2,
    RetentionDeleteFailed = 3,
    RetentionMissing = 4,
    AdminTest = 5
}

public enum NotificationSourceKind
{
    BackupTask = 1,
    BackupFile = 2,
    AdminRequest = 3
}

public enum NotificationStatus
{
    Pending = 1,
    Sending = 2,
    Sent = 3,
    SendFailed = 4,
    Discarded = 5
}

public static class NotificationSendPolicy
{
    public static TimeSpan Backoff(int sendAttemptCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sendAttemptCount, 1);
        return sendAttemptCount switch
        {
            1 => TimeSpan.FromMinutes(1),
            2 => TimeSpan.FromMinutes(5),
            3 => TimeSpan.FromMinutes(15),
            4 => TimeSpan.FromHours(1),
            _ => TimeSpan.FromHours(6),
        };
    }
}
