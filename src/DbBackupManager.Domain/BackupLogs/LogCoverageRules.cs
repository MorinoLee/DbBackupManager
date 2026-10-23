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

    public LogStorageLocation Location { get; init; } = LogStorageLocation.Local;

    public LogLocationCopyState LocationCopy { get; init; } = LogLocationCopyState.Available;

    public bool IsManaged { get; init; } = true;
}

public sealed class LogCoverageRequest
{
    public LogCoverageRequest(
        LogDataStartDecision start,
        bool enumerationComplete,
        IEnumerable<LogCoverageCandidate> candidates,
        bool expectedUseChecksum,
        bool expectedUseCompression)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(candidates);
        Start = start;
        EnumerationComplete = enumerationComplete;
        Candidates = Array.AsReadOnly(candidates.ToArray());
        ExpectedUseChecksum = expectedUseChecksum;
        ExpectedUseCompression = expectedUseCompression;
    }

    public LogDataStartDecision Start { get; }

    /// <summary>数据分页已读完，只证明枚举完成，不等于候选事实完整。</summary>
    public bool EnumerationComplete { get; }

    public IReadOnlyList<LogCoverageCandidate> Candidates { get; }

    public bool ExpectedUseChecksum { get; }

    public bool ExpectedUseCompression { get; }
}

public sealed class LogCoverageResult
{
    internal LogCoverageResult(
        LogCoverageConclusion conclusion,
        BackupLsn? frontierLsn,
        LogDataStartDecision start,
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

        var classified = ClassifyAll(request);
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
        var missingCopies = bridgeable
            .Where(candidate => candidate.State == CandidateState.MissingCopy)
            .ToArray();
        var missingWithoutEnd = missingCopies
            .Where(candidate => candidate.LastLsn is null)
            .ToArray();
        var missingEnd = missingCopies.Length > 0 && missingWithoutEnd.Length == 0
            ? MissingRunEnd(missingCopies, frontier.Value)
            : (decimal?)null;
        IReadOnlyList<LogCoverageRange> missingGaps = missingEnd is { } unavailableEnd
            ? [new LogCoverageRange(frontier, new BackupLsn(unavailableEnd))]
            : [];
        if (missingGaps.Count > 0)
        {
            reasons.Add(LogReason.CopyMissing);
        }

        var hasUnknownEvidence = unknown.Length > 0
            || missingWithoutEnd.Length > 0
            || !request.EnumerationComplete;

        if (unsupported.Length > 0)
        {
            reasons.Add(LogReason.CoverageBranchUnsupported);
            if (hasUnknownEvidence)
            {
                reasons.Add(LogReason.CoverageUnknown);
                if (!request.EnumerationComplete)
                {
                    reasons.Add(LogReason.QueryIncomplete);
                }

                reasons.AddRange(unknown.Select(candidate => candidate.ReasonCode));
                reasons.AddRange(missingWithoutEnd.Select(candidate => candidate.ReasonCode));
            }

            reasons.AddRange(unsupported.Select(candidate => candidate.ReasonCode));
            return Create(
                LogCoverageConclusion.UnsupportedBranch,
                frontier,
                start,
                used,
                continuous,
                missingGaps,
                hasUnknownEvidence ? [new LogCoverageRange(frontier, null)] : [],
                [new LogCoverageRange(frontier, null)],
                reasons,
                classified);
        }

        if (hasUnknownEvidence)
        {
            reasons.Add(LogReason.CoverageUnknown);
            if (!request.EnumerationComplete)
            {
                reasons.Add(LogReason.QueryIncomplete);
            }

            reasons.AddRange(unknown.Select(candidate => candidate.ReasonCode));
            reasons.AddRange(missingWithoutEnd.Select(candidate => candidate.ReasonCode));
            return Create(
                LogCoverageConclusion.Unknown,
                frontier,
                start,
                used,
                continuous,
                missingGaps,
                [new LogCoverageRange(frontier, null)],
                [],
                reasons,
                classified);
        }

        var next = NextKnownFirst(classified, frontier);
        if (missingEnd is { } gapEnd)
        {
            reasons.Add(LogReason.CoverageGap);
            reasons.Add(LogReason.CoverageUnknown);
            return Create(
                LogCoverageConclusion.Gap,
                frontier,
                start,
                used,
                continuous,
                [new LogCoverageRange(frontier, new BackupLsn(gapEnd))],
                [new LogCoverageRange(new BackupLsn(gapEnd), null)],
                [],
                reasons,
                classified);
        }

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
            used,
            continuous,
            [new LogCoverageRange(frontier, null)],
            [],
            [],
            reasons,
            classified);
    }

    private static List<ClassifiedCandidate> ClassifyAll(LogCoverageRequest request)
    {
        var result = new List<ClassifiedCandidate>();
        foreach (var item in request.Candidates)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(item.Actual);
            if (item.BackupSetId == Guid.Empty)
            {
                throw new ArgumentException("日志备份集标识不能为空。", nameof(request));
            }

            RequireDefined(item.Location, nameof(item.Location));
            RequireDefined(item.LocationCopy, nameof(item.LocationCopy));
            RequireDefined(item.Actual.Status, nameof(item.Actual.Status));
            if (item.Actual.ObservedAtUtc is { } observedAtUtc
                && observedAtUtc.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException("观察时间必须使用 UTC。", nameof(request));
            }
        }

        foreach (var group in request.Candidates
            .Where(candidate => candidate.Location == request.Start.Location)
            .GroupBy(candidate => candidate.BackupSetId))
        {
            var items = group.ToArray();
            if (items.Length == 1)
            {
                result.Add(Classify(items[0], request));
                continue;
            }

            var first = items[0];
            if (items.Skip(1).Any(other => Conflicts(first, other)))
            {
                result.Add(new ClassifiedCandidate(
                    first.BackupSetId,
                    CandidateState.Unresolved,
                    null,
                    null,
                    LogReason.DuplicateIdentityConflict));
                continue;
            }

            var kept = items
                .OrderByDescending(candidate =>
                    candidate.Actual.Status == LogEvidenceStatus.Complete
                    && candidate.LocationCopy == LogLocationCopyState.Available)
                .First();
            result.Add(Classify(kept, request));
        }

        return result;
    }

    private static ClassifiedCandidate Classify(LogCoverageCandidate candidate, LogCoverageRequest request)
    {
        var actual = candidate.Actual;
        var first = actual.FirstLsn;
        var last = actual.LastLsn;

        ClassifiedCandidate State(CandidateState state, string reason) =>
            new(candidate.BackupSetId, state, first, last, reason);

        if (candidate.Location != request.Start.Location)
        {
            return State(CandidateState.Unresolved, LogReason.LocationMismatch);
        }

        if (!candidate.IsManaged)
        {
            return State(CandidateState.Unresolved, LogReason.ExternalNotManaged);
        }

        switch (actual.Status)
        {
            case LogEvidenceStatus.SourceConflict:
                return State(CandidateState.Unresolved, LogReason.ActualSourceConflict);
            case LogEvidenceStatus.PermissionDenied:
            case LogEvidenceStatus.HistoryNotFound:
            case LogEvidenceStatus.MissingFields:
                return State(CandidateState.Unresolved, LogReason.ActualEvidenceUnknown);
        }

        if (first is null || last is null)
        {
            return State(CandidateState.Unresolved, LogReason.IntervalMissingFields);
        }

        if (first.Value.Value >= last.Value.Value)
        {
            return State(CandidateState.Invalid, LogReason.IntervalInvalid);
        }

        switch (candidate.LocationCopy)
        {
            case LogLocationCopyState.Unknown:
                return State(CandidateState.Unresolved, LogReason.CopyUnknown);
            case LogLocationCopyState.Missing:
                return State(CandidateState.MissingCopy, LogReason.CopyMissing);
        }

        if (actual.Type is null)
        {
            return State(CandidateState.Unresolved, LogReason.ActualTypeUnknown);
        }

        if (actual.Type != BackupType.Log)
        {
            return State(CandidateState.Invalid, LogReason.ActualTypeNotLog);
        }

        if (actual.IsCopyOnly is null)
        {
            return State(CandidateState.Unresolved, LogReason.ActualCopyOnlyUnknown);
        }

        if (actual.IsCopyOnly.Value)
        {
            return State(CandidateState.Invalid, LogReason.ActualCopyOnly);
        }

        if (actual.RecoveryModel is null or DatabaseRecoveryModel.Unknown)
        {
            return State(CandidateState.Unresolved, LogReason.ActualRecoveryModelUnknown);
        }

        if (actual.RecoveryModel != DatabaseRecoveryModel.Full)
        {
            return State(CandidateState.Invalid, LogReason.ActualRecoveryModelNotFull);
        }

        if (actual.HasBulkLoggedData is null)
        {
            return State(CandidateState.Unresolved, LogReason.ActualBulkUnknown);
        }

        if (actual.HasBulkLoggedData.Value)
        {
            return State(CandidateState.Invalid, LogReason.ActualBulkLogged);
        }

        if (actual.IsForceOffline is null)
        {
            return State(CandidateState.Unresolved, LogReason.ActualForceOfflineUnknown);
        }

        if (actual.IsForceOffline.Value)
        {
            return State(CandidateState.Invalid, LogReason.ActualForceOffline);
        }

        if (actual.IsDamaged is null || actual.IsSnapshot is null || actual.IsIncomplete is null)
        {
            return State(CandidateState.Unresolved, LogReason.ActualMetadataUnknown);
        }

        if (actual.IsDamaged.Value || actual.IsSnapshot.Value || actual.IsIncomplete.Value)
        {
            return State(CandidateState.Invalid, LogReason.ActualMetadataInvalid);
        }

        if (actual.UseChecksum is null || actual.UseCompression is null)
        {
            return State(CandidateState.Unresolved, LogReason.ActualProtectionUnknown);
        }

        if (actual.UseChecksum.Value != request.ExpectedUseChecksum
            || actual.UseCompression.Value != request.ExpectedUseCompression)
        {
            return State(CandidateState.Invalid, LogReason.ActualProtectionMismatch);
        }

        if (!actual.HasCompleteIdentity)
        {
            return State(CandidateState.Unresolved, LogReason.ActualIdentityUnknown);
        }

        if (actual.Database != request.Start.Database)
        {
            return State(CandidateState.Unsupported, LogReason.ActualIdentityUnsupported);
        }

        if (!actual.HasCompleteBranch)
        {
            return State(CandidateState.Unresolved, LogReason.ActualBranchUnknown);
        }

        if (actual.Branch!.FirstRecoveryForkId != actual.Branch.RecoveryForkId)
        {
            return State(CandidateState.Unsupported, LogReason.ActualBranchUnsupported);
        }

        if (actual.Branch != request.Start.Branch)
        {
            return State(CandidateState.Unsupported, LogReason.ActualBranchUnsupported);
        }

        if (actual.Generation is not { IsKnown: true })
        {
            return State(CandidateState.Unresolved, LogReason.ActualGenerationUnknown);
        }

        if (request.Start.Generation is not { IsKnown: true }
            || actual.Generation.GenerationId != request.Start.Generation.GenerationId)
        {
            return State(CandidateState.Unsupported, LogReason.ActualGenerationUnsupported);
        }

        return State(CandidateState.Valid, LogReason.CoverageContinuous);
    }

    private static bool Conflicts(LogCoverageCandidate left, LogCoverageCandidate right)
    {
        var a = left.Actual;
        var b = right.Actual;
        return left.Location != right.Location
            || left.IsManaged != right.IsManaged
            || (a.Status == LogEvidenceStatus.SourceConflict) != (b.Status == LogEvidenceStatus.SourceConflict)
            || a.FirstLsn != b.FirstLsn
            || a.LastLsn != b.LastLsn
            || a.Type != b.Type
            || a.IsCopyOnly != b.IsCopyOnly
            || a.RecoveryModel != b.RecoveryModel
            || a.HasBulkLoggedData != b.HasBulkLoggedData
            || a.IsForceOffline != b.IsForceOffline
            || a.UseChecksum != b.UseChecksum
            || a.UseCompression != b.UseCompression
            || a.IsDamaged != b.IsDamaged
            || a.IsSnapshot != b.IsSnapshot
            || a.IsIncomplete != b.IsIncomplete
            || a.Database != b.Database
            || a.Branch != b.Branch
            || a.Generation?.GenerationId != b.Generation?.GenerationId;
    }

    private static bool CanBridge(ClassifiedCandidate candidate, decimal frontier) =>
        (candidate.FirstLsn is null || candidate.FirstLsn.Value.Value <= frontier)
        && (candidate.LastLsn is null || candidate.LastLsn.Value.Value > frontier);

    private static ClassifiedCandidate? NextKnownFirst(
        IReadOnlyList<ClassifiedCandidate> classified,
        BackupLsn frontier)
    {
        return classified
            .Where(candidate => candidate.FirstLsn is not null
                && candidate.FirstLsn.Value.Value > frontier.Value)
            .OrderBy(candidate => candidate.FirstLsn!.Value.Value)
            .ThenBy(candidate => candidate.BackupSetId)
            .FirstOrDefault();
    }

    private static decimal MissingRunEnd(
        IReadOnlyList<ClassifiedCandidate> missingCopies,
        decimal frontier)
    {
        var end = frontier;
        var extended = true;
        while (extended)
        {
            extended = false;
            foreach (var candidate in missingCopies)
            {
                if (candidate.FirstLsn is { } first
                    && candidate.LastLsn is { } last
                    && first.Value <= end
                    && last.Value > end)
                {
                    end = last.Value;
                    extended = true;
                }
            }
        }

        return end;
    }

    private static LogCoverageResult Create(
        LogCoverageConclusion conclusion,
        BackupLsn? frontier,
        LogDataStartDecision start,
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
            used,
            continuous,
            gaps,
            unknown,
            unsupported,
            reasons,
            classified
                .Where(candidate => candidate.State != CandidateState.Valid)
                .Select(candidate => candidate.BackupSetId));
    }

    private static LogCoverageResult Unavailable(LogDataStartDecision start, bool enumerationComplete)
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
            [],
            [],
            [],
            [],
            [],
            reasons,
            []);
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
        MissingCopy = 2,
        Unresolved = 3,
        Unsupported = 4,
        Invalid = 5
    }

    private sealed record ClassifiedCandidate(
        Guid BackupSetId,
        CandidateState State,
        BackupLsn? FirstLsn,
        BackupLsn? LastLsn,
        string ReasonCode);
}
