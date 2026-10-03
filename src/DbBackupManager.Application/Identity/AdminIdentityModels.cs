namespace DbBackupManager.Application.Identity;

public enum AdminIdentityResultCode
{
    Succeeded,
    ValidationFailed,
    InvalidCredentials,
    SetupUnavailable,
    AuthenticationRequired,
    ConcurrencyConflict,
}

public sealed record AdminSession(Guid AdminUserId, string Username, string SecurityStamp);

public sealed record AdminIdentityResult(
    AdminIdentityResultCode Code,
    AdminSession? Session = null,
    IReadOnlyList<CredentialValidationFailure>? ValidationFailures = null)
{
    public bool IsSucceeded => Code == AdminIdentityResultCode.Succeeded;
}

public enum PasswordVerificationStatus
{
    Failed,
    Succeeded,
    SucceededRehashNeeded,
}

public sealed record AdminCredentialSnapshot(
    Guid Id,
    string Username,
    string NormalizedUsername,
    string PasswordHash,
    string SecurityStamp,
    bool IsEnabled,
    int FailedLoginCount,
    DateTimeOffset? FailedLoginWindowStartedAtUtc,
    DateTimeOffset? LockoutEndUtc,
    byte[] RowVersion)
{
    public bool IsLockedOut(DateTimeOffset utcNow)
    {
        return LockoutEndUtc is not null && LockoutEndUtc > utcNow;
    }
}

public sealed record NewAdminCredential(
    Guid Id,
    string Username,
    string NormalizedUsername,
    string PasswordHash,
    string SecurityStamp);

public enum AdminStoreResult
{
    Succeeded,
    SetupUnavailable,
    NotFound,
    ConcurrencyConflict,
}
