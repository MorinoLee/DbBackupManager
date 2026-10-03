using System.Data;
using System.Data.Common;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.Servers;
using DbBackupManager.Application.StorageTargets;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.StorageTargets;

public static class StorageTargetManagementRegistration
{
    public static IServiceCollection AddStorageTargetManagement(this IServiceCollection services) =>
        services.AddScoped<IStorageTargetManagementService, StorageTargetManagementService>();
}

internal sealed class StorageTargetManagementService(
    IDbContextFactory<PlatformDbContext> factory,
    IBackupFileStorageProbe probe) : IStorageTargetManagementService
{
    private const string ProbeFileName = "DbBackupManager.connection-probe";

    public Task<ManagementResult<IReadOnlyList<StorageTargetItem>>> ListAsync(
        AdminSession actor, CancellationToken cancellationToken = default) =>
        GuardAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await AuthorizeAsync(db, actor, cancellationToken);
            var rows = await db.StorageTargets.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
            return new ManagementResult<IReadOnlyList<StorageTargetItem>>(
                ManagementCode.Succeeded, rows.Select(Map).ToArray());
        });

    public Task<ManagementResult<StorageTargetItem>> SaveAsync(
        AdminSession actor, Guid? id, string? version, StorageTargetInput input,
        CancellationToken cancellationToken = default) =>
        MutateAsync(actor, async db =>
        {
            RequireSafe(input.Name, input.Host, input.BasePath, input.Fingerprint);
            var protocol = (FileTransferProtocol)input.Protocol;
            if (!Enum.IsDefined(protocol)) Reject(ManagementCode.ValidationFailed);
            if (protocol == FileTransferProtocol.Sftp && !IsSha256Fingerprint(input.Fingerprint))
                Reject(ManagementCode.ValidationFailed);
            var kind = await db.CredentialReferences
                .Where(x => x.Id == input.CredentialId && (x.IsEnabled || !input.IsEnabled))
                .Select(x => (CredentialKind?)x.Kind).SingleOrDefaultAsync(cancellationToken);
            if (!IsMatchingCredential(protocol, kind)) Reject(ManagementCode.ValidationFailed);
            var endpoint = new FileEndpointSettings(
                protocol, input.Host, input.Port, input.BasePath, input.CredentialId, input.Fingerprint);
            StorageTarget entity;
            if (id is null)
            {
                entity = new(Guid.NewGuid(), input.Name, endpoint, input.IsEnabled);
                db.StorageTargets.Add(entity);
            }
            else
            {
                entity = await db.StorageTargets.AsTracking()
                    .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                    ?? throw new Rejected(ManagementCode.NotFound);
                CheckVersion(entity, version);
                entity.Rename(input.Name);
                entity.UpdateEndpoint(endpoint);
                entity.SetEnabled(input.IsEnabled);
            }
            Audit(db, actor, id is null ? "storage_target.create" : "storage_target.update", entity.Id);
            await db.SaveChangesAsync(cancellationToken);
            return Map(entity);
        }, cancellationToken);

    public Task<ManagementResult<StorageTargetItem>> SetEnabledAsync(
        AdminSession actor, Guid id, string version, bool isEnabled,
        CancellationToken cancellationToken = default) =>
        MutateAsync(actor, async db =>
        {
            var entity = await db.StorageTargets.AsTracking()
                .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new Rejected(ManagementCode.NotFound);
            CheckVersion(entity, version);
            if (isEnabled)
            {
                var kind = await db.CredentialReferences.Where(x => x.Id == entity.CredentialReferenceId)
                    .Select(x => (CredentialKind?)x.Kind).SingleOrDefaultAsync(cancellationToken);
                if (!IsMatchingCredential(entity.Protocol, kind)) Reject(ManagementCode.ValidationFailed);
                var enabled = await db.CredentialReferences.AnyAsync(
                    x => x.Id == entity.CredentialReferenceId && x.IsEnabled, cancellationToken);
                if (!enabled) Reject(ManagementCode.ValidationFailed);
            }
            entity.SetEnabled(isEnabled);
            Audit(db, actor, isEnabled ? "storage_target.enable" : "storage_target.disable", entity.Id);
            await db.SaveChangesAsync(cancellationToken);
            return Map(entity);
        }, cancellationToken);

    public Task<ManagementResult<StorageTargetItem>> TestConnectionAsync(
        AdminSession actor, Guid id, string version, CancellationToken cancellationToken = default) =>
        GuardAsync(async () =>
        {
            StorageTargetItem snapshot;
            string credentialVersion;
            BackupFileEndpointInput endpoint;
            await using (var db = await factory.CreateDbContextAsync(cancellationToken))
            {
                await AuthorizeAsync(db, actor, cancellationToken);
                var entity = await db.StorageTargets.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                    ?? throw new Rejected(ManagementCode.NotFound);
                CheckVersion(entity, version);
                var credential = await db.CredentialReferences.Where(x => x.Id == entity.CredentialReferenceId)
                    .Select(x => new { x.Kind, x.IsEnabled, x.RowVersion }).SingleAsync(cancellationToken);
                if (!entity.IsEnabled || !credential.IsEnabled || !IsMatchingCredential(entity.Protocol, credential.Kind))
                    Reject(ManagementCode.ValidationFailed);
                snapshot = Map(entity);
                credentialVersion = Convert.ToBase64String(credential.RowVersion);
                endpoint = new BackupFileEndpointInput(
                    entity.Protocol, entity.Host, entity.Port, entity.BasePath,
                    entity.CredentialReferenceId, entity.SftpHostKeyFingerprint);
            }

            var inspected = await probe.InspectAsync(
                endpoint, endpoint.ChildFile(ProbeFileName), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var failure = inspected.IsSucceeded ? null : inspected.Failure;
            var saved = await MutateAsync(actor, async db =>
            {
                var entity = await db.StorageTargets.AsTracking()
                    .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                    ?? throw new Rejected(ManagementCode.NotFound);
                CheckVersion(entity, snapshot.Version);
                var currentCredentialVersion = await db.CredentialReferences
                    .Where(x => x.Id == snapshot.Settings.CredentialId)
                    .Select(x => x.RowVersion).SingleAsync(cancellationToken);
                if (Convert.ToBase64String(currentCredentialVersion) != credentialVersion)
                    Reject(ManagementCode.Conflict);
                Audit(db, actor, "storage_target.probe", entity.Id, failure?.Code.ToString());
                await db.SaveChangesAsync(cancellationToken);
                return Map(entity);
            }, cancellationToken);
            return saved.Code == ManagementCode.Succeeded && failure is not null
                ? new(ManagementCode.TargetFailed, saved.Value, failure.Code.ToString())
                : saved;
        });

    private async Task<ManagementResult<T>> MutateAsync<T>(
        AdminSession actor, Func<PlatformDbContext, Task<T>> action, CancellationToken cancellationToken) =>
        await GuardAsync(async () =>
        {
            await using var strategy = await factory.CreateDbContextAsync(cancellationToken);
            return await strategy.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                await using var tx = await db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken);
                await AuthorizeAsync(db, actor, cancellationToken);
                var result = await action(db);
                await tx.CommitAsync(cancellationToken);
                return new ManagementResult<T>(ManagementCode.Succeeded, result);
            });
        });

    private static async Task<ManagementResult<T>> GuardAsync<T>(Func<Task<ManagementResult<T>>> action)
    {
        try { return await action(); }
        catch (Rejected e) { return new(e.Code); }
        catch (ArgumentException) { return new(ManagementCode.ValidationFailed); }
        catch (DbUpdateConcurrencyException) { return new(ManagementCode.Conflict); }
        catch (DbUpdateException e) when (e.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        { return new(ManagementCode.Conflict); }
        catch (Exception e) when (e is DbException or DbUpdateException or InvalidOperationException)
        { return new(ManagementCode.Unavailable); }
    }

    private static async Task AuthorizeAsync(PlatformDbContext db, AdminSession actor, CancellationToken token)
    {
        var admin = await db.AdminUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actor.AdminUserId, token);
        if (admin is not { IsEnabled: true } || admin.IsLockedOut(DateTimeOffset.UtcNow)
            || !string.Equals(admin.SecurityStamp, actor.SecurityStamp, StringComparison.Ordinal))
            Reject(ManagementCode.AuthenticationRequired);
    }

    private static void RequireSafe(params string?[] values)
    {
        if (values.Any(x => x?.Any(char.IsControl) == true)) Reject(ManagementCode.ValidationFailed);
    }

    private static void CheckVersion(ConcurrentEntity entity, string? version)
    {
        byte[] expected;
        try { expected = Convert.FromBase64String(version is { Length: <= 16 } ? version : ""); }
        catch (FormatException) { throw new Rejected(ManagementCode.ValidationFailed); }
        if (expected.Length != 8) Reject(ManagementCode.ValidationFailed);
        if (!entity.RowVersion.AsSpan().SequenceEqual(expected)) Reject(ManagementCode.Conflict);
    }

    private static void Audit(PlatformDbContext db, AdminSession actor, string action, Guid id, string? error = null) =>
        db.AuditRecords.Add(new AuditRecord(
            actor.AdminUserId, action, "StorageTarget", id.ToString("N"),
            error is null ? "Succeeded" : "Failed", error));

    private static bool IsMatchingCredential(FileTransferProtocol protocol, CredentialKind? kind) => protocol switch
    {
        FileTransferProtocol.Smb => kind == CredentialKind.SmbPassword,
        FileTransferProtocol.Sftp => kind is CredentialKind.SftpPassword or CredentialKind.SftpPrivateKey,
        _ => false,
    };

    private static bool IsSha256Fingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("SHA256:", StringComparison.Ordinal))
            return false;
        try
        {
            var encoded = value["SHA256:".Length..].TrimEnd('=');
            return Convert.FromBase64String(encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=')).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void Reject(ManagementCode code) => throw new Rejected(code);
    private sealed class Rejected(ManagementCode code) : Exception { public ManagementCode Code { get; } = code; }

    private static StorageTargetItem Map(StorageTarget x) => new(x.Id,
        new(x.Name, (int)x.Protocol, x.Host, x.Port, x.BasePath, x.CredentialReferenceId,
            x.SftpHostKeyFingerprint, x.IsEnabled),
        Convert.ToBase64String(x.RowVersion));
}
