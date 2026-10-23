namespace DbBackupManager.Domain.BackupLogs;

public enum LogPriorCallState
{
    None = 1,
    Active = 2,
    TerminationUnknown = 3,
    TerminatedResultUnknown = 4,
    TerminatedSucceeded = 5,
    TerminatedFailed = 6
}

public enum LogRequestIntent
{
    OriginalSlotReplay = 1,
    FutureScheduledSlot = 2,
    Manual = 3
}

/// <summary>
/// 原 SQL 调用和原授权的事实。Active、终止不可见或仍有遗留 Permit 时，新槽不能绕过阻塞。
/// </summary>
public sealed record LogPriorCallFacts
{
    public LogPriorCallState State { get; init; } = LogPriorCallState.None;

    public bool HasActiveAttempt { get; init; }

    public bool HasOutstandingPermit { get; init; }

    public LogEvidenceStatus TerminationEvidence { get; init; } = LogEvidenceStatus.Complete;
}

public sealed record LogPriorCallDecision(
    bool MayEnterAdmission,
    bool ReusesOriginalInvocation,
    bool PreservesPriorUnknown,
    string ReasonCode)
{
    /// <summary>纯领域决定从不授予 SQL 调用能力。</summary>
    public bool GrantsSqlInvocation { get; }
}

/// <summary>
/// M01=A 的纯事实判定：原调用未证明终止时阻塞；已终止后只允许独立未来意图进入后续准入。
/// </summary>
public static class LogPriorCallRules
{
    public static LogPriorCallDecision Evaluate(LogPriorCallFacts? facts, LogRequestIntent intent)
    {
        ArgumentNullException.ThrowIfNull(facts);
        RequireDefined(facts.State, nameof(facts.State));
        RequireDefined(facts.TerminationEvidence, nameof(facts.TerminationEvidence));
        RequireDefined(intent, nameof(intent));

        if (facts.State == LogPriorCallState.Active || facts.HasActiveAttempt)
        {
            return Blocked(LogReason.PriorCallActive);
        }

        if (facts.HasOutstandingPermit)
        {
            return Blocked(LogReason.PriorPermitOutstanding);
        }

        if (facts.State == LogPriorCallState.TerminationUnknown
            || facts.TerminationEvidence != LogEvidenceStatus.Complete)
        {
            return Blocked(LogReason.PriorCallTerminationUnknown);
        }

        if (intent == LogRequestIntent.OriginalSlotReplay)
        {
            return Blocked(LogReason.OriginalSlotNotReplayable);
        }

        if (facts.State is LogPriorCallState.TerminatedResultUnknown
            or LogPriorCallState.TerminatedSucceeded
            or LogPriorCallState.TerminatedFailed)
        {
            return new LogPriorCallDecision(
                MayEnterAdmission: true,
                ReusesOriginalInvocation: false,
                PreservesPriorUnknown: facts.State == LogPriorCallState.TerminatedResultUnknown,
                StateReason(facts.State));
        }

        return new LogPriorCallDecision(
            MayEnterAdmission: true,
            ReusesOriginalInvocation: false,
            PreservesPriorUnknown: false,
            LogReason.NoPriorCall);
    }

    private static string StateReason(LogPriorCallState state) =>
        state == LogPriorCallState.TerminatedResultUnknown
            ? LogReason.FutureRequestIndependent
            : LogReason.PriorCallTerminated;

    private static LogPriorCallDecision Blocked(string reason) =>
        new(false, false, false, reason);

    private static void RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "枚举值无效。");
        }
    }
}
