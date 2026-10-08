using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.BackupSets;

public sealed record BackupMetadataReconciliationRequest(
    LeaseHandle Lease, Guid BackupSetId, Guid DatabaseId, Guid MutationId, DateTimeOffset ObservedAtUtc,
    BackupDatabaseIdentity ExpectedDatabase, TargetSqlConnectionInput Connection,
    TargetSqlBackupMetadataRequest File, bool SqlSuccessObserved, DateTimeOffset? PlatformCompletedAtUtc);

/// <summary>提交响应丢失后，保存并重放此固定命令；不得重新读取后复用 MutationId。</summary>
public sealed record PreparedBackupMetadataRegistration(
    RegisterBackupSetCommand Command, BackupSetAssessment HistoricalDependency, BackupSetAssessment ActiveBaseline,
    TargetSqlBackupMetadataEvidence? Evidence);

public sealed class BackupMetadataReconciliationService(
    ITargetSqlBackupMetadataReader reader, IBackupMetadataReconciliationLookup lookup, IBackupSetRegistrationStore store)
{
    public async Task<PreparedBackupMetadataRegistration> PrepareAsync(
        BackupMetadataReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.File.AttemptId != request.Lease.BackupAttemptId || request.DatabaseId == Guid.Empty
            || request.BackupSetId == Guid.Empty || request.MutationId == Guid.Empty
            || request.ObservedAtUtc.Offset != TimeSpan.Zero
            || request.PlatformCompletedAtUtc is { Offset.Ticks: not 0 })
            throw new ArgumentException("核对请求的身份或 UTC 时间无效。", nameof(request));

        var attempt = await lookup.ReadAttemptAsync(request.Lease.TaskId, request.Lease.BackupAttemptId, cancellationToken);
        if (attempt is null || attempt.DatabaseId != request.DatabaseId
            || attempt.DatabaseName != request.File.DatabaseName || attempt.SqlFilePath != request.File.AttemptFilePath
            || !SameConnection(attempt.Connection, request.Connection))
            throw new ArgumentException("读取目标与 Attempt 固定快照不一致。", nameof(request));
        var replay = await lookup.ReadReplayAsync(request.Lease, request.DatabaseId, request.BackupSetId, request.MutationId, cancellationToken);
        if (replay is not null)
        {
            if (replay.Command.SqlSuccessObserved != request.SqlSuccessObserved
                || request.PlatformCompletedAtUtc is { } completed
                    && replay.Command.Completion.Source == BackupCompletionTimeSource.PlatformObserved
                    && replay.Command.Completion.CompletedAtUtc != completed)
                throw new ArgumentException("同一 MutationId 不能替换 SQL 成功或平台完成事实。", nameof(request));
            return new(replay.Command, replay.HistoricalDependency, replay.ActiveBaseline, null);
        }

        // SQL 读取期间尚未打开任何平台 Context；查询受管记录也逐次关闭 Context。
        var evidence = await reader.ReadAsync(request.Connection, request.File, cancellationToken);
        var merged = evidence.Header.Metadata.Merge(evidence.History.Metadata, out var conflict);
        var status = conflict ? BaselineEvidenceStatus.SourceConflict : ProblemStatus(evidence.Header, evidence.History);
        var completion = BackupCompletionTimeRules.Evaluate(request.PlatformCompletedAtUtc,
            merged.SqlFinishedLocal.Value, request.ObservedAtUtc, evidence.CurrentServerOffset, evidence.ServerTimeZone);
        var historical = BackupSetAssessment.NotApplicable;
        Guid? baseId = null;
        if (merged.Type.Value == BackupType.Differential || merged.Type.State == BackupMetadataState.Unknown || conflict)
        {
            var candidates = await Candidates(merged.DifferentialBaseGuid.Value);
            var full = External(merged.DifferentialBaseGuid.Value);
            var differential = new DifferentialBackupEvidence(new(Identity(merged), Branch(merged), merged.DifferentialBaseGuid.Value,
                    merged.DifferentialBaseLsn.Value, status), merged.Type.Value, merged.IsCopyOnly.Value,
                    merged.DatabaseBackupLsn.Value);
            // 先交给领域识别已确认的冲突，缺少其他字段不能掩盖 COPY_ONLY 或明确矛盾。
            var known = DifferentialBaselineRules.EvaluateDependency(request.ExpectedDatabase,
                differential with { ActualBase = differential.ActualBase with { Status = KnownStatus(status) } },
                KnownCandidates(candidates), full is null ? null : full with { Status = KnownStatus(full.Status) });
            var decision = known.Conclusion == DifferentialBaselineConclusion.Mismatch ? known
                : DifferentialBaselineRules.EvaluateDependency(request.ExpectedDatabase, differential, candidates, full);
            historical = BackupSetAssessment.FromDecision(decision);
            baseId = decision.ManagedFullId;
        }
        else if (status != BaselineEvidenceStatus.Complete)
            historical = Unknown(status);
        else if (request.ExpectedDatabase.DatabaseGuid is null || request.ExpectedDatabase.DatabaseGuid == Guid.Empty
            || request.ExpectedDatabase.FamilyGuid is null || request.ExpectedDatabase.FamilyGuid == Guid.Empty)
            historical = Unknown(BaselineEvidenceStatus.MissingFields);
        else if (Identity(merged) != request.ExpectedDatabase)
            historical = new(BackupMetadataState.Known, DifferentialBaselineConclusion.Mismatch,
                DifferentialBaselineReason.DatabaseIdentityMismatch);

        var activeGuid = evidence.Active.Baseline.DataFileBases.Select(x => x.BaseBackupSetGuid).Distinct().ToArray();
        var activeCandidates = activeGuid.Length == 1 ? await Candidates(activeGuid[0]) : [];
        if (activeGuid.Length == 1 && activeGuid[0] is { } currentGuid && currentGuid == merged.BackupSetGuid.Value
            && merged.Type.Value == BackupType.Full && merged.IsCopyOnly.Value == false
            && status == BaselineEvidenceStatus.Complete && request.SqlSuccessObserved
            && activeCandidates.All(x => x.Evidence.BackupSetGuid != currentGuid))
        {
            // 本次普通 FULL 将在同一登记命令中成为受管备份集；外部 FULL 不走此入口。
            activeCandidates = activeCandidates.Append(new(request.BackupSetId, Full(merged, status))).ToArray();
        }
        var activeFull = activeGuid.Length == 1 ? External(activeGuid[0]) : null;
        var currentBaseline = evidence.Active.Baseline;
        var knownActive = DifferentialBaselineRules.EvaluateActiveBaseline(request.ExpectedDatabase,
            new(currentBaseline.Database, currentBaseline.CurrentRecoveryForkId,
                currentBaseline.DataFileBases.Select(x => x with { Status = KnownStatus(x.Status) }), KnownStatus(currentBaseline.Status)),
            KnownCandidates(activeCandidates), activeFull is null ? null : activeFull with { Status = KnownStatus(activeFull.Status) });
        var active = BackupSetAssessment.FromDecision(knownActive.Conclusion == DifferentialBaselineConclusion.Mismatch ? knownActive
            : DifferentialBaselineRules.EvaluateActiveBaseline(request.ExpectedDatabase, currentBaseline, activeCandidates, activeFull));
        var observations = new List<BackupSetObservation>
        {
            Observation(evidence.Header, BackupSetEvidenceKind.Backup, historical),
            Observation(evidence.History, BackupSetEvidenceKind.Backup, historical)
        };
        foreach (var full in evidence.ReferencedFulls)
            observations.Add(Observation(full, BackupSetEvidenceKind.Dependency,
                full.Status == BaselineEvidenceStatus.Complete ? BackupSetAssessment.NotApplicable : Unknown(full.Status)));
        foreach (var source in new[] { evidence.Header, evidence.History }.Concat(evidence.ReferencedFulls))
            foreach (var record in source.AmbiguousRecords ?? [])
                observations.Add(Observation(source with { Metadata = record },
                    source.RequestedBackupSetGuid is null ? BackupSetEvidenceKind.Backup : BackupSetEvidenceKind.Dependency,
                    Unknown(source.Status)));
        foreach (var file in evidence.Active.Baseline.DataFileBases.DefaultIfEmpty())
        {
            var current = evidence.Active.Baseline;
            observations.Add(new(BackupSetEvidenceSource.Database, BackupSetEvidenceKind.ActiveBaseline,
                new()
                {
                    DatabaseGuid = Field(current.Database.DatabaseGuid),
                    FamilyGuid = Field(current.Database.FamilyGuid),
                    RecoveryForkId = Field(current.CurrentRecoveryForkId),
                    DifferentialBaseGuid = Field(file?.BaseBackupSetGuid),
                    DifferentialBaseLsn = Field(file?.BaseLsn)
                }, new(null, BackupCompletionTimeSource.Unknown, BackupCompletionTimeReason.SqlFinishMissing), active));
        }
        var command = new RegisterBackupSetCommand(request.Lease, request.BackupSetId, request.DatabaseId,
            request.MutationId, request.ObservedAtUtc, merged, completion, historical, baseId,
            request.SqlSuccessObserved, observations.AsReadOnly());
        return new(command, historical, active, evidence);

        async Task<IReadOnlyList<ManagedFullBackupCandidate>> Candidates(Guid? guid) => guid is { } value
            ? await lookup.FindAsync(request.DatabaseId, value, cancellationToken) : [];
        FullBackupBaselineEvidence? External(Guid? guid)
        {
            var matches = evidence.ReferencedFulls.Where(x => (x.Metadata.BackupSetGuid.Value == guid
                || x.RequestedBackupSetGuid == guid) && guid is not null).ToArray();
            if (matches.Length == 0) return null;
            var candidate = matches[0];
            var candidateMetadata = candidate.Metadata;
            var candidateStatus = candidate.Status;
            foreach (var other in matches.Skip(1))
            {
                candidateMetadata = candidateMetadata.Merge(other.Metadata, out var different);
                if (different) candidateStatus = BaselineEvidenceStatus.SourceConflict;
            }
            return Full(candidateMetadata, candidateStatus) with { BackupSetGuid = candidate.Metadata.BackupSetGuid.Value ?? guid };
        }
        BackupSetObservation Observation(BackupMetadataSourceEvidence source, BackupSetEvidenceKind kind,
            BackupSetAssessment assessment) => new(source.Source, kind, source.Metadata,
                BackupCompletionTimeRules.Evaluate(kind == BackupSetEvidenceKind.Backup ? request.PlatformCompletedAtUtc : null,
                    source.Metadata.SqlFinishedLocal.Value, request.ObservedAtUtc, evidence.CurrentServerOffset, evidence.ServerTimeZone), assessment);
    }

    public Task<BackupTaskStoreResult<BackupSetRegistrationResult>> CommitAsync(
        PreparedBackupMetadataRegistration prepared, CancellationToken cancellationToken = default) =>
        store.RegisterAsync(prepared.Command, cancellationToken);

    private static BackupMetadataField<T> Field<T>(T? value) where T : struct
    {
        if (value is Guid id && id == Guid.Empty) return default;
        return value is { } known ? BackupMetadata.Known(known) : default;
    }
    private static bool SameConnection(TargetSqlConnectionInput a, TargetSqlConnectionInput b) =>
        a.ConnectionAddress == b.ConnectionAddress && a.CredentialReferenceId == b.CredentialReferenceId
        && a.EncryptConnection == b.EncryptConnection && a.TrustServerCertificate == b.TrustServerCertificate
        && a.CertificateTrustReason == b.CertificateTrustReason && a.ConnectionTimeoutSeconds == b.ConnectionTimeoutSeconds
        && a.AllowLegacyTls == b.AllowLegacyTls && a.LegacyTlsReason == b.LegacyTlsReason;
    internal static BackupDatabaseIdentity Identity(BackupSetMetadata value) => new(value.DatabaseGuid.Value, value.FamilyGuid.Value);
    internal static BackupRecoveryBranch Branch(BackupSetMetadata value) => new(value.FirstRecoveryForkId.Value, value.RecoveryForkId.Value);
    public static FullBackupBaselineEvidence Full(BackupSetMetadata value, BaselineEvidenceStatus status) =>
        new(value.BackupSetGuid.Value, Identity(value), Branch(value), value.Type.Value, value.IsCopyOnly.Value, value.CheckpointLsn.Value, status);

    private static BaselineEvidenceStatus KnownStatus(BaselineEvidenceStatus status) =>
        status == BaselineEvidenceStatus.MissingFields ? BaselineEvidenceStatus.Complete : status;
    private static ManagedFullBackupCandidate[] KnownCandidates(IReadOnlyList<ManagedFullBackupCandidate> candidates) =>
        candidates.Select(x => x with { Evidence = x.Evidence with { Status = KnownStatus(x.Evidence.Status) } }).ToArray();
    private static BaselineEvidenceStatus ProblemStatus(params BackupMetadataSourceEvidence[] sources)
    {
        foreach (var status in new[] { BaselineEvidenceStatus.SourceConflict, BaselineEvidenceStatus.PermissionDenied,
            BaselineEvidenceStatus.HistoryNotFound, BaselineEvidenceStatus.MultipleBases, BaselineEvidenceStatus.MissingFields })
            if (sources.Any(x => x.Status == status)) return status;
        return BaselineEvidenceStatus.Complete;
    }
    private static BackupSetAssessment Unknown(BaselineEvidenceStatus status) => new(BackupMetadataState.Known,
        status == BaselineEvidenceStatus.SourceConflict ? DifferentialBaselineConclusion.Mismatch : DifferentialBaselineConclusion.Unknown,
        status switch
        {
            BaselineEvidenceStatus.SourceConflict => DifferentialBaselineReason.SourceConflict,
            BaselineEvidenceStatus.PermissionDenied => DifferentialBaselineReason.PermissionDenied,
            BaselineEvidenceStatus.HistoryNotFound => DifferentialBaselineReason.HistoryNotFound,
            _ => DifferentialBaselineReason.MissingFields
        });
}
