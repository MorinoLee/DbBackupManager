using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.BackupLogs;

public enum LogObservationSource
{
    Unspecified = 0,
    BackupHeader = 1,
    Msdb = 2,
    RecoveryStatus = 3,
    Platform = 4
}

public enum LogEvidenceStatus
{
    Complete = 1,
    MissingFields = 2,
    PermissionDenied = 3,
    HistoryNotFound = 4,
    SourceConflict = 5
}

public enum LogGenerationEvidenceKind
{
    Unspecified = 0,
    BeginsLogChain = 1,
    RecoveryModelTransition = 2,
    IdentityChange = 3,
    RecoveryFork = 4
}

public enum LogCapabilityState
{
    Unknown = 1,
    Supported = 2,
    Unsupported = 3
}

public enum LogResourceAvailability
{
    Unknown = 1,
    Available = 2,
    Busy = 3,
    BudgetUnavailable = 4
}

public enum LogStorageLocation
{
    Local = 1,
    Remote = 2
}

/// <summary>
/// 受约束的具体位置作用域。远端必须携带稳定目标标识，不能只写 Remote 后跨目标拼接。
/// </summary>
public readonly record struct LogLocationScope
{
    public LogLocationScope(LogStorageLocation kind, Guid? storageTargetId = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "枚举值无效。");
        }

        if (kind == LogStorageLocation.Local)
        {
            if (storageTargetId is not null)
            {
                throw new ArgumentException("本地位置不能携带远端存储目标。", nameof(storageTargetId));
            }

            StorageTargetId = null;
        }
        else
        {
            if (storageTargetId is null || storageTargetId == Guid.Empty)
            {
                throw new ArgumentException("远端位置必须携带具体存储目标。", nameof(storageTargetId));
            }

            StorageTargetId = storageTargetId;
        }

        Kind = kind;
    }

    public LogStorageLocation Kind { get; }

    public Guid? StorageTargetId { get; }

    public static LogLocationScope Local { get; } = new(LogStorageLocation.Local);

    public static LogLocationScope Remote(Guid storageTargetId) =>
        new(LogStorageLocation.Remote, storageTargetId);

    public override string ToString() =>
        Kind == LogStorageLocation.Local
            ? "Local"
            : $"Remote:{StorageTargetId}";
}

/// <summary>候选自身绑定的不可变执行选项，不得用当前版本选项重验历史候选。</summary>
public sealed record LogProtectionExpectation(bool UseChecksum, bool UseCompression);

public enum LogLocationCopyState
{
    Available = 1,
    Missing = 2,
    Unknown = 3
}

/// <summary>
/// 链世代证据。没有证据时保留 null/空标识，不能为满足连接条件补造同代。
/// </summary>
public sealed record LogChainGeneration(
    Guid? GenerationId,
    LogGenerationEvidenceKind Evidence = LogGenerationEvidenceKind.Unspecified)
{
    public bool IsKnown => GenerationId is { } id && id != Guid.Empty;
}

/// <summary>
/// 单份备份的实际属性与来源观察。缺失字段保持 null，不能与已知 false 合并。
/// </summary>
public sealed record LogBackupActualMetadata
{
    public BackupType? Type { get; init; }

    public bool? IsCopyOnly { get; init; }

    public DatabaseRecoveryModel? RecoveryModel { get; init; }

    public bool? BeginsLogChain { get; init; }

    public bool? HasBulkLoggedData { get; init; }

    public bool? IsForceOffline { get; init; }

    public bool? UseChecksum { get; init; }

    public bool? UseCompression { get; init; }

    public bool? IsDamaged { get; init; }

    public bool? IsSnapshot { get; init; }

    public bool? IsIncomplete { get; init; }

    public BackupLsn? FirstLsn { get; init; }

    public BackupLsn? LastLsn { get; init; }

    /// <summary>仅作来源证据；不能替代 LOG 覆盖前沿或数据起点的 LastLSN。</summary>
    public BackupLsn? DatabaseBackupLsn { get; init; }

    public BackupLsn? ForkPointLsn { get; init; }

    public BackupDatabaseIdentity? Database { get; init; }

    public BackupRecoveryBranch? Branch { get; init; }

    public LogChainGeneration? Generation { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public DateTimeOffset? ObservedAtUtc { get; init; }

    public LogObservationSource Source { get; init; } = LogObservationSource.Unspecified;

    public LogEvidenceStatus Status { get; init; } = LogEvidenceStatus.Complete;

    public bool HasCompleteIdentity =>
        Database is { DatabaseGuid: { } databaseGuid, FamilyGuid: { } familyGuid }
        && databaseGuid != Guid.Empty
        && familyGuid != Guid.Empty;

    public bool HasCompleteBranch =>
        Branch is { FirstRecoveryForkId: { } firstFork, RecoveryForkId: { } recoveryFork }
        && firstFork != Guid.Empty
        && recoveryFork != Guid.Empty;
}
