using DbBackupManager.Application.Identity;
using DbBackupManager.Application.Servers;

namespace DbBackupManager.Application.StorageTargets;

public sealed record StorageTargetInput(
    string Name,
    int Protocol,
    string Host,
    int? Port,
    string BasePath,
    Guid CredentialId,
    string? Fingerprint,
    bool IsEnabled);

public sealed record StorageTargetItem(Guid Id, StorageTargetInput Settings, string Version);

public interface IStorageTargetManagementService
{
    Task<ManagementResult<IReadOnlyList<StorageTargetItem>>> ListAsync(
        AdminSession actor, CancellationToken cancellationToken = default);

    Task<ManagementResult<StorageTargetItem>> SaveAsync(
        AdminSession actor, Guid? id, string? version, StorageTargetInput input,
        CancellationToken cancellationToken = default);

    Task<ManagementResult<StorageTargetItem>> SetEnabledAsync(
        AdminSession actor, Guid id, string version, bool isEnabled,
        CancellationToken cancellationToken = default);

    Task<ManagementResult<StorageTargetItem>> TestConnectionAsync(
        AdminSession actor, Guid id, string version,
        CancellationToken cancellationToken = default);
}
