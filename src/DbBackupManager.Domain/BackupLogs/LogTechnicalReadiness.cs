using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.BackupLogs;

public enum LogTechnicalReadiness
{
    Ready = 1,
    NotReady = 2,
    Unknown = 3,
    Unsupported = 4
}

/// <summary>
/// LOG 技术就绪的输入事实。技术就绪不等于拥有受管数据起点、位置连续可恢复或调用授权。
/// </summary>
public sealed record LogTechnicalReadinessFacts
{
    public Guid DatabaseId { get; init; }

    public bool? IsOnline { get; init; }

    public DatabaseRecoveryModel? RecoveryModel { get; init; }

    public BackupDatabaseIdentity? ManagedDatabase { get; init; }

    public BackupDatabaseIdentity? SourceDatabase { get; init; }

    public BackupRecoveryBranch? CurrentBranch { get; init; }

    public BackupLsn? LastLogBackupLsn { get; init; }

    public LogCapabilityState Capability { get; init; } = LogCapabilityState.Unknown;

    public LogResourceAvailability ResourceAvailability { get; init; } = LogResourceAvailability.Unknown;

    public LogEvidenceStatus ObservationStatus { get; init; } = LogEvidenceStatus.Complete;

    public LogObservationSource Source { get; init; } = LogObservationSource.Unspecified;

    public DateTimeOffset? ObservedAtUtc { get; init; }

    /// <summary>仅用于表达场景；历史受管 FULL 不豁免技术就绪。</summary>
    public bool HasHistoricalManagedFull { get; init; }
}

public sealed record LogTechnicalReadinessDecision(LogTechnicalReadiness Readiness, string ReasonCode)
{
    /// <summary>只表示技术前置可继续评估，不是 SQL 调用授权。</summary>
    public bool IsReady => Readiness == LogTechnicalReadiness.Ready;
}

/// <summary>
/// 纯技术准入规则；Ready 只表示可以继续后续授权协调，不是执行或调用授权。
/// </summary>
public static class LogTechnicalReadinessRules
{
    public static LogTechnicalReadinessDecision Evaluate(LogTechnicalReadinessFacts? facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.ObservedAtUtc is { } observedAtUtc && observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("观察时间必须使用 UTC。", nameof(facts));
        }

        RequireDefined(facts.Capability, nameof(facts.Capability));
        RequireDefined(facts.ResourceAvailability, nameof(facts.ResourceAvailability));
        RequireDefined(facts.ObservationStatus, nameof(facts.ObservationStatus));
        RequireDefined(facts.Source, nameof(facts.Source));
        if (facts.RecoveryModel is { } recoveryModel)
        {
            RequireDefined(recoveryModel, nameof(facts.RecoveryModel));
        }

        switch (facts.ObservationStatus)
        {
            case LogEvidenceStatus.SourceConflict:
                return Unknown(LogReason.ObservationConflict);
            case LogEvidenceStatus.PermissionDenied:
                return Unknown(LogReason.ObservationPermissionDenied);
            case LogEvidenceStatus.MissingFields:
            case LogEvidenceStatus.HistoryNotFound:
                return Unknown(LogReason.ObservationMissing);
        }

        if (facts.DatabaseId == Guid.Empty)
        {
            return Unknown(LogReason.PlatformDatabaseMissing);
        }

        if (facts.IsOnline is null)
        {
            return Unknown(LogReason.ObservationMissing);
        }

        if (!facts.IsOnline.Value)
        {
            return NotReady(LogReason.DatabaseOffline);
        }

        if (facts.RecoveryModel is null or DatabaseRecoveryModel.Unknown)
        {
            return Unknown(LogReason.RecoveryModelUnknown);
        }

        if (facts.RecoveryModel != DatabaseRecoveryModel.Full)
        {
            return NotReady(LogReason.RecoveryModelNotFull);
        }

        switch (facts.Capability)
        {
            case LogCapabilityState.Unsupported:
                return new LogTechnicalReadinessDecision(LogTechnicalReadiness.Unsupported, LogReason.CapabilityUnsupported);
            case LogCapabilityState.Unknown:
                return Unknown(LogReason.CapabilityUnknown);
        }

        if (!CompleteIdentity(facts.ManagedDatabase) || !CompleteIdentity(facts.SourceDatabase))
        {
            return Unknown(LogReason.IdentityUnknown);
        }

        if (facts.ManagedDatabase != facts.SourceDatabase)
        {
            return NotReady(LogReason.IdentityMismatch);
        }

        if (!CompleteBranch(facts.CurrentBranch))
        {
            return Unknown(LogReason.BranchUnknown);
        }

        if (facts.LastLogBackupLsn is null)
        {
            return NotReady(LogReason.ChainNotReady);
        }

        return facts.ResourceAvailability switch
        {
            LogResourceAvailability.Available => new LogTechnicalReadinessDecision(
                LogTechnicalReadiness.Ready,
                LogReason.TechnicalReady),
            LogResourceAvailability.Busy => NotReady(LogReason.ResourceBusy),
            LogResourceAvailability.BudgetUnavailable => NotReady(LogReason.ResourceBudgetUnavailable),
            _ => Unknown(LogReason.ResourceUnknown),
        };
    }

    private static LogTechnicalReadinessDecision NotReady(string reason) =>
        new(LogTechnicalReadiness.NotReady, reason);

    private static LogTechnicalReadinessDecision Unknown(string reason) =>
        new(LogTechnicalReadiness.Unknown, reason);

    private static bool CompleteIdentity(BackupDatabaseIdentity? identity) =>
        identity is { DatabaseGuid: { } databaseGuid, FamilyGuid: { } familyGuid }
        && databaseGuid != Guid.Empty
        && familyGuid != Guid.Empty;

    /// <summary>
    /// 技术就绪只要求当前活动 fork 已知；目录首 fork 是诊断证据，缺失本身不阻断当前就绪。
    /// </summary>
    private static bool CompleteBranch(BackupRecoveryBranch? branch) =>
        branch is { RecoveryForkId: { } recoveryFork }
        && recoveryFork != Guid.Empty;

    private static void RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "枚举值无效。");
        }
    }
}
