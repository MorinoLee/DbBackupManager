using DbBackupManager.Application.Identity;

namespace DbBackupManager.Application.FileCredentials;

public enum FileCredentialKind { SmbPassword = 2, SftpPassword = 3, SftpPrivateKey = 4 }

public enum FileCredentialResultCode
{
    Succeeded,
    AuthenticationRequired,
    ValidationFailed,
    NotFound,
    Conflict,
    ProtectionUnavailable,
    Unavailable,
}

public sealed record FileCredentialItem(
    Guid Id, string Name, string Username, FileCredentialKind Kind, bool IsEnabled, string Version,
    bool HasPassphrase = false);

public sealed record FileCredentialResult<T>(FileCredentialResultCode Code, T? Value = default);

public interface IFileCredentialService
{
    Task<FileCredentialResult<IReadOnlyList<FileCredentialItem>>> ListAsync(
        AdminSession actor, CancellationToken cancellationToken = default);

    Task<FileCredentialResult<FileCredentialItem>> CreateAsync(
        AdminSession actor, string name, string username, FileCredentialKind kind, string password,
        CancellationToken cancellationToken = default);

    Task<FileCredentialResult<FileCredentialItem>> RotatePasswordAsync(
        AdminSession actor, Guid id, string version, string password,
        CancellationToken cancellationToken = default);

    Task<FileCredentialResult<FileCredentialItem>> SetEnabledAsync(
        AdminSession actor, Guid id, string version, bool isEnabled,
        CancellationToken cancellationToken = default);

    Task<FileCredentialResult<FileCredentialItem>> CreatePrivateKeyAsync(
        AdminSession actor, string name, string username, string privateKey, string? passphrase,
        CancellationToken cancellationToken = default);

    Task<FileCredentialResult<FileCredentialItem>> RotatePrivateKeyAsync(
        AdminSession actor, Guid id, string version, string privateKey, string? passphrase,
        CancellationToken cancellationToken = default);
}
