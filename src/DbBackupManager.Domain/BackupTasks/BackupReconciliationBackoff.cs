namespace DbBackupManager.Domain.BackupTasks;

/// <summary>结果未知的核对从 15 秒起按指数后退，最长 30 分钟。</summary>
public static class BackupReconciliationBackoff
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(30);

    public static TimeSpan DelayAfterAttempt(int attemptCount)
    {
        if (attemptCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptCount), "核对次数必须从 1 开始。");
        }

        var delay = InitialDelay;
        for (var step = 1; step < attemptCount; step++)
        {
            if (delay >= MaximumDelay)
            {
                return MaximumDelay;
            }

            delay = delay <= MaximumDelay - delay ? delay + delay : MaximumDelay;
        }

        return delay;
    }
}
