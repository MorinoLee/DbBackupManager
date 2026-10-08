using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.BackupTasks;

public enum BackupExecutionOperationKind { Admission, SqlResult, Metadata, VerifyLocal, Transfer, ValidateCopy, Recovery }
public enum BackupExecutionOperationState { Reserved, Frozen, Applied, Abandoned }
public enum BackupExecutionObservationSource { Comparison, BackupHeader, Msdb, Database, Registration, SourceFile, RemotePartial, RemoteFinal, Termination }
public enum BackupExecutionOutcome { Unknown, Succeeded, ConfirmedFailed, Indeterminate, Cancelled }
public enum BackupSqlOutcomeSource { Unknown, NotInvoked, PlatformResponse, RecoveredEvidence }
public enum BackupContentState { Unknown, Verified }
public enum BackupObjectProtection { Unknown, Unsupported, GuardedUntilCommit }

/// <summary>完整 SHA-256 的规范十六进制表示；持久化边界使用 binary(32)。</summary>
public sealed record BackupContentEvidence
{
    public string? DigestAlgorithm { get; init; }
    public BackupContentState State { get; init; }
    public string? DigestHex { get; init; }
    public long? LengthBytes { get; init; }
    public string? StableObjectId { get; init; }
    public BackupObjectProtection Protection { get; init; }
    public string? ReasonCode { get; init; }

    public void Validate()
    {
        if (!Enum.IsDefined(State) || !Enum.IsDefined(Protection)) throw new ArgumentException("内容证据状态无效。");
        if (State == BackupContentState.Verified)
        {
            if (DigestAlgorithm != "SHA256" || LengthBytes is not > 0 || DigestHex is null || DigestHex.Length != 64
                || DigestHex.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
                throw new ArgumentException("完整内容证据必须包含正数长度和规范 SHA-256。");
        }
        else if (DigestAlgorithm is not null || DigestHex is not null || LengthBytes is not null)
            throw new ArgumentException("未知内容不能携带已验证摘要或长度。");
        if (StableObjectId is not null) BackupTaskValues.RequireText(StableObjectId, 256, nameof(StableObjectId));
        if (ReasonCode is not null) BackupTaskValues.RequireText(ReasonCode, 100, nameof(ReasonCode));
    }

    public bool MatchesContent(BackupContentEvidence other) =>
        State == BackupContentState.Verified && other.State == BackupContentState.Verified
        && DigestAlgorithm == "SHA256" && other.DigestAlgorithm == "SHA256"
        && LengthBytes is > 0 && DigestHex is { Length: 64 }
        && DigestHex.All(c => char.IsAsciiHexDigit(c) && !char.IsUpper(c))
        && LengthBytes == other.LengthBytes && DigestHex == other.DigestHex;
}

public sealed record BackupExecutionFacts
{
    public BackupExecutionOutcome Outcome { get; init; }
    public BackupSqlOutcomeSource SqlOutcomeSource { get; init; }
    public DateTimeOffset? PlatformCompletedAtUtc { get; init; }
    public DateTimeOffset? EvidenceAtUtc { get; init; }
    public bool SqlSuccessObserved { get; init; }
    public bool OriginalCallTerminated { get; init; }
    public bool OriginalCallerCannotInvoke { get; init; }
    public bool? UsedCopyOnly { get; init; }
    public bool? UsedChecksum { get; init; }
    public bool? UsedCompression { get; init; }
    public string? ReasonCode { get; init; }
    public Guid? ActualBaseBackupSetId { get; init; }
    public BackupSetMetadata Metadata { get; init; } = new();
    public BackupCompletionTimeDecision Completion { get; init; } =
        new(null, BackupCompletionTimeSource.Unknown, BackupCompletionTimeReason.SqlFinishMissing);
    public BackupSetAssessment Assessment { get; init; } = BackupSetAssessment.NotApplicable;
    public BackupSetAssessment ActiveAssessment { get; init; } = BackupSetAssessment.NotApplicable;
    public BackupContentEvidence Content { get; init; } = new();

    public void Validate()
    {
        if (!Enum.IsDefined(Outcome) || !Enum.IsDefined(SqlOutcomeSource)) throw new ArgumentException("执行结果无效。");
        BackupTaskValues.OptionalUtc(PlatformCompletedAtUtc, nameof(PlatformCompletedAtUtc));
        BackupTaskValues.OptionalUtc(EvidenceAtUtc, nameof(EvidenceAtUtc));
        if (PlatformCompletedAtUtc is not null
            && (SqlOutcomeSource != BackupSqlOutcomeSource.PlatformResponse || !SqlSuccessObserved))
            throw new ArgumentException("平台完成时刻必须有实际 SQL 成功响应。");
        if (SqlOutcomeSource == BackupSqlOutcomeSource.NotInvoked && SqlSuccessObserved)
            throw new ArgumentException("未调用不能携带 SQL 成功事实。");
        if (ReasonCode is not null) BackupTaskValues.RequireText(ReasonCode, 100, nameof(ReasonCode));
        if (ActualBaseBackupSetId is { } id) BackupTaskValues.RequireId(id, nameof(ActualBaseBackupSetId));
        Metadata.Validate();
        BackupSetCodes.ValidateCompletion(Completion);
        Assessment.Validate();
        ActiveAssessment.Validate();
        Content.Validate();
    }
}

public sealed class BackupPlanExecutionOperation : ConcurrentEntity
{
    private BackupPlanExecutionOperation() { }
    public BackupPlanExecutionOperation(Guid mutationId, Guid taskId, Guid attemptId,
        BackupExecutionOperationKind kind, int sequence, Guid intendedBackupSetId, DateTimeOffset observedAtUtc) : base(mutationId)
    {
        TaskId = BackupTaskValues.RequireId(taskId, nameof(taskId));
        AttemptId = BackupTaskValues.RequireId(attemptId, nameof(attemptId));
        IntendedBackupSetId = BackupTaskValues.RequireId(intendedBackupSetId, nameof(intendedBackupSetId));
        if (!Enum.IsDefined(kind) || sequence < 1) throw new ArgumentException("操作类型或编号无效。");
        Kind = kind;
        Sequence = sequence;
        ObservedAtUtc = BackupTaskValues.RequireUtc(observedAtUtc, nameof(observedAtUtc));
    }
    public Guid TaskId { get; private set; }
    public Guid AttemptId { get; private set; }
    public BackupExecutionOperationKind Kind { get; private set; }
    public int Sequence { get; private set; }
    public Guid IntendedBackupSetId { get; private set; }
    public DateTimeOffset ObservedAtUtc { get; private set; }
    public BackupExecutionOperationState State { get; private set; }
    public BackupExecutionFacts Facts { get; private set; } = new();
    public void Freeze(BackupExecutionFacts facts)
    {
        facts.Validate();
        if (State is BackupExecutionOperationState.Frozen or BackupExecutionOperationState.Applied)
        {
            if (Facts != facts) throw new InvalidOperationException("冻结载荷冲突。");
            return;
        }
        if (State != BackupExecutionOperationState.Reserved) throw new InvalidOperationException("操作不能冻结。");
        Facts = facts;
        State = BackupExecutionOperationState.Frozen;
    }
    public void MarkApplied()
    {
        if (State == BackupExecutionOperationState.Applied) return;
        if (State != BackupExecutionOperationState.Frozen) throw new InvalidOperationException("只有冻结操作可应用。");
        State = BackupExecutionOperationState.Applied;
    }
    public void Abandon()
    {
        if (State != BackupExecutionOperationState.Reserved
            || Kind is BackupExecutionOperationKind.SqlResult or BackupExecutionOperationKind.Transfer or BackupExecutionOperationKind.ValidateCopy)
            throw new InvalidOperationException("只有未冻结的只读操作可废弃。");
        State = BackupExecutionOperationState.Abandoned;
    }
}

public sealed class BackupPlanExecutionObservation
{
    private BackupPlanExecutionObservation() { }
    public BackupPlanExecutionObservation(Guid id, Guid operationId, int entryNumber,
        BackupExecutionObservationSource source, BackupSetEvidenceKind kind, BackupExecutionFacts facts)
    {
        Id = BackupTaskValues.RequireId(id, nameof(id));
        OperationId = BackupTaskValues.RequireId(operationId, nameof(operationId));
        if (entryNumber < 0 || !Enum.IsDefined(source) || !Enum.IsDefined(kind)) throw new ArgumentException("观察来源或编号无效。");
        EntryNumber = entryNumber;
        Source = source;
        Kind = kind;
        facts.Validate();
        Facts = facts;
    }
    public Guid Id { get; private set; }
    public Guid OperationId { get; private set; }
    public int EntryNumber { get; private set; }
    public BackupExecutionObservationSource Source { get; private set; }
    public BackupSetEvidenceKind Kind { get; private set; }
    public BackupExecutionFacts Facts { get; private set; } = new();
}

public enum BackupInvocationBindingState { Unknown, Known }
public enum BackupInvocationTerminationKind { PlatformCompleted, PlatformConfirmedFailed, RecoveredTerminated }
public sealed record BackupInvocationBinding
{
    public BackupInvocationBindingState State { get; init; }
    public Guid CallerIncarnationId { get; init; }
    public int? SessionId { get; init; }
    public DateTime? SessionEstablishedLocal { get; init; }
    public Guid? ConnectionId { get; init; }
    public void Validate()
    {
        BackupTaskValues.RequireId(CallerIncarnationId, nameof(CallerIncarnationId));
        if (!Enum.IsDefined(State) || State == BackupInvocationBindingState.Known
            && (SessionId is not > 0 || SessionEstablishedLocal is not { Kind: DateTimeKind.Unspecified })
            || State == BackupInvocationBindingState.Unknown
            && (SessionId is not null || SessionEstablishedLocal is not null || ConnectionId is not null)
            || ConnectionId == Guid.Empty) throw new ArgumentException("调用会话证据无效。");
    }
}

public sealed class BackupInvocationAuthorization : ConcurrentEntity
{
    private BackupInvocationAuthorization() { }
    public BackupInvocationAuthorization(Guid id, Guid databaseId, Guid taskId, Guid attemptId,
        Guid mutationId, Guid sqlOperationId, DateTimeOffset grantedAtUtc, BackupInvocationBinding binding) : base(id)
    {
        DatabaseId = BackupTaskValues.RequireId(databaseId, nameof(databaseId));
        TaskId = BackupTaskValues.RequireId(taskId, nameof(taskId));
        AttemptId = BackupTaskValues.RequireId(attemptId, nameof(attemptId));
        MutationId = BackupTaskValues.RequireId(mutationId, nameof(mutationId));
        SqlOperationId = BackupTaskValues.RequireId(sqlOperationId, nameof(sqlOperationId));
        GrantedAtUtc = BackupTaskValues.RequireUtc(grantedAtUtc, nameof(grantedAtUtc));
        binding.Validate();
        Binding = binding;
    }
    public Guid DatabaseId { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid AttemptId { get; private set; }
    public Guid MutationId { get; private set; }
    public Guid SqlOperationId { get; private set; }
    public DateTimeOffset GrantedAtUtc { get; private set; }
    public BackupInvocationBinding Binding { get; private set; } = new();
    public DateTimeOffset? TerminalObservedAtUtc { get; private set; }
    public BackupInvocationTerminationKind? TerminationKind { get; private set; }
    public Guid? TerminationEvidenceId { get; private set; }
    public Guid? TerminationMutationId { get; private set; }
    public void RecordTermination(Guid mutationId, Guid evidenceId, BackupInvocationTerminationKind kind,
        DateTimeOffset observedAtUtc, bool originalCallTerminated, bool originalCallerCannotInvoke)
    {
        BackupTaskValues.RequireId(mutationId, nameof(mutationId));
        BackupTaskValues.RequireId(evidenceId, nameof(evidenceId));
        BackupTaskValues.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (!Enum.IsDefined(kind) || !originalCallTerminated || !originalCallerCannotInvoke
            || kind == BackupInvocationTerminationKind.RecoveredTerminated && Binding.State != BackupInvocationBindingState.Known)
            throw new ArgumentException("缺少原调用终止及旧调用者不可重发的证据。");
        if (TerminalObservedAtUtc is not null)
        {
            if (TerminationMutationId != mutationId || TerminationEvidenceId != evidenceId
                || TerminationKind != kind || TerminalObservedAtUtc != observedAtUtc)
                throw new InvalidOperationException("授权终止事实冲突。");
            return;
        }
        TerminalObservedAtUtc = observedAtUtc;
        TerminationKind = kind;
        TerminationEvidenceId = evidenceId;
        TerminationMutationId = mutationId;
    }
}
