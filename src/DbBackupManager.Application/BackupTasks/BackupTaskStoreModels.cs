using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.BackupTasks;

public enum BackupTaskStoreResultCode
{
    Succeeded,
    AlreadyApplied,
    AlreadyExists,
    NotFound,
    ConfigurationUnavailable,
    AuthenticationRequired,
    StateMismatch,
    LeaseLost,
    ConcurrencyConflict,
}

public enum BackupStageOutcome
{
    Succeeded,
    ConfirmedFailed,
    Indeterminate,
    Cancelled,
}

public sealed record BackupTaskStoreResult<T>(BackupTaskStoreResultCode Code, T? Value = null)
    where T : class
{
    public bool IsSucceeded => Code is BackupTaskStoreResultCode.Succeeded
        or BackupTaskStoreResultCode.AlreadyApplied
        or BackupTaskStoreResultCode.AlreadyExists;
}

public sealed record CreateBackupTaskCommand(
    Guid TaskId,
    Guid PolicyId,
    BackupTaskTriggerType TriggerType,
    DateTimeOffset? ScheduledSlotAtUtc,
    Guid MutationId,
    DateTimeOffset OccurredAtUtc,
    Guid? ActorAdminUserId = null,
    string? ActorSecurityStamp = null);

public sealed record ClaimNextBackupTaskCommand(
    Guid LeaseToken,
    Guid BackupAttemptId,
    Guid MutationId,
    string LeaseOwner,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record BackupStageCommitCommand(
    Guid MutationId,
    BackupStageOutcome Outcome,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset? EvidenceAtUtc = null,
    long? SourceLengthBytes = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record BackupTaskMutationCommand(
    Guid TaskId,
    Guid MutationId,
    DateTimeOffset OccurredAtUtc,
    Guid? ActorAdminUserId = null,
    string? ActorSecurityStamp = null);

public sealed record ExpireExecutionLeaseCommand(
    Guid TaskId,
    Guid MutationId,
    DateTimeOffset UtcNow,
    string ErrorCode,
    string ErrorMessage);

public sealed record AcquireReconciliationLeaseCommand(
    Guid TaskId,
    Guid LeaseToken,
    string LeaseOwner,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record ReconciliationCommitCommand(
    Guid MutationId,
    BackupReconciliationOutcome Outcome,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset? EvidenceAtUtc = null,
    long? SourceLengthBytes = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record ConfirmNeedsAttentionCommand(
    Guid TaskId,
    Guid MutationId,
    DateTimeOffset OccurredAtUtc,
    BackupReconciliationOutcome Outcome,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    Guid? ActorAdminUserId = null,
    string? ActorSecurityStamp = null);

public sealed record BackupTaskStateModel(
    Guid TaskId,
    BackupTaskStatus Status,
    BackupTaskStage? CurrentStage,
    Guid? CurrentBackupAttemptId,
    DateTimeOffset? CancellationRequestedAtUtc,
    int RetryCount,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int ReconciliationAttemptCount = 0,
    DateTimeOffset? NextReconciliationAtUtc = null);

public sealed record BackupTaskIdentityModel(
    Guid ServerId,
    string ServerName,
    Guid InstanceId,
    string InstanceName,
    Guid DatabaseId,
    string DatabaseName);

public sealed record BackupSqlTargetModel(
    string ConnectionAddress,
    Guid SqlCredentialReferenceId,
    bool EncryptConnection,
    bool TrustServerCertificate,
    string? CertificateTrustReason,
    int ConnectionTimeoutSeconds,
    bool AllowLegacyTls = false, string? LegacyTlsReason = null);

public sealed record BackupFileEndpointModel(
    FileTransferProtocol Protocol,
    string Host,
    int? Port,
    string BasePath,
    Guid CredentialReferenceId,
    string? SftpHostKeyFingerprint);

public sealed record BackupTaskPolicyModel(
    BackupStorageMode StorageMode,
    Guid? StorageTargetId,
    BackupFileEndpointModel? RemoteEndpoint,
    int? LocalRetentionDays,
    int? RemoteRetentionDays,
    bool UseChecksum,
    bool UseCompression,
    bool UseCopyOnly,
    int BackupTimeoutMinutes,
    int VerifyTimeoutMinutes,
    int TransferTimeoutMinutes,
    string TimeZoneId);

public sealed record BackupTaskSnapshotModel(
    Guid TaskId,
    string PolicyName,
    BackupTaskIdentityModel Identity,
    BackupSqlTargetModel SqlTarget,
    string LocalSqlBackupRootPath,
    string FileNameRuleVersion,
    BackupFileEndpointModel WorkerSourceEndpoint,
    BackupTaskPolicyModel Policy,
    BackupType BackupType = BackupType.Full,
    BackupRunPurpose? Purpose = null);

public sealed class LeaseHandle
{
    private readonly byte[] _rowVersion;

    public LeaseHandle(
        Guid taskId,
        Guid leaseToken,
        BackupLeasePurpose purpose,
        BackupTaskStage stage,
        Guid backupAttemptId,
        DateTimeOffset expiresAtUtc,
        byte[] rowVersion)
    {
        ArgumentNullException.ThrowIfNull(rowVersion);
        TaskId = taskId;
        LeaseToken = leaseToken;
        Purpose = purpose;
        Stage = stage;
        BackupAttemptId = backupAttemptId;
        ExpiresAtUtc = expiresAtUtc;
        _rowVersion = [.. rowVersion];
    }

    public Guid TaskId { get; }

    public Guid LeaseToken { get; }

    public BackupLeasePurpose Purpose { get; }

    public BackupTaskStage Stage { get; }

    public Guid BackupAttemptId { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public byte[] RowVersion => [.. _rowVersion];
}

public sealed class BackupAttemptModel
{
    private readonly byte[] _rowVersion;

    public BackupAttemptModel(
        Guid id,
        int attemptNumber,
        BackupInvocationStatus invocationStatus,
        string localSqlFilePath,
        string workerSourceFilePath,
        Guid? remoteStorageTargetId,
        string? remotePartialFilePath,
        string? remoteFinalFilePath,
        long? sourceLengthBytes,
        DateTimeOffset? localVerifiedAtUtc,
        DateTimeOffset? remoteValidatedAtUtc,
        DateTimeOffset? localCleanupCompletedAtUtc,
        byte[] rowVersion)
    {
        ArgumentNullException.ThrowIfNull(rowVersion);
        Id = id;
        AttemptNumber = attemptNumber;
        InvocationStatus = invocationStatus;
        LocalSqlFilePath = localSqlFilePath;
        WorkerSourceFilePath = workerSourceFilePath;
        RemoteStorageTargetId = remoteStorageTargetId;
        RemotePartialFilePath = remotePartialFilePath;
        RemoteFinalFilePath = remoteFinalFilePath;
        SourceLengthBytes = sourceLengthBytes;
        LocalVerifiedAtUtc = localVerifiedAtUtc;
        RemoteValidatedAtUtc = remoteValidatedAtUtc;
        LocalCleanupCompletedAtUtc = localCleanupCompletedAtUtc;
        _rowVersion = [.. rowVersion];
    }

    public Guid Id { get; }

    public int AttemptNumber { get; }

    public BackupInvocationStatus InvocationStatus { get; }

    public string LocalSqlFilePath { get; }

    public string WorkerSourceFilePath { get; }

    public Guid? RemoteStorageTargetId { get; }

    public string? RemotePartialFilePath { get; }

    public string? RemoteFinalFilePath { get; }

    public long? SourceLengthBytes { get; }

    public DateTimeOffset? LocalVerifiedAtUtc { get; }

    public DateTimeOffset? RemoteValidatedAtUtc { get; }

    public DateTimeOffset? LocalCleanupCompletedAtUtc { get; }

    public byte[] RowVersion => [.. _rowVersion];
}

public sealed record BackupExecutionWorkItem(
    BackupTaskStateModel Task,
    BackupTaskSnapshotModel Snapshot,
    BackupAttemptModel Attempt,
    LeaseHandle Lease);

public sealed record BackupTaskTransitionModel(
    BackupTaskStateModel Task,
    LeaseHandle? Lease);
