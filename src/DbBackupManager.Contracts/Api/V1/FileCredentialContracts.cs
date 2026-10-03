using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace DbBackupManager.Contracts.Api.V1;

public enum FileCredentialKindValue { SmbPassword = 2, SftpPassword = 3, SftpPrivateKey = 4 }

public sealed class CreateFileCredentialRequest
{
    [JsonRequired, EnumDataType(typeof(FileCredentialKindValue))]
    public FileCredentialKindValue Kind { get; init; }

    [Required, StringLength(200)]
    public string Name { get; init; } = string.Empty;

    [Required, StringLength(256)]
    public string Username { get; init; } = string.Empty;

    [Required, StringLength(1024)]
    public string Password { get; init; } = string.Empty;
}

public sealed class RotateFileCredentialPasswordRequest
{
    [Required, StringLength(16)]
    public string Version { get; init; } = string.Empty;

    [Required, StringLength(1024)]
    public string Password { get; init; } = string.Empty;
}

public sealed class SetFileCredentialEnabledRequest
{
    [Required, StringLength(16)]
    public string Version { get; init; } = string.Empty;

    [JsonRequired]
    public bool IsEnabled { get; init; }
}

public sealed class CreateSftpPrivateKeyRequest
{
    [Required, StringLength(200)]
    public string Name { get; init; } = string.Empty;

    [Required, StringLength(256)]
    public string Username { get; init; } = string.Empty;

    [Required, StringLength(32768)]
    public string PrivateKey { get; init; } = string.Empty;

    [StringLength(1024)]
    public string? Passphrase { get; init; }
}

public sealed class RotateSftpPrivateKeyRequest
{
    [Required, StringLength(16)]
    public string Version { get; init; } = string.Empty;

    [Required, StringLength(32768)]
    public string PrivateKey { get; init; } = string.Empty;

    [StringLength(1024)]
    public string? Passphrase { get; init; }
}

public sealed record FileCredentialResponse(
    Guid Id, string Name, string Username, FileCredentialKindValue Kind, bool IsEnabled, string Version,
    bool HasPassphrase);
