using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupLogs;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.BackupLogs;

public sealed record LogCandidateSnapshot(
    bool EnumerationComplete,
    IReadOnlyList<LogAssessmentCandidate> Candidates,
    IReadOnlyList<string> Issues);

public interface ILogAssessmentCandidateReader
{
    Task<LogCandidateSnapshot> ReadAsync(LogAssessmentRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 只读组合：调用 13-A 纯规则并生成固定 Prepared；不创建任务、不写许可、不调用文件/BACKUP。
/// </summary>
public sealed class LogAssessmentCompositionService(
    ITargetSqlLogReadinessReader readinessReader,
    ILogAssessmentCommitStore commitStore,
    ILogAssessmentCandidateReader? candidateReader = null,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<PreparedLogAssessment> PrepareAsync(LogAssessmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = LogAssessmentInput.From(request);
        var requestFingerprint = LogAssessmentCanonicalizer.RequestFingerprint(input);
        var existing = await commitStore.ReadAsync(request.DatabaseId, request.MutationId, cancellationToken);
        if (existing.Code == LogAssessmentReadCode.Found)
        {
            if (existing.Value!.RequestFingerprint == requestFingerprint) return existing.Value;
            throw new LogAssessmentConflictException("同一 DatabaseId/MutationId 的 LOG 请求绑定已存在且不同。");
        }
        if (existing.Code == LogAssessmentReadCode.Conflict)
            throw new LogAssessmentConflictException("LOG 账本回执无法安全重构。");

        var candidates = request.Candidates.Count > 0
            ? new LogCandidateSnapshot(request.EnumerationComplete, request.Candidates, [])
            : candidateReader is null
                ? new LogCandidateSnapshot(request.EnumerationComplete, [], [])
                : await candidateReader.ReadAsync(request, cancellationToken);
        var readiness = await readinessReader.ReadAsync(request.Connection,
            new TargetSqlLogReadinessRequest(request.DatabaseId, request.DatabaseName,
                request.ExpectedDatabase, request.TimeoutSeconds), cancellationToken);
        return Compose(request, input, requestFingerprint, readiness, candidates);
    }

    public async Task<LogAssessmentReadCommit> ReadCommitAsync(Guid databaseId, Guid mutationId,
        CancellationToken cancellationToken = default)
    {
        var result = await commitStore.ReadAsync(databaseId, mutationId, cancellationToken);
        return result.Code == LogAssessmentReadCode.NotFound
            ? new(LogAssessmentReadCode.PreparedPayloadRequired, null, null)
            : new(result.Code, result.Value?.PayloadFingerprint, result.Value);
    }

    public Task<LogAssessmentCommitResult> CommitAsync(PreparedLogAssessment prepared,
        CancellationToken cancellationToken = default) =>
        commitStore.CommitAsync(prepared, cancellationToken);

    private PreparedLogAssessment Compose(LogAssessmentRequest request, LogAssessmentInput input,
        string requestFingerprint, TargetSqlLogReadinessEvidence readiness, LogCandidateSnapshot snapshot)
    {
        var candidates = snapshot.Candidates.Where(c => c.Scope == request.Scope)
            .Where(c => !c.IsManaged || c.Actual.Database is null
                || c.Actual.Database.DatabaseGuid is not { } databaseGuid
                || c.Actual.Database.FamilyGuid is not { } familyGuid
                || databaseGuid == request.ExpectedDatabase.DatabaseGuid && familyGuid == request.ExpectedDatabase.FamilyGuid)
            .OrderBy(c => c.BackupSetId).ThenBy(c => c.SourceRevision)
            .ThenBy(c => c.Scope.Kind).ThenBy(c => c.Scope.StorageTargetId)
            .ThenBy(c => c.Actual.Type).ThenBy(c => c.Actual.FirstLsn?.Value)
            .ThenBy(c => c.Actual.LastLsn?.Value)
            .ThenBy(LogAssessmentCanonicalizer.CandidateSortKey, StringComparer.Ordinal).ToArray();
        var issues = new List<LogAssessmentIssueDraft>();
        if (snapshot.Candidates.Any(c => c.Scope != request.Scope))
            issues.Add(new("log.candidate_scope_mismatch"));
        if (snapshot.Candidates.Any(c => c.IsManaged && c.Actual.Database is { DatabaseGuid: { } d, FamilyGuid: { } f }
                && (d != request.ExpectedDatabase.DatabaseGuid || f != request.ExpectedDatabase.FamilyGuid)))
            issues.Add(new("log.candidate_identity_mismatch"));
        issues.AddRange(snapshot.Issues.Select(code => new LogAssessmentIssueDraft(code)));

        var technicalStatus = MapStatus(readiness);
        var recovery = readiness.Recovery;
        var observationAt = recovery.WasAttempted ? recovery.ObservedAtUtc : readiness.Database.ObservedAtUtc;
        var facts = new LogTechnicalReadinessFacts
        {
            DatabaseId = request.DatabaseId,
            IsOnline = readiness.Database.State switch
            {
                TargetSqlDatabaseState.Online => true,
                null => null,
                _ => false,
            },
            RecoveryModel = MapRecoveryModel(readiness.Database.RecoveryModel),
            ManagedDatabase = request.ExpectedDatabase,
            SourceDatabase = recovery.Database,
            CurrentBranch = recovery.WasAttempted
                ? new BackupRecoveryBranch(recovery.DiagnosticFirstRecoveryForkId, recovery.CurrentRecoveryForkId)
                : null,
            LastLogBackupLsn = recovery.LastLogBackupLsnState == TargetSqlLogLsnState.Known
                ? recovery.LastLogBackupLsn
                : null,
            Capability = request.Capability,
            ResourceAvailability = request.ResourceAvailability,
            ObservationStatus = technicalStatus,
            Source = LogObservationSource.RecoveryStatus,
            ObservedAtUtc = observationAt,
            HasHistoricalManagedFull = candidates.Any(c => c.IsManaged && c.Actual.Type == BackupType.Full
                && c.Actual.IsCopyOnly == false),
        };
        var technical = LogTechnicalReadinessRules.Evaluate(facts);
        var resolved = ResolveGenerations(request.DatabaseId, candidates);
        var candidatesWithGenerations = resolved.Candidates;
        var start = ComposeStart(request, candidatesWithGenerations);
        var coverage = ComposeCoverage(request, candidatesWithGenerations, start, snapshot.EnumerationComplete);
        var prior = LogPriorCallRules.Evaluate(request.PriorCall, request.Intent);
        var databaseBudget = LogBudgetRules.Evaluate(request.DatabaseBudgetLimits, request.DatabaseBudgetObservation);
        var globalBudget = LogBudgetRules.Evaluate(request.GlobalBudgetLimits, request.GlobalBudgetObservation);

        var observations = Observations(readiness, candidatesWithGenerations, out var candidateStart);
        var generations = resolved.Generations;
        var refs = CandidateRefs(candidatesWithGenerations, candidateStart, coverage);
        var candidateSetRevision = LogAssessmentCanonicalizer.CandidateSetRevision(candidatesWithGenerations);
        var assessmentId = LogAssessmentCanonicalizer.DeterministicId("assessment",
            request.DatabaseId, request.MutationId);
        var prepared = new PreparedLogAssessment(assessmentId, PreparedLogAssessment.CurrentSchemaVersion,
            requestFingerprint, string.Empty, input, readiness,
            new(technical.Readiness.ToString(), technical.ReasonCode), start, coverage,
            new(prior.MayEnterAdmission, prior.ReusesOriginalInvocation, prior.PreservesPriorUnknown, prior.ReasonCode),
            new(databaseBudget.AllowsNewLog, databaseBudget.ReasonCode),
            new(globalBudget.AllowsNewLog, globalBudget.ReasonCode),
            candidatesWithGenerations, candidateSetRevision, snapshot.EnumerationComplete,
            observations, generations, refs, issues, _clock.GetUtcNow());
        return prepared with { PayloadFingerprint = LogAssessmentCanonicalizer.PayloadFingerprint(prepared) };
    }

    private static LogEvidenceStatus MapStatus(TargetSqlLogReadinessEvidence evidence)
    {
        var db = evidence.Database;
        var recovery = evidence.Recovery;
        if (evidence.ExpectedIdentityMatches == false || evidence.StableAcrossRead == false
            || db.Issues.Concat(recovery.Issues).Any(x => x.Problem == BackupMetadataReadProblem.SourceConflict))
            return LogEvidenceStatus.SourceConflict;
        if (new[] { db.FailureCode, recovery.FailureCode }.Any(code => code == TargetSqlFailureCode.AuthorizationDenied))
            return LogEvidenceStatus.PermissionDenied;
        if (db.State is { } state && state != TargetSqlDatabaseState.Online && db.QueryComplete)
            return LogEvidenceStatus.Complete;
        if (!db.QueryComplete || !recovery.QueryComplete || db.State is null
            || recovery.LastLogBackupLsnState == TargetSqlLogLsnState.Unknown
            || evidence.StableAcrossRead is null)
            return LogEvidenceStatus.MissingFields;
        return LogEvidenceStatus.Complete;
    }

    private static DatabaseRecoveryModel? MapRecoveryModel(TargetSqlRecoveryModel? model) => model switch
    {
        TargetSqlRecoveryModel.Full => DatabaseRecoveryModel.Full,
        TargetSqlRecoveryModel.BulkLogged => DatabaseRecoveryModel.BulkLogged,
        TargetSqlRecoveryModel.Simple => DatabaseRecoveryModel.Simple,
        _ => null,
    };

    private static LogDataStartSnapshot ComposeStart(LogAssessmentRequest request,
        IReadOnlyList<LogAssessmentCandidate> candidates)
    {
        var decision = LogDataStartRules.Select(new(request.Scope,
            candidates.Select(ToStartCandidate), request.PreferredFullBackupSetId));
        return new(decision.Availability.ToString(), decision.ReasonCode, request.Scope.Kind.ToString(),
            request.Scope.StorageTargetId, decision.FullBackupSetId, decision.DifferentialBackupSetId,
            decision.RestorePointLsn, decision.Database, decision.Branch, decision.Generation,
            decision.Conflicts.Select(ToConflict).ToArray());
    }

    private static LogCoverageSnapshot ComposeCoverage(LogAssessmentRequest request,
        IReadOnlyList<LogAssessmentCandidate> candidates, LogDataStartSnapshot start, bool enumerationComplete)
    {
        var domainStart = LogDataStartRules.Select(new(request.Scope,
            candidates.Select(ToStartCandidate), request.PreferredFullBackupSetId));
        var result = LogCoverageRules.Evaluate(new(domainStart, enumerationComplete,
            candidates.Select(ToCoverageCandidate)));
        return new(result.Conclusion.ToString(), result.FrontierLsn, request.Scope.Kind.ToString(),
            request.Scope.StorageTargetId, result.UsedBackupSetIds, result.RejectedBackupSetIds,
            result.KnownContinuousRanges.Select(ToRange).ToArray(), result.KnownGaps.Select(ToRange).ToArray(),
            result.UnknownRanges.Select(ToRange).ToArray(), result.UnsupportedRanges.Select(ToRange).ToArray(),
            result.ReasonCodes, result.Conflicts.Select(ToConflict).ToArray());
    }

    private static LogDataStartCandidate ToStartCandidate(LogAssessmentCandidate candidate) => new()
    {
        BackupSetId = candidate.BackupSetId,
        Actual = candidate.Actual,
        Scope = candidate.Scope,
        LocationCopy = candidate.CopyState,
        IsManaged = candidate.IsManaged,
        DifferentialDependency = candidate.DifferentialDependency,
        MatchesFullBackupSetId = candidate.MatchesFullBackupSetId,
    };

    private static LogCoverageCandidate ToCoverageCandidate(LogAssessmentCandidate candidate) => new()
    {
        BackupSetId = candidate.BackupSetId,
        Actual = candidate.Actual,
        Scope = candidate.Scope,
        LocationCopy = candidate.CopyState,
        IsManaged = candidate.IsManaged,
        ProtectionExpectation = candidate.ProtectionExpectation,
    };

    private static LogObservationDraft[] Observations(TargetSqlLogReadinessEvidence evidence,
        IReadOnlyList<LogAssessmentCandidate> candidates, out int candidateStart)
    {
        var result = new List<LogObservationDraft>
        {
            new(0, "database.initial", LogObservationSource.RecoveryStatus,
                Status(evidence.Database), evidence.Database.ObservedAtUtc, evidence.Database.Issues.Count == 0,
                evidence.Database, null, evidence.Database.FailureCode?.ToString(),
                evidence.Database.FailurePhase?.ToString()),
            new(1, "recovery.initial", LogObservationSource.RecoveryStatus,
                evidence.Recovery.WasAttempted ? Status(evidence.Recovery) : LogEvidenceStatus.HistoryNotFound,
                evidence.Recovery.ObservedAtUtc, evidence.Recovery.QueryComplete,
                null, evidence.Recovery, evidence.Recovery.FailureCode?.ToString(),
                evidence.Recovery.FailurePhase?.ToString()),
        };
        var next = 2;
        if (evidence.Recheck is { } recheck)
            result.Add(new(next++, "database.recheck", LogObservationSource.RecoveryStatus,
                Status(recheck), recheck.ObservedAtUtc, recheck.QueryComplete, recheck, null,
                recheck.FailureCode?.ToString(), recheck.FailurePhase?.ToString()));
        if (evidence.RecoveryRecheck is { } recoveryRecheck)
            result.Add(new(next++, "recovery.recheck", LogObservationSource.RecoveryStatus,
                Status(recoveryRecheck), recoveryRecheck.ObservedAtUtc, recoveryRecheck.QueryComplete,
                null, recoveryRecheck, recoveryRecheck.FailureCode?.ToString(),
                recoveryRecheck.FailurePhase?.ToString()));
        candidateStart = next;
        foreach (var candidate in candidates)
            result.Add(new(next++, "candidate", candidate.Actual.Source,
                candidate.Status, candidate.Actual.ObservedAtUtc, candidate.HasRequiredFields,
                null, null, null, null));
        return result.ToArray();
    }

    private static LogEvidenceStatus Status(TargetSqlDatabaseReadinessObservation observation) =>
        observation.FailureCode == TargetSqlFailureCode.AuthorizationDenied ? LogEvidenceStatus.PermissionDenied
        : observation.QueryComplete ? LogEvidenceStatus.Complete : LogEvidenceStatus.MissingFields;

    private static LogEvidenceStatus Status(TargetSqlRecoveryStatusObservation observation) =>
        observation.FailureCode == TargetSqlFailureCode.AuthorizationDenied ? LogEvidenceStatus.PermissionDenied
        : observation.QueryComplete ? LogEvidenceStatus.Complete : LogEvidenceStatus.MissingFields;

    private static (LogAssessmentCandidate[] Candidates, LogGenerationDraft[] Generations) ResolveGenerations(
        Guid databaseId, LogAssessmentCandidate[] candidates)
    {
        var result = new List<LogAssessmentCandidate>(candidates.Length);
        var drafts = new List<LogGenerationDraft>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var actual = candidate.Actual;
            var valid = candidate.Status == LogEvidenceStatus.Complete
                && actual.Status == LogEvidenceStatus.Complete
                && candidate.HasRequiredFields
                && actual.Type is not null && actual.Database is { DatabaseGuid: { } databaseGuid, FamilyGuid: { } familyGuid }
                && databaseGuid != Guid.Empty && familyGuid != Guid.Empty
                && actual.Branch is { FirstRecoveryForkId: { } firstFork, RecoveryForkId: { } recoveryFork }
                && firstFork != Guid.Empty && recoveryFork != Guid.Empty;
            Guid? generationId = null;
            var evidenceKind = actual.Generation?.Evidence ?? LogGenerationEvidenceKind.Unspecified;
            if (valid && actual.Generation is { IsKnown: true } known
                && known.Evidence != LogGenerationEvidenceKind.Unspecified)
            {
                generationId = known.GenerationId;
            }
            else if (valid && actual.BeginsLogChain == true)
            {
                evidenceKind = LogGenerationEvidenceKind.BeginsLogChain;
                generationId = LogAssessmentCanonicalizer.DeterministicId("generation",
                    LogAssessmentCanonicalizer.EvidenceKey(databaseId,
                        actual, evidenceKind, candidate.SourceRevision));
            }

            var resolvedActual = actual with
            {
                Generation = generationId is { } id && id != Guid.Empty
                    ? new LogChainGeneration(id, evidenceKind)
                    : null,
            };
            result.Add(candidate with { Actual = resolvedActual });
            if (generationId is null) continue;
            var key = LogAssessmentCanonicalizer.EvidenceKey(databaseId,
                actual, evidenceKind, candidate.SourceRevision);
            if (keys.Add(key))
                drafts.Add(new(generationId, key, evidenceKind, actual.Database?.DatabaseGuid,
                    actual.Database?.FamilyGuid, actual.Branch?.FirstRecoveryForkId,
                    actual.Branch?.RecoveryForkId, actual.ObservedAtUtc, candidate.Status));
        }
        return (result.ToArray(), drafts.ToArray());
    }

    private static LogCandidateRefDraft[] CandidateRefs(IReadOnlyList<LogAssessmentCandidate> candidates,
        int candidateStart, LogCoverageSnapshot coverage)
    {
        var used = coverage.UsedBackupSetIds.ToHashSet();
        var rejected = coverage.RejectedBackupSetIds.ToHashSet();
        return candidates.Select((candidate, index) => new LogCandidateRefDraft(candidateStart + index,
            candidate.BackupSetId, candidate.Scope.Kind.ToString(), candidate.Scope.StorageTargetId,
            candidate.SourceRevision, used.Contains(candidate.BackupSetId), rejected.Contains(candidate.BackupSetId),
            candidate.Actual.Generation?.GenerationId)).ToArray();
    }

    private static LogConflictSnapshot ToConflict(LogObservationConflict conflict) =>
        new(conflict.BackupSetId, conflict.Scope.Kind.ToString(), conflict.Scope.StorageTargetId, conflict.FieldCode);
    private static LogRangeSnapshot ToRange(LogCoverageRange range) => new(range.FromLsn, range.ToLsn);
}

public sealed record LogAssessmentReadCommit(LogAssessmentReadCode Code, string? PayloadFingerprint,
    PreparedLogAssessment? Value);
