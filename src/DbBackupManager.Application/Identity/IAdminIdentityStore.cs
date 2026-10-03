namespace DbBackupManager.Application.Identity;

public interface IAdminIdentityStore
{
    Task<bool> IsSetupRequiredAsync(CancellationToken cancellationToken = default);

    Task<AdminStoreResult> TryCreateFirstAdminAsync(
        NewAdminCredential credential,
        CancellationToken cancellationToken = default);

    Task<AdminCredentialSnapshot?> FindByNormalizedUsernameAsync(
        string normalizedUsername,
        CancellationToken cancellationToken = default);

    Task<AdminCredentialSnapshot?> FindByIdAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default);

    Task<AdminStoreResult> RecordFailedLoginAsync(
        AdminCredentialSnapshot credential,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    Task<AdminStoreResult> RecordSuccessfulLoginAsync(
        AdminCredentialSnapshot credential,
        string? replacementPasswordHash,
        CancellationToken cancellationToken = default);

    Task<AdminStoreResult> ChangePasswordAsync(
        AdminCredentialSnapshot credential,
        string passwordHash,
        string securityStamp,
        CancellationToken cancellationToken = default);

    Task RecordRejectedAuthenticationAsync(
        Guid? actorAdminUserId,
        string action,
        string reasonCode,
        CancellationToken cancellationToken = default);

    Task RecordCsrfFailureAsync(
        Guid? actorAdminUserId,
        CancellationToken cancellationToken = default);
}
