namespace DbBackupManager.Domain.BackupLogs;

public sealed record LogBudgetLimits(
    long? MaxPendingFileCount,
    long? MaxPendingBytes,
    long? MinimumFreeBytes);

public sealed record LogBudgetObservation(
    long? PendingFileCount,
    long? PendingBytes,
    long? FreeBytes,
    bool HasUnknownReservations = false);

public sealed class LogBudgetAdmission
{
    internal LogBudgetAdmission(bool allowsNewLog, string reasonCode)
    {
        AllowsNewLog = allowsNewLog;
        ReasonCode = reasonCode;
    }

    public bool AllowsNewLog { get; }

    public string ReasonCode { get; }

    /// <summary>新 LOG 预算不足不阻止原文件的核对、续传或传输。</summary>
    public bool AllowsOriginalFileWork { get; } = true;

    /// <summary>纯规则不是 SQL 调用授权。</summary>
    public bool IsInvocationAuthorization { get; }

    /// <summary>实际额度占用必须由后续协调层以短事务原子预约。</summary>
    public bool RequiresTransactionalReservation { get; } = true;
}

/// <summary>
/// 新 LOG 的纯预算判断。未知占用不归零；手动 LOG 不豁免；这里不执行原子预约。
/// 同一规则由调用方按每库与全局分别评估，两个范围都允许才可准入。
/// 调用方不得为手动请求增加绕过本判断的分支。
/// </summary>
public static class LogBudgetRules
{
    public static LogBudgetAdmission Evaluate(LogBudgetLimits? limits, LogBudgetObservation? observation)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (limits.MaxPendingFileCount is null
            || limits.MaxPendingBytes is null
            || limits.MinimumFreeBytes is null)
        {
            return Denied(LogReason.BudgetConfigMissing);
        }

        if (limits.MaxPendingFileCount <= 0
            || limits.MaxPendingBytes <= 0
            || limits.MinimumFreeBytes <= 0)
        {
            return Denied(LogReason.BudgetConfigInvalid);
        }

        ArgumentNullException.ThrowIfNull(observation);
        if (observation.PendingFileCount is null
            || observation.PendingBytes is null
            || observation.FreeBytes is null
            || observation.PendingFileCount < 0
            || observation.PendingBytes < 0
            || observation.FreeBytes < 0)
        {
            return Denied(LogReason.BudgetObservationUnknown);
        }

        if (observation.HasUnknownReservations)
        {
            return Denied(LogReason.BudgetReservationUnknown);
        }

        if (observation.PendingFileCount >= limits.MaxPendingFileCount)
        {
            return Denied(LogReason.BudgetFileCountExceeded);
        }

        if (observation.PendingBytes >= limits.MaxPendingBytes)
        {
            return Denied(LogReason.BudgetBytesExceeded);
        }

        if (observation.FreeBytes < limits.MinimumFreeBytes)
        {
            return Denied(LogReason.BudgetFreeSpaceLow);
        }

        return new LogBudgetAdmission(allowsNewLog: true, LogReason.BudgetAdmissible);
    }

    private static LogBudgetAdmission Denied(string reason) =>
        new(allowsNewLog: false, reason);
}
