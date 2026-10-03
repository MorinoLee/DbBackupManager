using DbBackupManager.Application.Identity;

namespace DbBackupManager.Application.SqlCredentials;

public enum SqlCredentialResultCode
{
    Succeeded,
    AuthenticationRequired,
    ValidationFailed,
    NotFound,
    Conflict,
    ProtectionUnavailable,
    Unavailable,
}

public sealed record SqlCredentialItem(
    Guid Id, string Name, string Username, bool IsEnabled, string Version);

public sealed record SqlCredentialResult<T>(SqlCredentialResultCode Code, T? Value = default);

public interface ISqlCredentialService
{
    Task<SqlCredentialResult<IReadOnlyList<SqlCredentialItem>>> ListAsync(
        AdminSession actor, CancellationToken cancellationToken = default);

    Task<SqlCredentialResult<SqlCredentialItem>> CreateAsync(
        AdminSession actor, string name, string username, string password,
        CancellationToken cancellationToken = default);

    Task<SqlCredentialResult<SqlCredentialItem>> RotatePasswordAsync(
        AdminSession actor, Guid id, string version, string password,
        CancellationToken cancellationToken = default);

    Task<SqlCredentialResult<SqlCredentialItem>> SetEnabledAsync(
        AdminSession actor, Guid id, string version, bool isEnabled,
        CancellationToken cancellationToken = default);
}
