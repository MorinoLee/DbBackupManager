using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.BackupLogs;

public enum LogCoverageConclusion
{
    Continuous = 1,
    Gap = 2,
    Unknown = 3,
    UnsupportedBranch = 4
}

/// <summary>右开区间；ToLsn 为空表示边界未证明。</summary>
public sealed record LogCoverageRange(BackupLsn? FromLsn, BackupLsn? ToLsn);

public sealed record LogCoverageCandidate
{
    public Guid BackupSetId { get; init; }

    public LogBackupActualMetadata Actual { get; init; } = new();

    public LogLocationScope Scope { get; init; } = LogLocationScope.Local;

    public LogLocationCopyState LocationCopy { get; init; } = LogLocationCopyState.Available;

    public bool IsManaged { get; init; } = true;

    /// <summary>该候选自己的不可变执行快照选项；不能用当前版本选项重验历史候选。</summary>
    public LogProtectionExpectation? ProtectionExpectation { get; init; }
}

public sealed class LogCoverageRequest
{
    public LogCoverageRequest(
        LogDataStartDecision start,
        bool enumerationComplete,
        IEnumerable<LogCoverageCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(candidates);
        Start = start;
        EnumerationComplete = enumerationComplete;
        Candidates = Array.AsReadOnly(candidates.ToArray());
    }

    public LogDataStartDecision Start { get; }

    /// <summary>数据分页已读完，只证明枚举完成，不等于候选事实完整。</summary>
    public bool EnumerationComplete { get; }

    public IReadOnlyList<LogCoverageCandidate> Candidates { get; }
}

public sealed class LogCoverageResult
{
    internal LogCoverageResult(
        LogCoverageConclusion conclusion,
        BackupLsn? frontierLsn,
        LogDataStartDecision start,
        IEnumerable<LogObservationConflict> conflicts,
        IEnumerable<Guid> usedBackupSetIds,
        IEnumerable<LogCoverageRange> knownContinuousRanges,
        IEnumerable<LogCoverageRange> knownGaps,
        IEnumerable<LogCoverageRange> unknownRanges,
        IEnumerable<LogCoverageRange> unsupportedRanges,
        IEnumerable<string> reasonCodes,
        IEnumerable<Guid> rejectedBackupSetIds)
    {
        Conclusion = conclusion;
        FrontierLsn = frontierLsn;
        Start = start;
        Conflicts = Array.AsReadOnly(
            conflicts.Concat(start.Conflicts).Distinct().ToArray());
        UsedBackupSetIds = Array.AsReadOnly(usedBackupSetIds.ToArray());
        KnownContinuousRanges = Array.AsReadOnly(knownContinuousRanges.ToArray());
        KnownGaps = Array.AsReadOnly(knownGaps.ToArray());
        UnknownRanges = Array.AsReadOnly(unknownRanges.ToArray());
        UnsupportedRanges = Array.AsReadOnly(unsupportedRanges.ToArray());
        ReasonCodes = Array.AsReadOnly(reasonCodes.Distinct(StringComparer.Ordinal).ToArray());
        RejectedBackupSetIds = Array.AsReadOnly(
            rejectedBackupSetIds.Distinct().OrderBy(id => id).ToArray());
    }

    public LogCoverageConclusion Conclusion { get; }

    public BackupLsn? FrontierLsn { get; }

    public LogDataStartDecision Start { get; }

    public LogLocationScope Scope => Start.Scope;

    public IReadOnlyList<LogObservationConflict> Conflicts { get; }

    public IReadOnlyList<Guid> UsedBackupSetIds { get; }

    public IReadOnlyList<LogCoverageRange> KnownContinuousRanges { get; }

    public IReadOnlyList<LogCoverageRange> KnownGaps { get; }

    public IReadOnlyList<LogCoverageRange> UnknownRanges { get; }

    public IReadOnlyList<LogCoverageRange> UnsupportedRanges { get; }

    public IReadOnlyList<string> ReasonCodes { get; }

    public IReadOnlyList<Guid> RejectedBackupSetIds { get; }
}

/// <summary>
/// 纯 LOG 覆盖规则：只比较已提供的区间/身份/分支/实际属性，不访问 SQL/文件，
/// 不写任务状态、不消耗 Permit、也不构成调用授权。
/// </summary>
public static class LogCoverageRules
{
    public static LogCoverageResult Evaluate(LogCoverageRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var start = request.Start;
        if (start.Availability != LogDataStartAvailability.Available
            || start.RestorePointLsn is null
            || start.Database is null
            || start.Branch is null
            || start.Generation is not { IsKnown: true })
        {
            return Unavailable(start, request.EnumerationComplete);
        }

        var (classified, conflicts) = ClassifyAll(request);
        var frontier = start.RestorePointLsn.Value;
        var used = new List<Guid>();
        var usedSet = new HashSet<Guid>();

        while (true)
        {
            var bridging = classified
                .Where(candidate => candidate.State == CandidateState.Valid
                    && !usedSet.Contains(candidate.BackupSetId)
                    && CanBridge(candidate, frontier.Value))
                .OrderByDescending(candidate => candidate.LastLsn!.Value.Value)
                .ThenBy(candidate => candidate.BackupSetId)
                .FirstOrDefault();

            if (bridging is null)
            {
                break;
            }

            used.Add(bridging.BackupSetId);
            usedSet.Add(bridging.BackupSetId);
            frontier = bridging.LastLsn!.Value;
        }

        var reasons = new List<string>();
        IReadOnlyList<LogCoverageRange> continuous = used.Count == 0
            ? []
            : [new LogCoverageRange(start.RestorePointLsn, frontier)];

        var bridgeable = classified
            .Where(candidate => candidate.State != CandidateState.Valid
                && CanBridge(candidate, frontier.Value))
            .ToArray();

        var unsupported = bridgeable
            .Where(candidate => candidate.State == CandidateState.Unsupported)
            .ToArray();
        var unknown = bridgeable
            .Where(candidate => candidate.State is CandidateState.Unresolved or CandidateState.Invalid)
            .ToArray();
        var hasUnknownEvidence = unknown.Length > 0
            || conflicts.Count > 0
            || !request.EnumerationComplete;

        if (unsupported.Length > 0)
        {
            reasons.Add(LogReason.CoverageBranchUnsupported);
            if (hasUnknownEvidence)
            {
                reasons.Add(LogReason.CoverageUnknown);
                if (conflicts.Count > 0)
                {
                    reasons.Add(LogReason.DuplicateIdentityConflict);
                }

                if (!request.EnumerationComplete)
                {
                    reasons.Add(LogReason.QueryIncomplete);
                }

                reasons.AddRange(unknown.Select(candidate => candidate.ReasonCode));
            }

            reasons.AddRange(unsupported.Select(candidate => candidate.ReasonCode));
            return Create(
                LogCoverageConclusion.UnsupportedBranch,
                frontier,
                start,
                conflicts,
                used,
                continuous,
                [],
                hasUnknownEvidence ? [new LogCoverageRange(frontier, null)] : [],
                [new LogCoverageRange(frontier, null)],
                reasons,
                classified);
        }

        if (hasUnknownEvidence)
        {
            reasons.Add(LogReason.CoverageUnknown);
            if (conflicts.Count > 0)
            {
                reasons.Add(LogReason.DuplicateIdentityConflict);
            }

            if (!request.EnumerationComplete)
            {
                reasons.Add(LogReason.QueryIncomplete);
            }

            reasons.AddRange(unknown.Select(candidate => candidate.ReasonCode));
            return Create(
                LogCoverageConclusion.Unknown,
                frontier,
                start,
                conflicts,
                used,
                continuous,
                [],
                [new LogCoverageRange(frontier, null)],
                [],
                reasons,
                classified);
        }

        var next = NextKnownFirst(classified, frontier);
        if (next is { } nextCandidate)
        {
            var following = nextCandidate.FirstLsn!.Value.Value;
            reasons.Add(LogReason.CoverageGap);
            if (nextCandidate.State == CandidateState.Unsupported)
            {
                reasons.Add(LogReason.CoverageBranchUnsupported);
                reasons.Add(nextCandidate.ReasonCode);
                return Create(
                    LogCoverageConclusion.Gap,
                    frontier,
                    start,
                    conflicts,
                    used,
                    continuous,
                    [new LogCoverageRange(frontier, new BackupLsn(following))],
                    [],
                    [new LogCoverageRange(new BackupLsn(following), null)],
                    reasons,
                    classified);
            }

            if (nextCandidate.State != CandidateState.Valid)
            {
                reasons.Add(LogReason.CoverageUnknown);
                reasons.Add(nextCandidate.ReasonCode);
                return Create(
                    LogCoverageConclusion.Gap,
                    frontier,
                    start,
                    conflicts,
                    used,
                    continuous,
                    [new LogCoverageRange(frontier, new BackupLsn(following))],
                    [new LogCoverageRange(new BackupLsn(following), null)],
                    [],
                    reasons,
                    classified);
            }

            return Create(
                LogCoverageConclusion.Gap,
                frontier,
                start,
                conflicts,
                used,
                continuous,
                [new LogCoverageRange(frontier, new BackupLsn(following))],
                [],
                [],
                reasons,
                classified);
        }

        if (used.Count > 0)
        {
            reasons.Add(LogReason.CoverageContinuous);
            return Create(
                LogCoverageConclusion.Continuous,
                frontier,
                start,
                conflicts,
                used,
                continuous,
                [],
                [],
                [],
                reasons,
                classified);
        }

        reasons.Add(LogReason.CoverageGap);
        return Create(
            LogCoverageConclusion.Gap,
            frontier,
            start,
            conflicts,
            used,
            continuous,
            [new LogCoverageRange(frontier, null)],
            [],
            [],
            reasons,
            classified);
    }

    private static (IReadOnlyList<ClassifiedCandidate> Classified, IReadOnlyList<LogObservationConflict> Conflicts) ClassifyAll(
        LogCoverageRequest request)
    {
        var result = new List<ClassifiedCandidate>();
        var conflicts = new List<LogObservationConflict>();
        foreach (var item in request.Candidates)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(item.Actual);
            if (item.BackupSetId == Guid.Empty)
            {
                throw new ArgumentException("日志备份集标识不能为空。", nameof(request));
            }

            RequireScope(item.Scope, nameof(item.Scope));
            RequireDefined(item.LocationCopy, nameof(item.LocationCopy));
            RequireDefined(item.Actual.Status, nameof(item.Actual.Status));
            if (item.Actual.ObservedAtUtc is { } observedAtUtc
                && observedAtUtc.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException("观察时间必须使用 UTC。", nameof(request));
            }
        }

        foreach (var group in request.Candidates
            .Where(candidate => candidate.Scope == request.Start.Scope)
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

            if (conflict is not null)
            {
                conflicts.Add(new LogObservationConflict(group.Key, request.Start.Scope, conflict));
                continue;
            }

            result.Add(Classify(merged, request));
        }

        return (result, conflicts);
    }

    private static LogCoverageCandidate? MergeCandidate(
        LogCoverageCandidate left,
        LogCoverageCandidate right,
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

        if (left.ProtectionExpectation is { } leftExpectation
            && right.ProtectionExpectation is { } rightExpectation
            && leftExpectation != rightExpectation)
        {
            conflict = LogFactField.ProtectionExpectation;
            return null;
        }

        return left with
        {
            Actual = mergedActual,
            LocationCopy = copy.Value,
            ProtectionExpectation = left.ProtectionExpectation ?? right.ProtectionExpectation,
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

    private static ClassifiedCandidate Classify(
        LogCoverageCandidate candidate,
        LogCoverageRequest request)
    {
        var actual = candidate.Actual;
        var first = actual.FirstLsn;
        var last = actual.LastLsn;

        ClassifiedCandidate State(CandidateState state, string reason, bool boundariesTrusted) =>
            new(candidate.BackupSetId, state, first, last, reason, boundariesTrusted);

        if (candidate.Scope != request.Start.Scope)
        {
            return State(CandidateState.Unresolved, LogReason.LocationMismatch, boundariesTrusted: false);
        }

        if (!candidate.IsManaged)
        {
            return State(CandidateState.Unresolved, LogReason.ExternalNotManaged, boundariesTrusted: false);
        }

        switch (actual.Status)
        {
            case LogEvidenceStatus.SourceConflict:
                return State(CandidateState.Unresolved, LogReason.ActualSourceConflict, boundariesTrusted: false);
            case LogEvidenceStatus.PermissionDenied:
            case LogEvidenceStatus.HistoryNotFound:
            case LogEvidenceStatus.MissingFields:
                return State(CandidateState.Unresolved, LogReason.ActualEvidenceUnknown, boundariesTrusted: false);
        }

        if (first is null || last is null)
        {
            return State(CandidateState.Unresolved, LogReason.IntervalMissingFields, boundariesTrusted: false);
        }

        if (first.Value.Value >= last.Value.Value)
        {
            return State(CandidateState.Invalid, LogReason.IntervalInvalid, boundariesTrusted: false);
        }

        if (actual.Type is null)
        {
            return State(CandidateState.Unresolved, LogReason.ActualTypeUnknown, boundariesTrusted: false);
        }

        if (actual.Type != BackupType.Log)
        {
            return State(CandidateState.Invalid, LogReason.ActualTypeNotLog, boundariesTrusted: true);
        }

        if (actual.IsCopyOnly is null)
        {
            return State(CandidateState.Unresolved, LogReason.ActualCopyOnlyUnknown, boundariesTrusted: false);
        }

        if (actual.IsCopyOnly.Value)
        {
            return State(CandidateState.Invalid, LogReason.ActualCopyOnly, boundariesTrusted: true);
        }

        if (actual.RecoveryModel is null or DatabaseRecoveryModel.Unknown)
        {
            return State(
                CandidateState.Unresolved,
                LogReason.ActualRecoveryModelUnknown,
                boundariesTrusted: false);
        }

        if (actual.RecoveryModel != DatabaseRecoveryModel.Full)
        {
            return State(CandidateState.Invalid, LogReason.ActualRecoveryModelNotFull, boundariesTrusted: true);
        }

        if (actual.HasBulkLoggedData is null)
        {
            return State(CandidateState.Unresolved, LogReason.ActualBulkUnknown, boundariesTrusted: false);
        }

        if (actual.HasBulkLoggedData.Value)
        {
            return State(CandidateState.Invalid, LogReason.ActualBulkLogged, boundariesTrusted: true);
        }

        if (actual.IsForceOffline is null)
        {
            return State(
                CandidateState.Unresolved,
                LogReason.ActualForceOfflineUnknown,
                boundariesTrusted: false);
        }

        if (actual.IsForceOffline.Value)
        {
            return State(CandidateState.Invalid, LogReason.ActualForceOffline, boundariesTrusted: true);
        }

        if (actual.IsDamaged is null || actual.IsSnapshot is null || actual.IsIncomplete is null)
        {
            return State(CandidateState.Unresolved, LogReason.ActualMetadataUnknown, boundariesTrusted: false);
        }

        if (actual.IsDamaged.Value || actual.IsSnapshot.Value || actual.IsIncomplete.Value)
        {
            return State(CandidateState.Invalid, LogReason.ActualMetadataInvalid, boundariesTrusted: true);
        }

        if (candidate.ProtectionExpectation is not { } expectation
            || actual.UseChecksum is null
            || actual.UseCompression is null)
        {
            return State(CandidateState.Unresolved, LogReason.ActualProtectionUnknown, boundariesTrusted: false);
        }

        if (actual.UseChecksum.Value != expectation.UseChecksum
            || actual.UseCompression.Value != expectation.UseCompression)
        {
            return State(CandidateState.Invalid, LogReason.ActualProtectionMismatch, boundariesTrusted: true);
        }

        if (!actual.HasCompleteIdentity)
        {
            return State(CandidateState.Unresolved, LogReason.ActualIdentityUnknown, boundariesTrusted: false);
        }

        if (actual.Database != request.Start.Database)
        {
            return State(CandidateState.Unsupported, LogReason.ActualIdentityUnsupported, boundariesTrusted: true);
        }

        if (!actual.HasCompleteBranch)
        {
            return State(CandidateState.Unresolved, LogReason.ActualBranchUnknown, boundariesTrusted: false);
        }

        if (actual.Branch!.FirstRecoveryForkId != actual.Branch.RecoveryForkId)
        {
            return State(CandidateState.Unsupported, LogReason.ActualBranchUnsupported, boundariesTrusted: true);
        }

        if (actual.Branch != request.Start.Branch)
        {
            return State(CandidateState.Unsupported, LogReason.ActualBranchUnsupported, boundariesTrusted: true);
        }

        if (actual.Generation is not { IsKnown: true })
        {
            return State(CandidateState.Unresolved, LogReason.ActualGenerationUnknown, boundariesTrusted: false);
        }

        if (request.Start.Generation is not { IsKnown: true }
            || actual.Generation.GenerationId != request.Start.Generation.GenerationId)
        {
            return State(CandidateState.Unsupported, LogReason.ActualGenerationUnsupported, boundariesTrusted: true);
        }

        return candidate.LocationCopy switch
        {
            LogLocationCopyState.Unknown => State(
                CandidateState.Unresolved,
                LogReason.CopyUnknown,
                boundariesTrusted: true),
            LogLocationCopyState.Missing => State(
                CandidateState.Unresolved,
                LogReason.CopyMissing,
                boundariesTrusted: true),
            _ => State(CandidateState.Valid, LogReason.CoverageContinuous, boundariesTrusted: true),
        };
    }

    private static bool CanBridge(ClassifiedCandidate candidate, decimal frontier)
    {
        if (!candidate.BoundariesTrusted)
        {
            return true;
        }

        return (candidate.FirstLsn is null || candidate.FirstLsn.Value.Value <= frontier)
            && (candidate.LastLsn is null || candidate.LastLsn.Value.Value > frontier);
    }

    private static ClassifiedCandidate? NextKnownFirst(
        IReadOnlyList<ClassifiedCandidate> classified,
        BackupLsn frontier)
    {
        return classified
            .Where(candidate => candidate.BoundariesTrusted
                && candidate.FirstLsn is not null
                && candidate.FirstLsn.Value.Value > frontier.Value)
            .OrderBy(candidate => candidate.FirstLsn!.Value.Value)
            .ThenBy(candidate => candidate.BackupSetId)
            .FirstOrDefault();
    }

    private static LogCoverageResult Create(
        LogCoverageConclusion conclusion,
        BackupLsn? frontier,
        LogDataStartDecision start,
        IReadOnlyList<LogObservationConflict> conflicts,
        IEnumerable<Guid> used,
        IEnumerable<LogCoverageRange> continuous,
        IEnumerable<LogCoverageRange> gaps,
        IEnumerable<LogCoverageRange> unknown,
        IEnumerable<LogCoverageRange> unsupported,
        IEnumerable<string> reasons,
        IReadOnlyList<ClassifiedCandidate> classified)
    {
        return new LogCoverageResult(
            conclusion,
            frontier,
            start,
            conflicts,
            used,
            continuous,
            gaps,
            unknown,
            unsupported,
            reasons,
            classified
                .Where(candidate => candidate.State != CandidateState.Valid)
                .Select(candidate => candidate.BackupSetId)
                .Concat(conflicts.Select(conflict => conflict.BackupSetId)));
    }

    private static LogCoverageResult Unavailable(
        LogDataStartDecision start,
        bool enumerationComplete)
    {
        var reasons = new List<string> { LogReason.CoverageUnknown, LogReason.StartNotAvailable };
        if (!enumerationComplete)
        {
            reasons.Add(LogReason.QueryIncomplete);
        }

        return new LogCoverageResult(
            LogCoverageConclusion.Unknown,
            null,
            start,
            start.Conflicts,
            [],
            [],
            [],
            [],
            [],
            reasons,
            []);
    }

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

    private enum CandidateState
    {
        Valid = 1,
        Unresolved = 2,
        Unsupported = 3,
        Invalid = 4
    }

    private sealed record ClassifiedCandidate(
        Guid BackupSetId,
        CandidateState State,
        BackupLsn? FirstLsn,
        BackupLsn? LastLsn,
        string ReasonCode,
        bool BoundariesTrusted);
}
