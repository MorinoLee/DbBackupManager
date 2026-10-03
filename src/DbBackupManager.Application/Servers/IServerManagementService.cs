using DbBackupManager.Application.Identity;

namespace DbBackupManager.Application.Servers;

public enum ManagementCode { Succeeded, AuthenticationRequired, ValidationFailed, NotFound, Conflict, Unavailable, TargetFailed }
public sealed record ManagementResult<T>(ManagementCode Code, T? Value = default, string? TargetError = null);
public sealed record ServerInput(string Name, string LocalBackupRootPath, int Protocol, string Host,
    int? Port, string BasePath, Guid CredentialId, string? Fingerprint, string? Description, bool IsEnabled);
public sealed record ServerItem(Guid Id, ServerInput Settings, string Version);
public sealed record InstanceInput(Guid ServerId, string Name, string ConnectionAddress, Guid SqlCredentialId,
    bool TrustServerCertificate, string? CertificateTrustReason, int ConnectionTimeoutSeconds, bool IsEnabled,
    bool AllowLegacyTls = false, string? LegacyTlsReason = null);
public sealed record InstanceItem(Guid Id, InstanceInput Settings, string Version, string ConnectionStatus,
    string? ProductVersion, string? Edition, DateTimeOffset? LastCheckedAtUtc, string? ErrorCode);
public sealed record DatabaseItem(Guid Id, Guid InstanceId, string Name, bool IsSystemDatabase, bool IsAvailable,
    bool IsManaged, string? State, string? RecoveryModel, DateTimeOffset LastDiscoveredAtUtc, string Version);
public sealed record ServerInventory(IReadOnlyList<ServerItem> Servers, IReadOnlyList<InstanceItem> Instances,
    IReadOnlyList<DatabaseItem> Databases);

public interface IServerManagementService
{
    Task<ManagementResult<ServerInventory>> ListAsync(AdminSession actor, CancellationToken cancellationToken = default);
    Task<ManagementResult<ServerItem>> SaveServerAsync(AdminSession actor, Guid? id, string? version,
        ServerInput input, CancellationToken cancellationToken = default);
    Task<ManagementResult<InstanceItem>> SaveInstanceAsync(AdminSession actor, Guid? id, string? version,
        InstanceInput input, CancellationToken cancellationToken = default);
    Task<ManagementResult<InstanceItem>> ProbeAsync(AdminSession actor, Guid id, string version, bool discover,
        CancellationToken cancellationToken = default);
    Task<ManagementResult<DatabaseItem>> SetManagedAsync(AdminSession actor, Guid id, string version, bool managed,
        CancellationToken cancellationToken = default);
}
