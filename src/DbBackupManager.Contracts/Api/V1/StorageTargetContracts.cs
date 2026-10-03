using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace DbBackupManager.Contracts.Api.V1;

public enum StorageProtocolValue { Smb = 1, Sftp = 2 }

public sealed class SaveStorageTargetRequest
{
    [StringLength(16)] public string? Version { get; init; }
    [Required, StringLength(200)] public string Name { get; init; } = string.Empty;
    [JsonRequired, EnumDataType(typeof(StorageProtocolValue))] public StorageProtocolValue Protocol { get; init; }
    [Required, StringLength(255)] public string Host { get; init; } = string.Empty;
    [Range(1, 65535)] public int? Port { get; init; }
    [Required, StringLength(2048)] public string BasePath { get; init; } = string.Empty;
    [JsonRequired] public Guid CredentialId { get; init; }
    [StringLength(500)] public string? Fingerprint { get; init; }
    [JsonRequired] public bool IsEnabled { get; init; }
}

public sealed class StorageTargetVersionRequest
{
    [Required, StringLength(16)] public string Version { get; init; } = string.Empty;
}

public sealed class SetStorageTargetEnabledRequest
{
    [Required, StringLength(16)] public string Version { get; init; } = string.Empty;
    [JsonRequired] public bool IsEnabled { get; init; }
}

public sealed record StorageTargetSettingsResponse(
    string Name, StorageProtocolValue Protocol, string Host, int? Port, string BasePath,
    Guid CredentialId, string? Fingerprint, bool IsEnabled);

public sealed record StorageTargetResponse(Guid Id, StorageTargetSettingsResponse Settings, string Version);
