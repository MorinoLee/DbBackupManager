namespace DbBackupManager.Domain.BackupLogs;

public sealed record LogAgeDecision(
    TimeSpan? RunAge,
    TimeSpan? LastSqlSuccessAge,
    bool HasSqlSuccess,
    bool Alert,
    string ReasonCode);

/// <summary>
/// LOG 延迟告警计龄。运行计龄按当前连续启用窗口计算；原始成功年龄仍是墙钟年龄，不是 RPO 承诺。
/// </summary>
public static class LogAgeRules
{
    public static LogAgeDecision Evaluate(
        DateTimeOffset nowUtc,
        DateTimeOffset windowStartedAtUtc,
        DateTimeOffset? lastSqlSuccessObservedAtUtc,
        bool isPaused,
        int intervalMinutes)
    {
        RequireUtc(nowUtc, nameof(nowUtc));
        RequireUtc(windowStartedAtUtc, nameof(windowStartedAtUtc));
        if (lastSqlSuccessObservedAtUtc is { } lastSuccess)
        {
            RequireUtc(lastSuccess, nameof(lastSqlSuccessObservedAtUtc));
            if (lastSuccess > nowUtc)
            {
                throw new ArgumentException("最近 SQL 成功观察时间不能晚于当前时间。", nameof(lastSqlSuccessObservedAtUtc));
            }
        }

        if (windowStartedAtUtc > nowUtc)
        {
            throw new ArgumentException("启用窗口起点不能晚于当前时间。", nameof(windowStartedAtUtc));
        }

        if (intervalMinutes is < 1 or > 1_440)
        {
            throw new ArgumentOutOfRangeException(
                nameof(intervalMinutes),
                "LOG 间隔必须介于 1 与 1440 分钟之间。");
        }

        var successAge = lastSqlSuccessObservedAtUtc is { } observed
            ? nowUtc - observed
            : (TimeSpan?)null;
        if (isPaused)
        {
            return new LogAgeDecision(
                null,
                successAge,
                lastSqlSuccessObservedAtUtc is not null,
                Alert: false,
                LogReason.AgePaused);
        }

        var runAgeStart = lastSqlSuccessObservedAtUtc is { } success && success > windowStartedAtUtc
            ? success
            : windowStartedAtUtc;
        var runAge = nowUtc - runAgeStart;
        var threshold = TimeSpan.FromMinutes(intervalMinutes * 2);
        var alert = runAge > threshold;
        var reason = alert
            ? LogReason.AgeExceeded
            : lastSqlSuccessObservedAtUtc is null
                ? LogReason.AgeNoSuccess
                : LogReason.AgeOk;
        return new LogAgeDecision(
            runAge,
            successAge,
            lastSqlSuccessObservedAtUtc is not null,
            alert,
            reason);
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("时间必须使用 UTC。", parameterName);
        }
    }
}
