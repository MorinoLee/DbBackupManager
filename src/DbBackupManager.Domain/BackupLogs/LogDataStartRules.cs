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
/// 某个具体位置作用域上的一份候选数据备份。FULL 与 DIFF 都按该作用域独立提供。
/// </summary>
public sealed record LogDataStartCandidate
{
    public Guid BackupSetId { get; init; }

    public LogBackupActualMetadata Actual { get; init; } = new();

    public LogLocationScope Scope { get; init; } = LogLocationScope.Local;

    public LogLocationCopyState LocationCopy { get; init; } = LogLocationCopyState.Available;

    public bool IsManaged { get; init; } = true;

    /// <summary>DIFF 是否已由既有依赖规则核对为实际匹配受管普通 FULL。</summary>
    public DifferentialBaselineConclusion? DifferentialDependency { get; init; }

    public Guid? MatchesFullBackupSetId { get; init; }
}

public sealed class LogDataStartRequest
{
    public LogDataStartRequest(
        LogLocationScope scope,
        IEnumerable<LogDataStartCandidate> candidates,
        Guid? preferredFullBackupSetId = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        Scope = scope;
        Candidates = Array.AsReadOnly(candidates.ToArray());
        PreferredFullBackupSetId = preferredFullBackupSetId;
    }

    public LogLocationScope Scope { get; }

    public IReadOnlyList<LogDataStartCandidate> Candidates { get; }

    /// <summary>可选，由调用方按恢复窗口固定一份 FULL；未指定时选择 LastLSN 最大的可用普通 FULL。</summary>
    public Guid? PreferredFullBackupSetId { get; }
}

public sealed class LogDataStartDecision
{
    internal LogDataStartDecision(
        LogDataStartAvailability availability,
        string reasonCode,
        LogLocationScope scope,
        IEnumerable<LogObservationConflict> conflicts,
        Guid? fullBackupSetId = null,
        Guid? differentialBackupSetId = null,
        BackupLsn? restorePointLsn = null,
        BackupDatabaseIdentity? database = null,
        BackupRecoveryBranch? branch = null,
        LogChainGeneration? generation = null)
    {
        Availability = availability;
        ReasonCode = reasonCode;
        Scope = scope;
        Conflicts = Array.AsReadOnly(conflicts.ToArray());
        FullBackupSetId = fullBackupSetId;
        DifferentialBackupSetId = differentialBackupSetId;
        RestorePointLsn = restorePointLsn;
        Database = database;
        Branch = branch;
        Generation = generation;
    }

    public LogDataStartAvailability Availability { get; }

    public string ReasonCode { get; }

    public LogLocationScope Scope { get; }

    public IReadOnlyList<LogObservationConflict> Conflicts { get; }

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
/// 按具体位置作用域选择受管普通 FULL 或实际匹配 DIFF 作为数据起点；不访问 SQL/文件。
/// </summary>
public static class LogDataStartRules
{
    public static LogDataStartDecision Select(LogDataStartRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireScope(request.Scope, nameof(request.Scope));
        foreach (var candidate in request.Candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(candidate.Actual);
            if (candidate.BackupSetId == Guid.Empty)
            {
                throw new ArgumentException("数据备份集标识不能为空。", nameof(request));
            }

            RequireScope(candidate.Scope, nameof(candidate.Scope));
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
            .Where(candidate => candidate.Scope == request.Scope)
            .ToArray();

        if (local.Length == 0)
        {
            return Missing(
                request.Scope,
                request.Candidates.Count == 0
                    ? LogReason.StartNoManagedFull
                    : LogReason.StartLocationMissing,
                []);
        }

        var groups = Reconcile(local, request.Scope);
        var conflicts = groups
            .Where(group => group.Conflict is not null)
            .Select(group => group.Conflict!)
            .ToArray();
        var clean = groups
            .Where(group => group.Conflict is null)
            .Select(group => group.Candidate!)
            .ToArray();

        if (request.PreferredFullBackupSetId is { } preferredFullId)
        {
            var preferred = clean
                .Where(candidate => candidate.BackupSetId == preferredFullId)
                .ToArray();
            if (preferred.Length == 0)
            {
                return groups.Any(group =>
                    group.Conflict is not null && group.BackupSetId == preferredFullId)
                    ? Unknown(request.Scope, LogReason.StartIdentityConflict, conflicts)
                    : Missing(request.Scope, LogReason.StartNoManagedFull, conflicts);
            }

            if (!IsUsableFull(preferred[0]))
            {
                return Unavailable(preferred[0], request.Scope, conflicts);
            }

            return Available(preferred[0], clean, request.Scope, conflicts);
        }

        var usableFulls = clean
            .Where(IsUsableFull)
            .OrderByDescending(candidate => candidate.Actual.LastLsn!.Value.Value)
            .ThenBy(candidate => candidate.BackupSetId)
            .ToArray();

        if (usableFulls.Length > 0)
        {
            return Available(usableFulls[0], clean, request.Scope, conflicts);
        }

        var fullCandidates = clean
            .Where(candidate => candidate.Actual.Type is null or BackupType.Full)
            .ToArray();

        if (fullCandidates.Length == 0)
        {
            return groups.Any(group => group.Conflict is not null && group.PotentialFull)
                ? Unknown(request.Scope, LogReason.StartIdentityConflict, conflicts)
                : Missing(request.Scope, LogReason.StartNoManagedFull, conflicts);
        }

        if (fullCandidates.Any(IsUnknownFullEvidence))
        {
            return Unknown(request.Scope, LogReason.StartEvidenceUnknown, conflicts);
        }

        return Missing(request.Scope, FullUnavailableReason(fullCandidates), conflicts);
    }

    private static List<StartCandidateGroup> Reconcile(
        IReadOnlyList<LogDataStartCandidate> candidates,
        LogLocationScope scope)
    {
        var groups = new List<StartCandidateGroup>();
        foreach (var group in candidates
            .GroupBy(candidate => candidate.BackupSetId)
            .OrderBy(group => group.Key))
        {
            var merged = group.First();
            string? conflict = null;
            foreach (var next in group.Skip(1))
            {
                var observation = LogObservationReconciler.Reconcile(merged.Actual, next.Actual);
                if (observation.ConflictField is { } field)
                {
                    conflict = field;
                    break;
                }

                var candidate = MergeCandidate(merged, next, observation.Merged, out var candidateConflict);
                if (candidateConflict is not null)
                {
                    conflict = candidateConflict;
                    break;
                }

                merged = candidate!;
            }

            groups.Add(new StartCandidateGroup(
                group.Key,
                group.Any(candidate => candidate.Actual.Type is null or BackupType.Full),
                conflict is null
                    ? null
                    : new LogObservationConflict(group.Key, scope, conflict),
                conflict is null ? merged : null));
        }

        return groups;
    }

    private static LogDataStartCandidate? MergeCandidate(
        LogDataStartCandidate left,
        LogDataStartCandidate right,
        LogBackupActualMetadata mergedActual,
        out string? conflict)
    {
        conflict = null;
        if (left.IsManaged != right.IsManaged)
        {
            conflict = LogFactField.IsManaged;
            return null;
        }

        var copy = MergeCopyState(left.LocationCopy, right.LocationCopy);
        if (copy is null)
        {
            conflict = LogFactField.CopyState;
            return null;
        }

        if (left.DifferentialDependency is { } leftDependency
            && right.DifferentialDependency is { } rightDependency
            && leftDependency != rightDependency)
        {
            conflict = LogFactField.DifferentialDependency;
            return null;
        }

        if (left.MatchesFullBackupSetId is { } leftFull
            && right.MatchesFullBackupSetId is { } rightFull
            && leftFull != rightFull)
        {
            conflict = LogFactField.MatchesFull;
            return null;
        }

        return left with
        {
            Actual = mergedActual,
            LocationCopy = copy.Value,
            DifferentialDependency = left.DifferentialDependency ?? right.DifferentialDependency,
            MatchesFullBackupSetId = left.MatchesFullBackupSetId ?? right.MatchesFullBackupSetId,
        };
    }

    private static LogLocationCopyState? MergeCopyState(
        LogLocationCopyState left,
        LogLocationCopyState right)
    {
        if (left == right)
        {
            return left;
        }

        if (left == LogLocationCopyState.Unknown)
        {
            return right;
        }

        if (right == LogLocationCopyState.Unknown)
        {
            return left;
        }

        return null;
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
        LogLocationScope scope,
        IReadOnlyList<LogObservationConflict> conflicts)
    {
        var differential = candidates
            .Where(candidate => IsUsableDifferential(candidate, full))
            .OrderByDescending(candidate => candidate.Actual.LastLsn!.Value.Value)
            .ThenBy(candidate => candidate.BackupSetId)
            .FirstOrDefault();

        return new LogDataStartDecision(
            LogDataStartAvailability.Available,
            differential is null ? LogReason.StartManagedFull : LogReason.StartManagedDifferential,
            scope,
            conflicts,
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
        LogLocationScope scope,
        IReadOnlyList<LogObservationConflict> conflicts)
    {
        var actual = candidate.Actual;
        if (!candidate.IsManaged)
        {
            return Missing(scope, LogReason.StartExternalNotManaged, conflicts);
        }

        if (actual.Type is null)
        {
            return Unknown(scope, LogReason.StartEvidenceUnknown, conflicts);
        }

        if (actual.Type != BackupType.Full)
        {
            return Missing(scope, LogReason.StartNoManagedFull, conflicts);
        }

        if (actual.IsCopyOnly == true)
        {
            return Missing(scope, LogReason.StartCopyOnlyNotEligible, conflicts);
        }

        if (actual.IsCopyOnly is null)
        {
            return Unknown(scope, LogReason.StartEvidenceUnknown, conflicts);
        }

        if (candidate.LocationCopy == LogLocationCopyState.Missing)
        {
            return Missing(scope, LogReason.StartLocationMissing, conflicts);
        }

        if (actual.HasCompleteBranch
            && actual.Branch!.FirstRecoveryForkId != actual.Branch.RecoveryForkId)
        {
            return Missing(scope, LogReason.StartBranchUnsupported, conflicts);
        }

        if (actual.IsDamaged == true
            || actual.IsSnapshot == true
            || actual.IsIncomplete == true)
        {
            return Missing(scope, LogReason.StartBackupInvalid, conflicts);
        }

        if (actual.IsDamaged is null
            || actual.IsSnapshot is null
            || actual.IsIncomplete is null)
        {
            return Unknown(scope, LogReason.StartEvidenceUnknown, conflicts);
        }

        if (actual.HasCompleteIdentity
            && actual.HasCompleteBranch
            && actual.Generation is { IsKnown: true }
            && actual.LastLsn is not null
            && actual.Status == LogEvidenceStatus.Complete
            && candidate.LocationCopy == LogLocationCopyState.Available)
        {
            return Missing(scope, LogReason.StartNoManagedFull, conflicts);
        }

        return Unknown(scope, LogReason.StartEvidenceUnknown, conflicts);
    }

    private static LogDataStartDecision Missing(
        LogLocationScope scope,
        string reason,
        IReadOnlyList<LogObservationConflict> conflicts) =>
        new(LogDataStartAvailability.Missing, reason, scope, conflicts);

    private static LogDataStartDecision Unknown(
        LogLocationScope scope,
        string reason,
        IReadOnlyList<LogObservationConflict> conflicts) =>
        new(LogDataStartAvailability.Unknown, reason, scope, conflicts);

    private static void RequireScope(LogLocationScope scope, string parameterName)
    {
        RequireDefined(scope.Kind, parameterName);
        if (scope.Kind == LogStorageLocation.Remote
            && (scope.StorageTargetId is null || scope.StorageTargetId == Guid.Empty))
        {
            throw new ArgumentException("远端位置必须携带具体存储目标。", parameterName);
        }
    }

    private static void RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "枚举值无效。");
        }
    }

    private sealed record StartCandidateGroup(
        Guid BackupSetId,
        bool PotentialFull,
        LogObservationConflict? Conflict,
        LogDataStartCandidate? Candidate);
}
