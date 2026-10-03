using System.Security.Cryptography;
using System.Text;

namespace DbBackupManager.Application.Identity;

public sealed class AdminIdentityService(
    IAdminIdentityStore store,
    IAdminPasswordService passwordService,
    TimeProvider timeProvider) : IAdminIdentityService
{
    private const int MaximumConcurrencyAttempts = 3;

    public Task<bool> IsSetupRequiredAsync(CancellationToken cancellationToken = default)
    {
        return store.IsSetupRequiredAsync(cancellationToken);
    }

    public async Task<AdminIdentityResult> SetupAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var validation = AdminCredentialPolicy.Validate(username, password);
        if (!validation.IsValid)
        {
            return new AdminIdentityResult(
                AdminIdentityResultCode.ValidationFailed,
                ValidationFailures: validation.Failures);
        }

        var credential = new NewAdminCredential(
            Guid.NewGuid(),
            validation.Username!,
            validation.NormalizedUsername!,
            passwordService.HashPassword(password!),
            CreateSecurityStamp());
        var result = await store.TryCreateFirstAdminAsync(credential, cancellationToken);

        return result switch
        {
            AdminStoreResult.Succeeded => new AdminIdentityResult(
                AdminIdentityResultCode.Succeeded,
                new AdminSession(credential.Id, credential.Username, credential.SecurityStamp)),
            AdminStoreResult.SetupUnavailable => new AdminIdentityResult(AdminIdentityResultCode.SetupUnavailable),
            _ => new AdminIdentityResult(AdminIdentityResultCode.ConcurrencyConflict),
        };
    }

    public async Task<AdminIdentityResult> AuthenticateAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var validation = AdminCredentialPolicy.Validate(username, password);
        if (!validation.IsValid)
        {
            passwordService.VerifyPassword(null, password ?? string.Empty);
            await store.RecordRejectedAuthenticationAsync(
                null,
                "auth.login",
                "invalid_credentials",
                cancellationToken);
            return new AdminIdentityResult(AdminIdentityResultCode.InvalidCredentials);
        }

        for (var attempt = 0; attempt < MaximumConcurrencyAttempts; attempt++)
        {
            var credential = await store.FindByNormalizedUsernameAsync(
                validation.NormalizedUsername!,
                cancellationToken);

            if (credential is null)
            {
                passwordService.VerifyPassword(null, password!);
                await store.RecordRejectedAuthenticationAsync(
                    null,
                    "auth.login",
                    "invalid_credentials",
                    cancellationToken);
                return new AdminIdentityResult(AdminIdentityResultCode.InvalidCredentials);
            }

            var verification = passwordService.VerifyPassword(credential.PasswordHash, password!);
            var now = timeProvider.GetUtcNow();

            if (!credential.IsEnabled || credential.IsLockedOut(now))
            {
                await store.RecordRejectedAuthenticationAsync(
                    credential.Id,
                    "auth.login",
                    "invalid_credentials",
                    cancellationToken);
                return new AdminIdentityResult(AdminIdentityResultCode.InvalidCredentials);
            }

            if (verification == PasswordVerificationStatus.Failed)
            {
                var failedResult = await store.RecordFailedLoginAsync(credential, now, cancellationToken);
                if (failedResult == AdminStoreResult.ConcurrencyConflict)
                {
                    continue;
                }

                return new AdminIdentityResult(AdminIdentityResultCode.InvalidCredentials);
            }

            var replacementHash = verification == PasswordVerificationStatus.SucceededRehashNeeded
                ? passwordService.HashPassword(password!)
                : null;
            var successResult = await store.RecordSuccessfulLoginAsync(
                credential,
                replacementHash,
                cancellationToken);

            if (successResult == AdminStoreResult.ConcurrencyConflict)
            {
                continue;
            }

            if (successResult != AdminStoreResult.Succeeded)
            {
                return new AdminIdentityResult(AdminIdentityResultCode.InvalidCredentials);
            }

            return new AdminIdentityResult(
                AdminIdentityResultCode.Succeeded,
                new AdminSession(credential.Id, credential.Username, credential.SecurityStamp));
        }

        return new AdminIdentityResult(AdminIdentityResultCode.InvalidCredentials);
    }

    public async Task<AdminIdentityResult> ValidateSessionAsync(
        Guid adminUserId,
        string? securityStamp,
        CancellationToken cancellationToken = default)
    {
        if (adminUserId == Guid.Empty || string.IsNullOrWhiteSpace(securityStamp))
        {
            return new AdminIdentityResult(AdminIdentityResultCode.AuthenticationRequired);
        }

        var credential = await store.FindByIdAsync(adminUserId, cancellationToken);
        if (credential is null
            || !credential.IsEnabled
            || !FixedTimeEquals(credential.SecurityStamp, securityStamp))
        {
            return new AdminIdentityResult(AdminIdentityResultCode.AuthenticationRequired);
        }

        return new AdminIdentityResult(
            AdminIdentityResultCode.Succeeded,
            new AdminSession(credential.Id, credential.Username, credential.SecurityStamp));
    }

    public async Task<AdminIdentityResult> ChangePasswordAsync(
        Guid adminUserId,
        string? securityStamp,
        string? currentPassword,
        string? newPassword,
        CancellationToken cancellationToken = default)
    {
        var validationFailures = AdminCredentialPolicy.ValidatePassword(newPassword);
        if (validationFailures.Count > 0)
        {
            return new AdminIdentityResult(
                AdminIdentityResultCode.ValidationFailed,
                ValidationFailures: validationFailures);
        }

        for (var attempt = 0; attempt < MaximumConcurrencyAttempts; attempt++)
        {
            var credential = await store.FindByIdAsync(adminUserId, cancellationToken);
            if (credential is null
                || !credential.IsEnabled
                || string.IsNullOrWhiteSpace(securityStamp)
                || !FixedTimeEquals(credential.SecurityStamp, securityStamp))
            {
                return new AdminIdentityResult(AdminIdentityResultCode.AuthenticationRequired);
            }

            if (passwordService.VerifyPassword(credential.PasswordHash, currentPassword ?? string.Empty)
                == PasswordVerificationStatus.Failed)
            {
                await store.RecordRejectedAuthenticationAsync(
                    credential.Id,
                    "auth.password.change",
                    "invalid_credentials",
                    cancellationToken);
                return new AdminIdentityResult(AdminIdentityResultCode.InvalidCredentials);
            }

            var newHash = passwordService.HashPassword(newPassword!);
            var newStamp = CreateSecurityStamp();
            var result = await store.ChangePasswordAsync(
                credential,
                newHash,
                newStamp,
                cancellationToken);

            if (result == AdminStoreResult.ConcurrencyConflict)
            {
                continue;
            }

            return result == AdminStoreResult.Succeeded
                ? new AdminIdentityResult(AdminIdentityResultCode.Succeeded)
                : new AdminIdentityResult(AdminIdentityResultCode.AuthenticationRequired);
        }

        return new AdminIdentityResult(AdminIdentityResultCode.ConcurrencyConflict);
    }

    public Task RecordCsrfFailureAsync(
        Guid? actorAdminUserId,
        CancellationToken cancellationToken = default)
    {
        return store.RecordCsrfFailureAsync(actorAdminUserId, cancellationToken);
    }

    private static string CreateSecurityStamp()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
