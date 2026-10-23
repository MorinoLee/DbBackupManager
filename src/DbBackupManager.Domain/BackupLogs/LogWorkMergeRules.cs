using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.BackupLogs;

public enum LogMergeDisposition
{
    Mergeable = 1,
    NotMergeable = 2
}

public sealed record LogMergeDecision(LogMergeDisposition Disposition, string ReasonCode);

/// <summary>
/// 尚未进入执行的计划内自动 LOG 事实。归并只输出决定，不重写任务或快照。
/// </summary>
public sealed record LogPendingWorkFacts
{
    public BackupType BackupType { get; init; } = BackupType.Log;

    public BackupTaskTriggerType Trigger { get; init; } = BackupTaskTriggerType.Scheduled;

    public bool HasAttempt { get; init; }

    public bool HasSqlReservation { get; init; }

    public bool HasInvocationPermit { get; init; }

    public bool HasExternalSideEffect { get; init; }

    public bool IsEnded { get; init; }
}

/// <summary>
/// 只输出“可归并/不可归并”决定；不终结、重写或删除任何任务，也不覆盖手动和在途快照。
/// </summary>
public static class LogWorkMergeRules
{
    /// <summary>FULL/DIFF 时隙从不覆盖 LOG 时隙。</summary>
    public static bool DatabaseBackupCoversLog => false;

    public static LogMergeDecision Evaluate(LogPendingWorkFacts? work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (work.BackupType != BackupType.Log)
        {
            return NotMergeable(LogReason.MergeNotLog);
        }

        if (work.Trigger == BackupTaskTriggerType.Manual)
        {
            return NotMergeable(LogReason.MergeManual);
        }

        if (work.IsEnded)
        {
            return NotMergeable(LogReason.MergeEnded);
        }

        if (work.HasAttempt)
        {
            return NotMergeable(LogReason.MergeAttemptExists);
        }

        if (work.HasSqlReservation)
        {
            return NotMergeable(LogReason.MergeReservationExists);
        }

        if (work.HasInvocationPermit)
        {
            return NotMergeable(LogReason.MergePermitExists);
        }

        if (work.HasExternalSideEffect)
        {
            return NotMergeable(LogReason.MergeSideEffectExists);
        }

        return new LogMergeDecision(LogMergeDisposition.Mergeable, LogReason.MergeEligible);
    }

    private static LogMergeDecision NotMergeable(string reason) =>
        new(LogMergeDisposition.NotMergeable, reason);
}
