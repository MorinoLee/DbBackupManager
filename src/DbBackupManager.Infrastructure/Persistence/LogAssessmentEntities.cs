namespace DbBackupManager.Infrastructure.Persistence;

public sealed class LogAssessmentCommit
{
    public Guid Id { get; set; }
    public Guid DatabaseId { get; set; }
    public Guid MutationId { get; set; }
    public int SchemaVersion { get; set; }
    public string RequestFingerprint { get; set; } = string.Empty;
    public string PayloadFingerprint { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string ScopeKind { get; set; } = string.Empty;
    public Guid? StorageTargetId { get; set; }
    public Guid PlanId { get; set; }
    public Guid PlanVersionId { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public DateTimeOffset PreparedAtUtc { get; set; }
    public DateTimeOffset CommittedAtUtc { get; set; }
    public string TechnicalReadiness { get; set; } = string.Empty;
    public string TechnicalReasonCode { get; set; } = string.Empty;
    public string DataStartAvailability { get; set; } = string.Empty;
    public string DataStartReasonCode { get; set; } = string.Empty;
    public string CoverageConclusion { get; set; } = string.Empty;
    public bool PriorCallMayEnterAdmission { get; set; }
    public string PriorCallReasonCode { get; set; } = string.Empty;
    public bool DatabaseBudgetAllows { get; set; }
    public string DatabaseBudgetReasonCode { get; set; } = string.Empty;
    public bool GlobalBudgetAllows { get; set; }
    public string GlobalBudgetReasonCode { get; set; } = string.Empty;
}

public sealed class LogObservationEntry
{
    public Guid Id { get; set; }
    public Guid AssessmentId { get; set; }
    public int EntryNumber { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? ObservedAtUtc { get; set; }
    public bool HasRequiredFields { get; set; }
    public string? FailureCode { get; set; }
    public string? FailurePhase { get; set; }
}

public sealed class LogCoverageEntry
{
    public Guid Id { get; set; }
    public Guid AssessmentId { get; set; }
    public string ScopeKind { get; set; } = string.Empty;
    public Guid? StorageTargetId { get; set; }
    public string Conclusion { get; set; } = string.Empty;
    public decimal? FrontierLsn { get; set; }
    public decimal? RestorePointLsn { get; set; }
}

public sealed class LogGenerationEvidence
{
    public Guid Id { get; set; }
    public string EvidenceKey { get; set; } = string.Empty;
    public string EvidenceKind { get; set; } = string.Empty;
    public Guid DatabaseId { get; set; }
    public Guid? SourceDatabaseGuid { get; set; }
    public Guid? FamilyGuid { get; set; }
    public Guid? FirstRecoveryForkId { get; set; }
    public Guid? RecoveryForkId { get; set; }
    public DateTimeOffset? ObservedAtUtc { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public string Status { get; set; } = string.Empty;
}
