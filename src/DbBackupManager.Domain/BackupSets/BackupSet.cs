using System.Collections.Frozen;
using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.BackupSets;

public readonly record struct BackupSetAssessment(
    BackupMetadataState State,
    DifferentialBaselineConclusion? Conclusion,
    string? ReasonCode)
{
    public static BackupSetAssessment NotApplicable => new(BackupMetadataState.NotApplicable, null, null);
    public static BackupSetAssessment FromDecision(DifferentialBaselineDecision decision) =>
        new(BackupMetadataState.Known, decision.Conclusion, decision.ReasonCode);

    public void Validate()
    {
        if (State == BackupMetadataState.NotApplicable && Conclusion is null && ReasonCode is null) return;
        if (State != BackupMetadataState.Known || Conclusion is null || !Enum.IsDefined(Conclusion.Value)
            || ReasonCode is null || !BackupSetCodes.BaselineReasons.Contains(ReasonCode))
            throw new ArgumentException("核对结论必须来自领域原因码，或明确标为不适用。");
    }
}

public static class BackupSetCodes
{
    public static IReadOnlySet<string> BaselineReasons { get; } = new[]
    {
        DifferentialBaselineReason.ManagedFullVerified, DifferentialBaselineReason.ManagedFullNotFound,
        DifferentialBaselineReason.ExternalFullObserved, DifferentialBaselineReason.SourceConflict,
        DifferentialBaselineReason.DatabaseIdentityMismatch, DifferentialBaselineReason.RecoveryBranchMismatch,
        DifferentialBaselineReason.BackupTypeMismatch, DifferentialBaselineReason.CopyOnlyFull,
        DifferentialBaselineReason.CopyOnlyDifferential, DifferentialBaselineReason.LsnMismatch,
        DifferentialBaselineReason.DatabaseBackupLsnMismatch, DifferentialBaselineReason.DuplicateManagedGuid,
        DifferentialBaselineReason.MissingFields, DifferentialBaselineReason.PermissionDenied,
        DifferentialBaselineReason.HistoryNotFound, DifferentialBaselineReason.MultipleBases
    }.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> UnknownCompletionReasons { get; } = new[]
    {
        BackupCompletionTimeReason.SqlFinishMissing, BackupCompletionTimeReason.ServerOffsetUnknown,
        BackupCompletionTimeReason.ServerTimeZoneUnknown, BackupCompletionTimeReason.AmbiguousLocalTime,
        BackupCompletionTimeReason.InvalidLocalTime, BackupCompletionTimeReason.ServerOffsetMismatch,
        BackupCompletionTimeReason.OlderThan24Hours, BackupCompletionTimeReason.FutureSqlTime,
        BackupCompletionTimeReason.ConversionOutOfRange
    }.ToFrozenSet(StringComparer.Ordinal);

    public static void ValidateCompletion(BackupCompletionTimeDecision completion)
    {
        var valid = completion.Source switch
        {
            BackupCompletionTimeSource.PlatformObserved => completion.CompletedAtUtc is { Offset.Ticks: 0 }
                && completion.ReasonCode == BackupCompletionTimeReason.PlatformObserved,
            BackupCompletionTimeSource.SqlLocalTime => completion.CompletedAtUtc is { Offset.Ticks: 0 }
                && completion.ReasonCode == BackupCompletionTimeReason.SqlLocalConverted,
            BackupCompletionTimeSource.Unknown => completion.CompletedAtUtc is null
                && UnknownCompletionReasons.Contains(completion.ReasonCode),
            _ => false
        };
        if (!valid) throw new ArgumentException("完成时间的值、来源和原因码不一致。");
    }

    internal static Guid RequireId(Guid id) =>
        id == Guid.Empty ? throw new ArgumentException("标识不能为空。") : id;
}

public sealed class BackupSet : ConcurrentEntity
{
    private BackupSet() { }
    public BackupSet(Guid id, Guid taskId, Guid attemptId, Guid databaseId,
        BackupSetMetadata metadata, BackupCompletionTimeDecision completion,
        BackupSetAssessment assessment, Guid? baseBackupSetId, bool sqlSucceeded)
        : base(id)
    {
        TaskId = BackupSetCodes.RequireId(taskId);
        AttemptId = BackupSetCodes.RequireId(attemptId);
        DatabaseId = BackupSetCodes.RequireId(databaseId);
        metadata.Validate();
        assessment.Validate();
        BackupSetCodes.ValidateCompletion(completion);
        Metadata = metadata;
        Completion = completion;
        Assessment = assessment;
        BaseBackupSetId = baseBackupSetId;
        if (baseBackupSetId is not null && assessment.Conclusion != DifferentialBaselineConclusion.Verified)
            throw new ArgumentException("新增依赖必须有已验证的领域结论。");
        SqlSuccessObserved = sqlSucceeded;
        ValidateDependency();
    }

    public Guid TaskId { get; private set; }
    public Guid AttemptId { get; private set; }
    public Guid DatabaseId { get; private set; }
    public BackupSetMetadata Metadata { get; private set; } = new();
    public BackupCompletionTimeDecision Completion { get; private set; } =
        new(null, BackupCompletionTimeSource.Unknown, BackupCompletionTimeReason.SqlFinishMissing);
    public BackupSetAssessment Assessment { get; private set; }
    public Guid? BaseBackupSetId { get; private set; }
    public bool SqlSuccessObserved { get; private set; }
    public bool HasMetadataConflict { get; private set; }
    public int ReconciliationCount { get; private set; }

    public void MarkMetadataConflict()
    {
        HasMetadataConflict = true;
        Assessment = new(BackupMetadataState.Known, DifferentialBaselineConclusion.Mismatch,
            DifferentialBaselineReason.SourceConflict);
    }

    public bool Reconcile(BackupSetMetadata metadata, BackupCompletionTimeDecision completion,
        BackupSetAssessment assessment, Guid? baseBackupSetId, bool sqlSucceeded, bool sourceConflict = false)
    {
        assessment.Validate();
        BackupSetCodes.ValidateCompletion(completion);
        if (baseBackupSetId is not null && assessment.Conclusion != DifferentialBaselineConclusion.Verified)
            throw new ArgumentException("新增依赖必须有已验证的领域结论。");
        var merged = Metadata.Merge(metadata, out var conflict);
        conflict |= sourceConflict;
        conflict |= BaseBackupSetId is not null && baseBackupSetId is not null
            && BaseBackupSetId != baseBackupSetId;
        if (!conflict)
        {
            Metadata = merged;
            BaseBackupSetId ??= baseBackupSetId;
            Assessment = assessment;
            ValidateDependency();
        }
        if (Completion.Source == BackupCompletionTimeSource.Unknown
            || Completion.Source == BackupCompletionTimeSource.SqlLocalTime
                && completion.Source == BackupCompletionTimeSource.PlatformObserved)
            Completion = completion;
        if (conflict || HasMetadataConflict)
        {
            HasMetadataConflict = true;
            Assessment = new(BackupMetadataState.Known, DifferentialBaselineConclusion.Mismatch,
                DifferentialBaselineReason.SourceConflict);
        }
        SqlSuccessObserved |= sqlSucceeded;
        ReconciliationCount = checked(ReconciliationCount + 1);
        return conflict;
    }

    private void ValidateDependency()
    {
        if (BaseBackupSetId is not null
            && (BaseBackupSetId == Id || Metadata.Type.Value != Configuration.BackupType.Differential))
            throw new ArgumentException("只有已验证 DIFF 可以登记受管 FULL 依赖。");
    }
}

public enum BackupSetEvidenceSource { Comparison = 1, BackupHeader = 2, Msdb = 3, Database = 4, Registration = 5 }
public enum BackupSetEvidenceKind { Backup = 1, Dependency = 2, ActiveBaseline = 3 }

/// <summary>按 Attempt、核对次数和条目序号追加；外部 FULL 只作为证据保存。</summary>
public sealed class BackupSetEvidence
{
    private BackupSetEvidence() { }
    public BackupSetEvidence(Guid id, Guid taskId, Guid attemptId, Guid? backupSetId,
        Guid mutationId, int reconciliationNumber, int entryNumber,
        BackupSetEvidenceSource source, BackupSetEvidenceKind kind,
        BackupSetMetadata metadata, BackupCompletionTimeDecision completion,
        BackupSetAssessment assessment, DateTimeOffset observedAtUtc, bool sqlSuccessObserved,
        Guid? requestedBaseBackupSetId = null)
    {
        Id = BackupSetCodes.RequireId(id);
        TaskId = BackupSetCodes.RequireId(taskId);
        AttemptId = BackupSetCodes.RequireId(attemptId);
        BackupSetId = backupSetId;
        MutationId = BackupSetCodes.RequireId(mutationId);
        if (reconciliationNumber < 1 || entryNumber < 0 || !Enum.IsDefined(source) || !Enum.IsDefined(kind)
            || observedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("证据编号、来源或时间无效。");
        metadata.Validate();
        assessment.Validate();
        BackupSetCodes.ValidateCompletion(completion);
        ReconciliationNumber = reconciliationNumber;
        EntryNumber = entryNumber;
        Source = source;
        Kind = kind;
        Metadata = metadata;
        Completion = completion;
        Assessment = assessment;
        ObservedAtUtc = observedAtUtc;
        SqlSuccessObserved = sqlSuccessObserved;
        RequestedBaseBackupSetId = requestedBaseBackupSetId;
    }
    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid AttemptId { get; private set; }
    public Guid? BackupSetId { get; private set; }
    public Guid? RequestedBaseBackupSetId { get; private set; }
    public Guid MutationId { get; private set; }
    public int ReconciliationNumber { get; private set; }
    public int EntryNumber { get; private set; }
    public BackupSetEvidenceSource Source { get; private set; }
    public BackupSetEvidenceKind Kind { get; private set; }
    public BackupSetMetadata Metadata { get; private set; } = new();
    public BackupCompletionTimeDecision Completion { get; private set; } =
        new(null, BackupCompletionTimeSource.Unknown, BackupCompletionTimeReason.SqlFinishMissing);
    public BackupSetAssessment Assessment { get; private set; }
    public DateTimeOffset ObservedAtUtc { get; private set; }
    public bool SqlSuccessObserved { get; private set; }
}
