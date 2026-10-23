using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.BackupLogs;

public enum LogDataStartAvailability
{
    Available = 1,
    Missing = 2,
    Unknown = 3
}

/// <summary>
/// 某个存储位置上一份候选数据备份。FULL 与 DIFF 都按该位置独立提供。
/// </summary>
public sealed record LogDataStartCandidate
{
    public Guid BackupSetId { get; init; }

    public LogBackupActualMetadata Actual { get; init; } = new();

    public LogStorageLocation Location { get; init; } = LogStorageLocation.Local;

    public LogLocationCopyState LocationCopy { get; init; } = LogLocationCopyState.Available;

    public bool IsManaged { get; init; } = true;

    /// <summary>DIFF 是否已由既有依赖规则核对为实际匹配受管普通 FULL。</summary>
    public DifferentialBaselineConclusion? DifferentialDependency { get; init; }

    public Guid? MatchesFullBackupSetId { get; init; }
}

public sealed class LogDataStartRequest
{
    public LogDataStartRequest(
        LogStorageLocation location,
        IEnumerable<LogDataStartCandidate> candidates,
        Guid? preferredFullBackupSetId = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        Location = location;
        Candidates = Array.AsReadOnly(candidates.ToArray());
        PreferredFullBackupSetId = preferredFullBackupSetId;
    }

    public LogStorageLocation Location { get; }

    public IReadOnlyList<LogDataStartCandidate> Candidates { get; }

    /// <summary>可选，由调用方按恢复窗口固定一份 FULL；未指定时选择 LastLSN 最大的可用普通 FULL。</summary>
    public Guid? PreferredFullBackupSetId { get; }
}

public sealed class LogDataStartDecision
{
    internal LogDataStartDecision(
        LogDataStartAvailability availability,
        string reasonCode,
        LogStorageLocation location,
        Guid? fullBackupSetId = null,
        Guid? differentialBackupSetId = null,
        BackupLsn? restorePointLsn = null,
        BackupDatabaseIdentity? database = null,
        BackupRecoveryBranch? branch = null,
        LogChainGeneration? generation = null)
    {
        Availability = availability;
        ReasonCode = reasonCode;
        Location = location;
        FullBackupSetId = fullBackupSetId;
        DifferentialBackupSetId = differentialBackupSetId;
        RestorePointLsn = restorePointLsn;
        Database = database;
        Branch = branch;
        Generation = generation;
    }

    public LogDataStartAvailability Availability { get; }

    public string ReasonCode { get; }

    public LogStorageLocation Location { get; }

    public Guid? FullBackupSetId { get; }

    public Guid? DifferentialBackupSetId { get; }

    /// <summary>初始覆盖前沿 R = 实际选定 FULL 或匹配 DIFF 的 LastLSN。</summary>
    public BackupLsn? RestorePointLsn { get; }

    public BackupDatabaseIdentity? Database { get; }

    public BackupRecoveryBranch? Branch { get; }

    public LogChainGeneration? Generation { get; }

    public bool IsAvailable => Availability == LogDataStartAvailability.Available;
}

/// <summary>
/// 按存储位置选择受管普通 FULL 或实际匹配 DIFF 作为数据起点；不访问 SQL/文件。
/// </summary>
public static class LogDataStartRules
{
    public static LogDataStartDecision Select(LogDataStartRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireDefined(request.Location, nameof(request.Location));
        foreach (var candidate in request.Candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(candidate.Actual);
            if (candidate.BackupSetId == Guid.Empty)
            {
                throw new ArgumentException("数据备份集标识不能为空。", nameof(request));
            }

            RequireDefined(candidate.Location, nameof(candidate.Location));
            RequireDefined(candidate.LocationCopy, nameof(candidate.LocationCopy));
            RequireDefined(candidate.Actual.Status, nameof(candidate.Actual.Status));
            if (candidate.DifferentialDependency is { } dependency)
            {
                RequireDefined(dependency, nameof(candidate.DifferentialDependency));
            }
            if (candidate.Actual.ObservedAtUtc is { } observedAtUtc
                && observedAtUtc.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException("观察时间必须使用 UTC。", nameof(request));
            }
        }

        var local = request.Candidates
            .Where(candidate => candidate.Location == request.Location)
            .ToArray();

        if (local.Length == 0)
        {
            return Missing(
                request.Location,
                request.Candidates.Count == 0
                    ? LogReason.StartNoManagedFull
                    : LogReason.StartLocationMissing);
        }

        if (request.PreferredFullBackupSetId is { } preferredFullId)
        {
            var preferred = local
                .Where(candidate => candidate.BackupSetId == preferredFullId)
                .ToArray();
            if (preferred.Length == 0)
            {
                return Missing(request.Location, LogReason.StartNoManagedFull);
            }

            if (!IsUsableFull(preferred[0]))
            {
                return Unavailable(preferred[0], request.Location);
            }

            return Available(preferred[0], local, request.Location);
        }

        var usableFulls = local
            .Where(IsUsableFull)
            .OrderByDescending(candidate => candidate.Actual.LastLsn!.Value.Value)
            .ThenBy(candidate => candidate.BackupSetId)
            .ToArray();

        if (usableFulls.Length > 0)
        {
            return Available(usableFulls[0], local, request.Location);
        }

        var fullCandidates = local
            .Where(candidate => candidate.Actual.Type is null or BackupType.Full)
            .ToArray();

        if (fullCandidates.Length == 0)
        {
            return Missing(request.Location, LogReason.StartNoManagedFull);
        }

        if (fullCandidates.Any(IsUnknownFullEvidence))
        {
            return Unknown(request.Location, LogReason.StartEvidenceUnknown);
        }

        return Missing(request.Location, FullUnavailableReason(fullCandidates));
    }

    private static bool IsUsableFull(LogDataStartCandidate candidate)
    {
        var actual = candidate.Actual;
        return candidate.IsManaged
            && candidate.LocationCopy == LogLocationCopyState.Available
            && actual.Status == LogEvidenceStatus.Complete
            && actual.Type == BackupType.Full
            && actual.IsCopyOnly == false
            && actual.LastLsn is not null
            && actual.IsDamaged == false
            && actual.IsSnapshot == false
            && actual.IsIncomplete == false
            && actual.HasCompleteIdentity
            && actual.HasCompleteBranch
            && actual.Branch!.FirstRecoveryForkId == actual.Branch.RecoveryForkId
            && actual.Generation is { IsKnown: true };
    }

    private static bool IsUnknownFullEvidence(LogDataStartCandidate candidate)
    {
        var actual = candidate.Actual;
        return candidate.LocationCopy == LogLocationCopyState.Unknown
            || actual.Status != LogEvidenceStatus.Complete
            || actual.Type is null
            || actual.IsCopyOnly is null
            || actual.LastLsn is null
            || actual.IsDamaged is null
            || actual.IsSnapshot is null
            || actual.IsIncomplete is null
            || !actual.HasCompleteIdentity
            || !actual.HasCompleteBranch
            || actual.Generation is not { IsKnown: true };
    }

    private static string FullUnavailableReason(IReadOnlyList<LogDataStartCandidate> candidates)
    {
        if (candidates.Any(candidate => !candidate.IsManaged))
        {
            return LogReason.StartExternalNotManaged;
        }

        if (candidates.Any(candidate => candidate.Actual.IsCopyOnly == true))
        {
            return LogReason.StartCopyOnlyNotEligible;
        }

        if (candidates.Any(candidate =>
            candidate.Actual.Type is BackupType.Full
            && candidate.LocationCopy == LogLocationCopyState.Missing))
        {
            return LogReason.StartLocationMissing;
        }

        if (candidates.Any(candidate =>
            candidate.Actual.Type is BackupType.Full
            && candidate.Actual.HasCompleteBranch
            && candidate.Actual.Branch!.FirstRecoveryForkId != candidate.Actual.Branch.RecoveryForkId))
        {
            return LogReason.StartBranchUnsupported;
        }

        if (candidates.Any(candidate =>
            candidate.Actual.IsDamaged == true
            || candidate.Actual.IsSnapshot == true
            || candidate.Actual.IsIncomplete == true))
        {
            return LogReason.StartBackupInvalid;
        }

        return LogReason.StartNoManagedFull;
    }

    private static LogDataStartDecision Available(
        LogDataStartCandidate full,
        IReadOnlyList<LogDataStartCandidate> candidates,
        LogStorageLocation location)
    {
        var differential = candidates
            .Where(candidate => IsUsableDifferential(candidate, full))
            .OrderByDescending(candidate => candidate.Actual.LastLsn!.Value.Value)
            .ThenBy(candidate => candidate.BackupSetId)
            .FirstOrDefault();

        return new LogDataStartDecision(
            LogDataStartAvailability.Available,
            differential is null ? LogReason.StartManagedFull : LogReason.StartManagedDifferential,
            location,
            full.BackupSetId,
            differential?.BackupSetId,
            differential?.Actual.LastLsn ?? full.Actual.LastLsn,
            full.Actual.Database,
            full.Actual.Branch,
            full.Actual.Generation);
    }

    private static bool IsUsableDifferential(LogDataStartCandidate candidate, LogDataStartCandidate full)
    {
        var actual = candidate.Actual;
        return candidate.IsManaged
            && candidate.LocationCopy == LogLocationCopyState.Available
            && actual.Status == LogEvidenceStatus.Complete
            && actual.Type == BackupType.Differential
            && actual.IsCopyOnly == false
            && actual.IsDamaged == false
            && actual.IsSnapshot == false
            && actual.IsIncomplete == false
            && actual.FirstLsn is not null
            && actual.LastLsn is not null
            && actual.FirstLsn.Value.Value < actual.LastLsn.Value.Value
            && full.Actual.LastLsn is not null
            && actual.LastLsn.Value.Value > full.Actual.LastLsn.Value.Value
            && actual.HasCompleteIdentity
            && actual.HasCompleteBranch
            && actual.Branch!.FirstRecoveryForkId == actual.Branch.RecoveryForkId
            && actual.Database == full.Actual.Database
            && actual.Branch == full.Actual.Branch
            && actual.Generation is { IsKnown: true }
            && full.Actual.Generation is { IsKnown: true }
            && actual.Generation.GenerationId == full.Actual.Generation.GenerationId
            && candidate.DifferentialDependency == DifferentialBaselineConclusion.Verified
            && candidate.MatchesFullBackupSetId == full.BackupSetId;
    }

    private static LogDataStartDecision Unavailable(
        LogDataStartCandidate candidate,
        LogStorageLocation location)
    {
        var actual = candidate.Actual;
        if (!candidate.IsManaged)
        {
            return Missing(location, LogReason.StartExternalNotManaged);
        }

        if (actual.Type is null)
        {
            return Unknown(location, LogReason.StartEvidenceUnknown);
        }

        if (actual.Type != BackupType.Full)
        {
            return Missing(location, LogReason.StartNoManagedFull);
        }

        if (actual.IsCopyOnly == true)
        {
            return Missing(location, LogReason.StartCopyOnlyNotEligible);
        }

        if (actual.IsCopyOnly is null)
        {
            return Unknown(location, LogReason.StartEvidenceUnknown);
        }

        if (candidate.LocationCopy == LogLocationCopyState.Missing)
        {
            return Missing(location, LogReason.StartLocationMissing);
        }

        if (actual.HasCompleteBranch
            && actual.Branch!.FirstRecoveryForkId != actual.Branch.RecoveryForkId)
        {
            return Missing(location, LogReason.StartBranchUnsupported);
        }

        if (actual.IsDamaged == true
            || actual.IsSnapshot == true
            || actual.IsIncomplete == true)
        {
            return Missing(location, LogReason.StartBackupInvalid);
        }

        if (actual.IsDamaged is null
            || actual.IsSnapshot is null
            || actual.IsIncomplete is null)
        {
            return Unknown(location, LogReason.StartEvidenceUnknown);
        }

        if (actual.HasCompleteIdentity
            && actual.HasCompleteBranch
            && actual.Generation is { IsKnown: true }
            && actual.LastLsn is not null
            && actual.Status == LogEvidenceStatus.Complete
            && candidate.LocationCopy == LogLocationCopyState.Available)
        {
            return Missing(location, LogReason.StartNoManagedFull);
        }

        return Unknown(location, LogReason.StartEvidenceUnknown);
    }

    private static LogDataStartDecision Missing(LogStorageLocation location, string reason) =>
        new(LogDataStartAvailability.Missing, reason, location);

    private static LogDataStartDecision Unknown(LogStorageLocation location, string reason) =>
        new(LogDataStartAvailability.Unknown, reason, location);

    private static void RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "枚举值无效。");
        }
    }
}
