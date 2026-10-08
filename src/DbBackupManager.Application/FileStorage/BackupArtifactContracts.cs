using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.FileStorage;

public enum BackupArtifactPathRole { RegisteredFile, RemotePartial, RemoteOnlySource }
public sealed record BackupArtifactDeletionOwner(Guid TaskId, Guid? AttemptId, Guid? FileId,
    BackupArtifactPathRole Role, Guid LeaseToken, byte[] RowVersion);
public sealed record BackupArtifactDeletionDecision(bool Allowed, string? ReasonCode = null);
public interface IBackupArtifactDeletionGuard
{
    Task<BackupArtifactDeletionDecision> EvaluateAsync(BackupFileDeleteRequest request, CancellationToken cancellationToken = default);
}

/// <summary>句柄能力不持久化；重启后的回执不能替代新的对象保护。</summary>
public interface IVerifiedBackupFileHandle : IAsyncDisposable
{
    BackupFileEndpointInput Endpoint { get; }
    string Path { get; }
    BackupContentEvidence Content { get; }
    bool GuardsExactPathUntilCommit { get; }
}
public sealed record BackupContentVerificationRequest(Guid TaskId, Guid AttemptId, BackupFileEndpointInput Endpoint,
    string Path, int TimeoutSeconds, BackupContentEvidence? ExpectedContent = null);
public interface IBackupFileContentVerifier
{
    Task<BackupFileStorageResult<IVerifiedBackupFileHandle>> OpenVerifiedAsync(
        BackupContentVerificationRequest request, CancellationToken cancellationToken = default);
    Task<BackupFileStorageResult<IVerifiedBackupFileHandle>> VerifyExistingAsync(
        BackupContentVerificationRequest request, CancellationToken cancellationToken = default);
}
public sealed record VerifiedBackupTransferReceipt(BackupContentEvidence SourceContent, BackupContentEvidence SentContent,
    BackupContentEvidence PartialReadBackContent)
{
    public bool MatchesVerifiedSource(BackupContentEvidence frozenSource) =>
        frozenSource.MatchesContent(SourceContent) && frozenSource.MatchesContent(SentContent)
        && frozenSource.MatchesContent(PartialReadBackContent);
}
public sealed record VerifiedBackupRenameReceipt(BackupContentEvidence BeforeRename, BackupContentEvidence FinalReadBack,
    bool PartialExists, bool FinalExists, IVerifiedBackupFileHandle FinalHandle)
{
    public bool CanRegisterAvailable(BackupContentEvidence frozenSource, BackupFileEndpointInput endpoint, string path) =>
        !PartialExists && FinalExists && frozenSource.MatchesContent(BeforeRename)
        && frozenSource.MatchesContent(FinalReadBack) && frozenSource.MatchesContent(FinalHandle.Content)
        && FinalHandle.GuardsExactPathUntilCommit && FinalHandle.Content.Protection == BackupObjectProtection.GuardedUntilCommit
        && FinalHandle.Endpoint.Protocol == endpoint.Protocol && FinalHandle.Endpoint.Host == endpoint.Host
        && FinalHandle.Endpoint.Port == endpoint.Port && FinalHandle.Endpoint.RootPath == endpoint.RootPath
        && FinalHandle.Endpoint.CredentialReferenceId == endpoint.CredentialReferenceId
        && FinalHandle.Endpoint.SftpHostKeyFingerprint == endpoint.SftpHostKeyFingerprint && FinalHandle.Path == path;
}
public interface IBackupPlanFileTransferExecutor
{
    Task<BackupFileStorageResult<VerifiedBackupTransferReceipt>> TransferVerifiedAsync(Guid mutationId,
        IVerifiedBackupFileHandle source, BackupContentVerificationRequest target, CancellationToken cancellationToken = default);
    Task<BackupFileStorageResult<VerifiedBackupRenameReceipt>> RenameAndVerifyAsync(Guid mutationId,
        BackupContentVerificationRequest partialFile, string finalPath, CancellationToken cancellationToken = default);
}
