using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupLogs;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.BackupLogs;

public sealed record LogAssessmentCandidate(
    Guid BackupSetId,
    LogBackupActualMetadata Actual,
    LogLocationScope Scope,
    LogLocationCopyState CopyState,
    bool IsManaged,
    LogProtectionExpectation? ProtectionExpectation,
    DifferentialBaselineConclusion? DifferentialDependency,
    Guid? MatchesFullBackupSetId,
    Guid SourceRevision,
    LogEvidenceStatus Status = LogEvidenceStatus.Complete,
    bool HasRequiredFields = true)
{
    public void Validate()
    {
        if (BackupSetId == Guid.Empty || SourceRevision == Guid.Empty)
            throw new ArgumentException("LOG 候选必须带备份集与来源修订身份。");
        if (ProtectionExpectation is not null
            && !Enum.IsDefined(CopyState))
            throw new ArgumentException("LOG 候选副本状态无效。");
        if (DifferentialDependency is { } dependency && !Enum.IsDefined(dependency))
            throw new ArgumentException("差异依赖结论无效。");
    }
}

public sealed record LogAssessmentRequest
{
    public LogAssessmentRequest(
        Guid databaseId,
        Guid mutationId,
        string databaseName,
        Guid planId,
        Guid planVersionId,
        Guid configurationVersion,
        BackupDatabaseIdentity expectedDatabase,
        LogLocationScope scope,
        LogRequestIntent intent,
        DateTimeOffset observedAtUtc,
        int timeoutSeconds,
        TargetSqlConnectionInput connection,
        LogPriorCallFacts priorCall,
        LogCapabilityState capability,
        LogResourceAvailability resourceAvailability,
        LogBudgetLimits? databaseBudgetLimits,
        LogBudgetObservation? databaseBudgetObservation,
        LogBudgetLimits? globalBudgetLimits,
        LogBudgetObservation? globalBudgetObservation,
        bool enumerationComplete,
        Guid? preferredFullBackupSetId,
        IEnumerable<LogAssessmentCandidate> candidates)
    {
        if (databaseId == Guid.Empty || mutationId == Guid.Empty || planId == Guid.Empty
            || planVersionId == Guid.Empty || configurationVersion == Guid.Empty)
            throw new ArgumentException("LOG 评估必须固定数据库、计划、版本和配置身份。");
        if (string.IsNullOrWhiteSpace(databaseName) || databaseName.Length > 128
            || databaseName.Any(char.IsControl))
            throw new ArgumentException("LOG 评估必须固定目标数据库名。", nameof(databaseName));
        ArgumentNullException.ThrowIfNull(expectedDatabase);
        if (expectedDatabase.DatabaseGuid is not { } databaseGuid || databaseGuid == Guid.Empty
            || expectedDatabase.FamilyGuid is not { } familyGuid || familyGuid == Guid.Empty)
            throw new ArgumentException("LOG 评估必须绑定已知期望源身份。", nameof(expectedDatabase));
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(priorCall);
        if (observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("LOG 评估观察时间必须为 UTC。", nameof(observedAtUtc));
        if (timeoutSeconds is < 1 or > 3600)
            throw new ArgumentException("LOG 评估超时无效。", nameof(timeoutSeconds));
        var args = candidates?.ToArray() ?? throw new ArgumentNullException(nameof(candidates));
        foreach (var candidate in args) candidate.Validate();

        DatabaseId = databaseId;
        MutationId = mutationId;
        DatabaseName = databaseName;
        PlanId = planId;
        PlanVersionId = planVersionId;
        ConfigurationVersion = configurationVersion;
        ExpectedDatabase = expectedDatabase;
        Scope = scope;
        Intent = intent;
        ObservedAtUtc = observedAtUtc;
        TimeoutSeconds = timeoutSeconds;
        Connection = connection;
        PriorCall = priorCall;
        Capability = capability;
        ResourceAvailability = resourceAvailability;
        DatabaseBudgetLimits = databaseBudgetLimits;
        DatabaseBudgetObservation = databaseBudgetObservation;
        GlobalBudgetLimits = globalBudgetLimits;
        GlobalBudgetObservation = globalBudgetObservation;
        EnumerationComplete = enumerationComplete;
        PreferredFullBackupSetId = preferredFullBackupSetId;
        Candidates = Array.AsReadOnly(args);
    }

    public Guid DatabaseId { get; init; }
    public Guid MutationId { get; init; }
    public string DatabaseName { get; init; }
    public Guid PlanId { get; init; }
    public Guid PlanVersionId { get; init; }
    public Guid ConfigurationVersion { get; init; }
    public BackupDatabaseIdentity ExpectedDatabase { get; init; }
    public LogLocationScope Scope { get; init; }
    public LogRequestIntent Intent { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public int TimeoutSeconds { get; init; }
    public TargetSqlConnectionInput Connection { get; init; }
    public LogPriorCallFacts PriorCall { get; init; }
    public LogCapabilityState Capability { get; init; }
    public LogResourceAvailability ResourceAvailability { get; init; }
    public LogBudgetLimits? DatabaseBudgetLimits { get; init; }
    public LogBudgetObservation? DatabaseBudgetObservation { get; init; }
    public LogBudgetLimits? GlobalBudgetLimits { get; init; }
    public LogBudgetObservation? GlobalBudgetObservation { get; init; }
    public bool EnumerationComplete { get; init; }
    public Guid? PreferredFullBackupSetId { get; init; }
    public IReadOnlyList<LogAssessmentCandidate> Candidates { get; init; }
}

public sealed record TargetSqlConnectionSnapshot(
    string ConnectionAddress,
    Guid CredentialReferenceId,
    bool EncryptConnection,
    bool TrustServerCertificate,
    string? CertificateTrustReason,
    int ConnectionTimeoutSeconds,
    bool AllowLegacyTls,
    string? LegacyTlsReason)
{
    public static TargetSqlConnectionSnapshot From(TargetSqlConnectionInput input) => new(
        input.ConnectionAddress, input.CredentialReferenceId, input.EncryptConnection,
        input.TrustServerCertificate, input.CertificateTrustReason, input.ConnectionTimeoutSeconds,
        input.AllowLegacyTls, input.LegacyTlsReason);

    public TargetSqlConnectionInput ToInput() => new(ConnectionAddress, CredentialReferenceId,
        EncryptConnection, TrustServerCertificate, CertificateTrustReason, ConnectionTimeoutSeconds,
        AllowLegacyTls, LegacyTlsReason);
}

public sealed record LogAssessmentInput(
    Guid DatabaseId,
    Guid MutationId,
    string DatabaseName,
    Guid PlanId,
    Guid PlanVersionId,
    Guid ConfigurationVersion,
    BackupDatabaseIdentity ExpectedDatabase,
    string ScopeKind,
    Guid? StorageTargetId,
    LogRequestIntent Intent,
    DateTimeOffset ObservedAtUtc,
    int TimeoutSeconds,
    TargetSqlConnectionSnapshot Connection,
    LogPriorCallFacts PriorCall,
    LogCapabilityState Capability,
    LogResourceAvailability ResourceAvailability,
    LogBudgetLimits? DatabaseBudgetLimits,
    LogBudgetObservation? DatabaseBudgetObservation,
    LogBudgetLimits? GlobalBudgetLimits,
    LogBudgetObservation? GlobalBudgetObservation,
    bool EnumerationComplete,
    Guid? PreferredFullBackupSetId,
    IReadOnlyList<LogAssessmentCandidate> Candidates)
{
    public static LogAssessmentInput From(LogAssessmentRequest request) => new(
        request.DatabaseId, request.MutationId, request.DatabaseName, request.PlanId, request.PlanVersionId,
        request.ConfigurationVersion, request.ExpectedDatabase, request.Scope.Kind.ToString(),
        request.Scope.StorageTargetId, request.Intent, request.ObservedAtUtc, request.TimeoutSeconds,
        TargetSqlConnectionSnapshot.From(request.Connection), request.PriorCall, request.Capability,
        request.ResourceAvailability, request.DatabaseBudgetLimits, request.DatabaseBudgetObservation,
        request.GlobalBudgetLimits, request.GlobalBudgetObservation, request.EnumerationComplete,
        request.PreferredFullBackupSetId,
        request.Candidates.OrderBy(x => x.BackupSetId).ThenBy(x => x.SourceRevision)
            .ThenBy(x => x.Scope.Kind).ThenBy(x => x.Scope.StorageTargetId)
            .ThenBy(x => x.Actual.Type).ThenBy(x => x.Actual.FirstLsn?.Value)
            .ThenBy(x => x.Actual.LastLsn?.Value).ToArray());

    public LogLocationScope Scope => new(
        Enum.Parse<LogStorageLocation>(ScopeKind, false), StorageTargetId);

    public LogAssessmentRequest ToRequest() => new(DatabaseId, MutationId, DatabaseName, PlanId, PlanVersionId,
        ConfigurationVersion, ExpectedDatabase, Scope, Intent, ObservedAtUtc, TimeoutSeconds,
        Connection.ToInput(), PriorCall, Capability, ResourceAvailability, DatabaseBudgetLimits,
        DatabaseBudgetObservation, GlobalBudgetLimits, GlobalBudgetObservation, EnumerationComplete,
        PreferredFullBackupSetId, Candidates);
}

public sealed record LogObservationDraft(
    int EntryNumber,
    string Role,
    LogObservationSource Source,
    LogEvidenceStatus Status,
    DateTimeOffset? ObservedAtUtc,
    bool HasRequiredFields,
    TargetSqlDatabaseReadinessObservation? Database,
    TargetSqlRecoveryStatusObservation? Recovery,
    string? FailureCode,
    string? FailurePhase);

public sealed record LogGenerationDraft(
    Guid? GenerationId,
    string EvidenceKey,
    LogGenerationEvidenceKind EvidenceKind,
    Guid? SourceDatabaseGuid,
    Guid? FamilyGuid,
    Guid? FirstRecoveryForkId,
    Guid? RecoveryForkId,
    DateTimeOffset? ObservedAtUtc,
    LogEvidenceStatus Status);

public sealed record LogCandidateRefDraft(
    int EntryNumber,
    Guid BackupSetId,
    string ScopeKind,
    Guid? StorageTargetId,
    Guid SourceRevision,
    bool Used,
    bool Rejected,
    Guid? GenerationId);

public sealed record LogAssessmentIssueDraft(string Code);

public sealed record LogTechnicalSnapshot(string Readiness, string ReasonCode);
public sealed record LogPriorCallSnapshot(bool MayEnterAdmission, bool ReusesOriginalInvocation,
    bool PreservesPriorUnknown, string ReasonCode);
public sealed record LogBudgetSnapshot(bool AllowsNewLog, string ReasonCode);
public sealed record LogConflictSnapshot(Guid BackupSetId, string ScopeKind, Guid? StorageTargetId, string FieldCode);
public sealed record LogRangeSnapshot(BackupLsn? FromLsn, BackupLsn? ToLsn);
public sealed record LogDataStartSnapshot(string Availability, string ReasonCode, string ScopeKind,
    Guid? StorageTargetId, Guid? FullBackupSetId, Guid? DifferentialBackupSetId,
    BackupLsn? RestorePointLsn, BackupDatabaseIdentity? Database, BackupRecoveryBranch? Branch,
    LogChainGeneration? Generation, IReadOnlyList<LogConflictSnapshot> Conflicts);
public sealed record LogCoverageSnapshot(string Conclusion, BackupLsn? FrontierLsn, string ScopeKind,
    Guid? StorageTargetId, IReadOnlyList<Guid> UsedBackupSetIds, IReadOnlyList<Guid> RejectedBackupSetIds,
    IReadOnlyList<LogRangeSnapshot> Continuous, IReadOnlyList<LogRangeSnapshot> Gaps,
    IReadOnlyList<LogRangeSnapshot> Unknown, IReadOnlyList<LogRangeSnapshot> Unsupported,
    IReadOnlyList<string> ReasonCodes, IReadOnlyList<LogConflictSnapshot> Conflicts);

public sealed record PreparedLogAssessment(
    Guid AssessmentId,
    int SchemaVersion,
    string RequestFingerprint,
    string PayloadFingerprint,
    LogAssessmentInput Input,
    TargetSqlLogReadinessEvidence Readiness,
    LogTechnicalSnapshot Technical,
    LogDataStartSnapshot DataStart,
    LogCoverageSnapshot Coverage,
    LogPriorCallSnapshot PriorCall,
    LogBudgetSnapshot DatabaseBudget,
    LogBudgetSnapshot GlobalBudget,
    IReadOnlyList<LogAssessmentCandidate> ResolvedCandidates,
    string CandidateSetRevision,
    bool CandidateEnumerationComplete,
    IReadOnlyList<LogObservationDraft> Observations,
    IReadOnlyList<LogGenerationDraft> Generations,
    IReadOnlyList<LogCandidateRefDraft> Candidates,
    IReadOnlyList<LogAssessmentIssueDraft> Issues,
    DateTimeOffset PreparedAtUtc)
{
    public const int CurrentSchemaVersion = 1;

    public bool BudgetAllows => DatabaseBudget.AllowsNewLog && GlobalBudget.AllowsNewLog;

    /// <summary>仅供测试/传输；不是调用许可。</summary>
    public bool IsTechnicallyReady => Technical.Readiness == nameof(LogTechnicalReadiness.Ready);
}

public enum LogAssessmentReadCode { NotFound, Found, Conflict, PreparedPayloadRequired }
public sealed record LogAssessmentReadResult(LogAssessmentReadCode Code, PreparedLogAssessment? Value = null);

public enum LogAssessmentCommitCode { Succeeded, AlreadyApplied, Conflict }
public sealed record LogAssessmentCommitResult(LogAssessmentCommitCode Code, PreparedLogAssessment? Value = null);

public sealed class LogAssessmentConflictException(string message) : InvalidOperationException(message);

public static class LogAssessmentCanonicalizer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(), new BackupLsnJsonConverter(), new LogLocationScopeJsonConverter() },
    };

    public static string RequestFingerprint(LogAssessmentInput input) => Hash(input);
    public static string CandidateSetRevision(IReadOnlyList<LogAssessmentCandidate> candidates) => Hash(candidates);
    public static string PayloadFingerprint(PreparedLogAssessment prepared) =>
        Hash(prepared with { RequestFingerprint = string.Empty, PayloadFingerprint = string.Empty });

    public static string Serialize(PreparedLogAssessment prepared) =>
        JsonSerializer.Serialize(prepared, Options);

    public static PreparedLogAssessment Deserialize(string json) =>
        JsonSerializer.Deserialize<PreparedLogAssessment>(json, Options)
        ?? throw new InvalidOperationException("LOG 账本载荷无法重构。");

    public static Guid DeterministicId(string domain, params object?[] values)
    {
        var text = domain + "|" + string.Join("|", values.Select(ValueText));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return new Guid(hash.AsSpan(0, 16));
    }

    public static string EvidenceKey(Guid databaseId, LogBackupActualMetadata actual,
        LogGenerationEvidenceKind kind, Guid sourceRevision)
    {
        var text = string.Join("|", "generation", databaseId, actual.Database?.DatabaseGuid,
            actual.Database?.FamilyGuid, actual.Branch?.FirstRecoveryForkId,
            actual.Branch?.RecoveryForkId, kind, sourceRevision);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string Hash<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Options))))
            .ToLowerInvariant();

    private static string ValueText(object? value) => value switch
    {
        null => string.Empty,
        DateTimeOffset time => time.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private sealed class BackupLsnJsonConverter : JsonConverter<BackupLsn>
    {
        public override BackupLsn Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number) return new BackupLsn(reader.GetDecimal());
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                using var document = JsonDocument.ParseValue(ref reader);
                return new BackupLsn(document.RootElement.GetProperty("Value").GetDecimal());
            }

            throw new JsonException("BackupLsn 必须以数值或 Value 对象保存。");
        }

        public override void Write(Utf8JsonWriter writer, BackupLsn value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value.Value);
    }

    private sealed class LogLocationScopeJsonConverter : JsonConverter<LogLocationScope>
    {
        public override LogLocationScope Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var kind = Enum.Parse<LogStorageLocation>(
                document.RootElement.GetProperty("Kind").GetString()!, false);
            var target = document.RootElement.TryGetProperty("StorageTargetId", out var id)
                && id.ValueKind != JsonValueKind.Null ? id.GetGuid() : (Guid?)null;
            return new LogLocationScope(kind, target);
        }

        public override void Write(Utf8JsonWriter writer, LogLocationScope value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("Kind", value.Kind.ToString());
            if (value.StorageTargetId is { } target) writer.WriteString("StorageTargetId", target);
            else writer.WriteNull("StorageTargetId");
            writer.WriteEndObject();
        }
    }
}
