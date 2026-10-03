using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace DbBackupManager.Contracts.Api.V1;

public sealed class SaveServerRequest
{
    [StringLength(16)] public string? Version { get; init; }
    [Required, StringLength(200)] public string Name { get; init; } = "";
    [Required, StringLength(2048)] public string LocalBackupRootPath { get; init; } = "";
    [JsonRequired, Range(1, 2)] public int Protocol { get; init; }
    [Required, StringLength(255)] public string Host { get; init; } = "";
    [Range(1, 65535)] public int? Port { get; init; }
    [Required, StringLength(2048)] public string BasePath { get; init; } = "";
    [JsonRequired] public Guid CredentialId { get; init; }
    [StringLength(500)] public string? Fingerprint { get; init; }
    [StringLength(1000)] public string? Description { get; init; }
    [JsonRequired] public bool IsEnabled { get; init; }
}
public sealed class SaveInstanceRequest
{
    [StringLength(16)] public string? Version { get; init; }
    [JsonRequired] public Guid ServerId { get; init; }
    [Required, StringLength(200)] public string Name { get; init; } = "";
    [Required, StringLength(255)] public string ConnectionAddress { get; init; } = "";
    [JsonRequired] public Guid SqlCredentialId { get; init; }
    [JsonRequired] public bool TrustServerCertificate { get; init; }
    public bool AllowLegacyTls { get; init; }
    [StringLength(500)] public string? LegacyTlsReason { get; init; }
    [StringLength(500)] public string? CertificateTrustReason { get; init; }
    [JsonRequired, Range(1, 300)] public int ConnectionTimeoutSeconds { get; init; }
    [JsonRequired] public bool IsEnabled { get; init; }
}
public sealed class InstanceProbeRequest
{
    [Required, StringLength(16)] public string Version { get; init; } = "";
}
public sealed class SetDatabaseManagedRequest
{
    [Required, StringLength(16)] public string Version { get; init; } = "";
    [JsonRequired] public bool IsManaged { get; init; }
}

public sealed record ServerSettingsResponse(string Name, string LocalBackupRootPath, int Protocol, string Host,
    int? Port, string BasePath, Guid CredentialId, string? Fingerprint, string? Description, bool IsEnabled);
public sealed record ServerResponse(Guid Id, ServerSettingsResponse Settings, string Version);
public sealed record InstanceSettingsResponse(Guid ServerId, string Name, string ConnectionAddress, Guid SqlCredentialId,
    bool TrustServerCertificate, string? CertificateTrustReason, int ConnectionTimeoutSeconds, bool IsEnabled,
    bool AllowLegacyTls = false, string? LegacyTlsReason = null);
public sealed record InstanceResponse(Guid Id, InstanceSettingsResponse Settings, string Version, string ConnectionStatus,
    string? ProductVersion, string? Edition, DateTimeOffset? LastCheckedAtUtc, string? ErrorCode);
public sealed record DatabaseResponse(Guid Id, Guid InstanceId, string Name, bool IsSystemDatabase, bool IsAvailable,
    bool IsManaged, string? State, string? RecoveryModel, DateTimeOffset LastDiscoveredAtUtc, string Version);
public sealed record ServerInventoryResponse(IReadOnlyList<ServerResponse> Servers, IReadOnlyList<InstanceResponse> Instances,
    IReadOnlyList<DatabaseResponse> Databases);
