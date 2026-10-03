namespace DbBackupManager.Application.Identity;

public interface IAdminIdentityService
{
    Task<bool> IsSetupRequiredAsync(CancellationToken cancellationToken = default);

    Task<AdminIdentityResult> SetupAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken = default);

    Task<AdminIdentityResult> AuthenticateAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken = default);

    Task<AdminIdentityResult> ValidateSessionAsync(
        Guid adminUserId,
        string? securityStamp,
        CancellationToken cancellationToken = default);

    Task<AdminIdentityResult> ChangePasswordAsync(
        Guid adminUserId,
        string? securityStamp,
        string? currentPassword,
        string? newPassword,
        CancellationToken cancellationToken = default);

    Task RecordCsrfFailureAsync(
        Guid? actorAdminUserId,
        CancellationToken cancellationToken = default);
}
