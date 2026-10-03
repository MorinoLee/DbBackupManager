using System.Data;
using System.Data.Common;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.Servers;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Servers;

public static class ServerManagementRegistration
{
    public static IServiceCollection AddServerManagement(this IServiceCollection services) =>
        services.AddScoped<IServerManagementService, ServerManagementService>();
}

internal sealed class ServerManagementService(IDbContextFactory<PlatformDbContext> factory,
    ITargetSqlReadOnlyProbe probe) : IServerManagementService
{
    public Task<ManagementResult<ServerInventory>> ListAsync(AdminSession actor, CancellationToken cancellationToken = default) =>
        GuardAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await AuthorizeAsync(db, actor, cancellationToken);
            var servers = await db.DatabaseServers.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
            var instances = await db.DatabaseInstances.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
            var databases = await db.ManagedDatabases.AsNoTracking().OrderBy(x => x.DatabaseName).ToListAsync(cancellationToken);
            return new ManagementResult<ServerInventory>(ManagementCode.Succeeded,
                new(servers.Select(Map).ToArray(), instances.Select(Map).ToArray(), databases.Select(Map).ToArray()));
        });

    public Task<ManagementResult<ServerItem>> SaveServerAsync(AdminSession actor, Guid? id, string? version,
        ServerInput input, CancellationToken cancellationToken = default) =>
        MutateAsync(actor, async db =>
        {
            RequireSafe(input.Name, input.LocalBackupRootPath, input.Host, input.BasePath, input.Description, input.Fingerprint);
            // Windows-first：SQL 目录必须是目标服务器的盘符绝对路径。
            var path = input.LocalBackupRootPath.Trim();
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
                || path[2..].IndexOfAny([':', '*', '?', '"', '<', '>', '|', '/']) >= 0
                || path.Split('\\').Any(x => x is "." or "..")) Reject(ManagementCode.ValidationFailed);
            var kind = await db.CredentialReferences.Where(x => x.Id == input.CredentialId && (x.IsEnabled || !input.IsEnabled))
                .Select(x => (CredentialKind?)x.Kind).SingleOrDefaultAsync(cancellationToken);
            if (!(input.Protocol == 1 && kind == CredentialKind.SmbPassword
                || input.Protocol == 2 && kind == CredentialKind.SftpPassword)) Reject(ManagementCode.ValidationFailed);
            var endpoint = new FileEndpointSettings((FileTransferProtocol)input.Protocol, input.Host, input.Port,
                input.BasePath, input.CredentialId, input.Fingerprint);
            DatabaseServer entity;
            if (id is null)
            {
                entity = new(Guid.NewGuid(), input.Name, path, endpoint, input.Description, input.IsEnabled);
                db.DatabaseServers.Add(entity);
            }
            else
            {
                entity = await db.DatabaseServers.AsTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                    ?? throw new Rejected(ManagementCode.NotFound);
                CheckVersion(entity, version);
                entity.Update(input.Name, path, endpoint, input.Description);
                entity.SetEnabled(input.IsEnabled);
            }
            Audit(db, actor, id is null ? "server.create" : "server.update", "DatabaseServer", entity.Id);
            await db.SaveChangesAsync(cancellationToken);
            return Map(entity);
        }, cancellationToken);

    public Task<ManagementResult<InstanceItem>> SaveInstanceAsync(AdminSession actor, Guid? id, string? version,
        InstanceInput input, CancellationToken cancellationToken = default) =>
        MutateAsync(actor, async db =>
        {
            RequireSafe(input.Name);
            var connection = new TargetSqlConnectionInput(input.ConnectionAddress, input.SqlCredentialId, true,
                input.TrustServerCertificate, input.CertificateTrustReason, input.ConnectionTimeoutSeconds, input.AllowLegacyTls, input.LegacyTlsReason);
            if (!await db.DatabaseServers.AnyAsync(x => x.Id == input.ServerId && (x.IsEnabled || !input.IsEnabled), cancellationToken)
                || !await db.CredentialReferences.AnyAsync(x => x.Id == input.SqlCredentialId && (x.IsEnabled || !input.IsEnabled)
                    && x.Kind == CredentialKind.SqlPassword, cancellationToken)) Reject(ManagementCode.ValidationFailed);
            DatabaseInstance entity;
            if (id is null)
            {
                entity = new(Guid.NewGuid(), input.ServerId, input.Name, connection.ConnectionAddress,
                    input.SqlCredentialId, true, input.TrustServerCertificate, input.CertificateTrustReason,
                    input.ConnectionTimeoutSeconds, input.IsEnabled, input.AllowLegacyTls, input.LegacyTlsReason);
                db.DatabaseInstances.Add(entity);
            }
            else
            {
                entity = await db.DatabaseInstances.AsTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                    ?? throw new Rejected(ManagementCode.NotFound);
                CheckVersion(entity, version);
                if (entity.ServerId != input.ServerId) Reject(ManagementCode.ValidationFailed);
                entity.Rename(input.Name);
                entity.UpdateConnection(connection.ConnectionAddress, input.SqlCredentialId, true,
                    input.TrustServerCertificate, input.CertificateTrustReason, input.ConnectionTimeoutSeconds, input.AllowLegacyTls, input.LegacyTlsReason);
                entity.SetEnabled(input.IsEnabled);
                // 修改连接后旧发现记录不再是当前端点的可用性证据。
                foreach (var row in await db.ManagedDatabases.AsTracking().Where(x => x.InstanceId == entity.Id).ToListAsync(cancellationToken))
                {
                    row.RefreshDiscovery(row.LastDiscoveredAtUtc, row.IsSystemDatabase, false, row.RecoveryModel, "ConfigurationChanged");
                    row.SetManaged(false);
                }
            }
            Audit(db, actor, id is null ? "instance.create" : "instance.update", "DatabaseInstance", entity.Id);
            Audit(db, actor, input.AllowLegacyTls ? "instance.legacy_tls.enabled" : "instance.legacy_tls.disabled", "DatabaseInstance", entity.Id);
            if (input.TrustServerCertificate) Audit(db, actor, "instance.certificate_trust_exception", "DatabaseInstance", entity.Id);
            await db.SaveChangesAsync(cancellationToken);
            return Map(entity);
        }, cancellationToken);

    public Task<ManagementResult<InstanceItem>> ProbeAsync(AdminSession actor, Guid id, string version, bool discover,
        CancellationToken cancellationToken = default) => GuardAsync(async () =>
    {
        ProbeSnapshot snapshot;
        // 此块结束后再调用外部适配器，禁止跨 I/O 保留 Platform Context。
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
        {
            await AuthorizeAsync(db, actor, cancellationToken);
            var instance = await db.DatabaseInstances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new Rejected(ManagementCode.NotFound);
            CheckVersion(instance, version);
            var server = await db.DatabaseServers.AsNoTracking().SingleAsync(x => x.Id == instance.ServerId, cancellationToken);
            var credential = await db.CredentialReferences.Where(x => x.Id == instance.SqlCredentialReferenceId)
                .Select(x => new { x.Kind, x.IsEnabled, x.RowVersion }).SingleAsync(cancellationToken);
            if (!instance.IsEnabled || !server.IsEnabled || !credential.IsEnabled || credential.Kind != CredentialKind.SqlPassword)
                Reject(ManagementCode.ValidationFailed);
            snapshot = new(Map(instance), Convert.ToBase64String(server.RowVersion), Convert.ToBase64String(credential.RowVersion));
        }
        var settings = snapshot.Instance.Settings;
        var input = new TargetSqlConnectionInput(settings.ConnectionAddress, settings.SqlCredentialId, true,
            settings.TrustServerCertificate, settings.CertificateTrustReason, settings.ConnectionTimeoutSeconds, settings.AllowLegacyTls, settings.LegacyTlsReason);
        var serverResult = await probe.ProbeServerAsync(input, cancellationToken);
        TargetSqlResult<TargetSqlDatabaseCatalog>? catalogResult = null;
        if (discover && serverResult.IsSucceeded)
            catalogResult = await probe.DiscoverDatabasesAsync(input, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var failure = serverResult.Failure ?? catalogResult?.Failure;
        var now = DateTimeOffset.UtcNow;
        var saved = await MutateAsync(actor, async db =>
        {
            var instance = await db.DatabaseInstances.AsTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new Rejected(ManagementCode.NotFound);
            CheckVersion(instance, snapshot.Instance.Version);
            var server = await db.DatabaseServers.AsNoTracking().SingleAsync(x => x.Id == settings.ServerId, cancellationToken);
            CheckVersion(server, snapshot.ServerVersion);
            var credentialVersion = await db.CredentialReferences.Where(x => x.Id == settings.SqlCredentialId)
                .Select(x => x.RowVersion).SingleAsync(cancellationToken);
            if (Convert.ToBase64String(credentialVersion) != snapshot.CredentialVersion) Reject(ManagementCode.Conflict);
            if (failure is null)
            {
                var info = serverResult.Value!;
                instance.RecordConnectionSucceeded(now, info.ProductVersion, info.ProductLevel, info.Edition);
                if (catalogResult is not null)
                    await ApplyDiscoveryAsync(db, id, catalogResult.Value!.Databases, now, cancellationToken);
            }
            else instance.RecordConnectionFailed(now, failure.Code.ToString());
            Audit(db, actor, discover ? "instance.discover" : "instance.probe", "DatabaseInstance", id,
                failure?.Code.ToString());
            await db.SaveChangesAsync(cancellationToken);
            return Map(instance);
        }, cancellationToken);
        return saved.Code == ManagementCode.Succeeded && failure is not null
            ? new(ManagementCode.TargetFailed, saved.Value, failure.Code.ToString()) : saved;
    });

    public Task<ManagementResult<DatabaseItem>> SetManagedAsync(AdminSession actor, Guid id, string version, bool managed,
        CancellationToken cancellationToken = default) => MutateAsync(actor, async db =>
    {
        var entity = await db.ManagedDatabases.AsTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new Rejected(ManagementCode.NotFound);
        CheckVersion(entity, version);
        if (managed && (!entity.IsAvailable || entity.IsSystemDatabase
            || !await (from i in db.DatabaseInstances
                       join s in db.DatabaseServers on i.ServerId equals s.Id
                       join c in db.CredentialReferences on i.SqlCredentialReferenceId equals c.Id
                       where i.Id == entity.InstanceId && i.IsEnabled && s.IsEnabled && c.IsEnabled && i.ConnectionStatus == SqlConnectionStatus.Connected
                       select i.Id).AnyAsync(cancellationToken))) Reject(ManagementCode.ValidationFailed);
        entity.SetManaged(managed);
        Audit(db, actor, managed ? "database.manage" : "database.unmanage", "ManagedDatabase", entity.Id);
        await db.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }, cancellationToken);

    private static async Task ApplyDiscoveryAsync(PlatformDbContext db, Guid instanceId,
        IReadOnlyList<TargetSqlDatabaseInfo> catalog, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (catalog.Select(x => x.Name.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count() != catalog.Count)
            Reject(ManagementCode.Conflict);
        var existing = await db.ManagedDatabases.AsTracking().Where(x => x.InstanceId == instanceId).ToListAsync(cancellationToken);
        var remaining = existing.ToDictionary(x => x.NormalizedDatabaseName, StringComparer.Ordinal);
        foreach (var discovered in catalog)
        {
            // 不合并大小写不同的目标库，也不把快照或系统库标为可纳管。
            var available = discovered.CanBeManaged && !discovered.IsSnapshot && !discovered.IsSystemDatabase
                && discovered.State == TargetSqlDatabaseState.Online;
            var state = discovered.IsSnapshot ? "Snapshot" : discovered.State.ToString();
            if (remaining.Remove(discovered.Name.Trim().ToUpperInvariant(), out var entity))
            {
                if (!string.Equals(entity.DatabaseName, discovered.Name, StringComparison.Ordinal)) Reject(ManagementCode.Conflict);
                entity.RefreshDiscovery(now, discovered.IsSystemDatabase, available, discovered.RecoveryModel.ToString(), state);
                if (discovered.IsSnapshot) entity.SetManaged(false);
            }
            else db.ManagedDatabases.Add(new(Guid.NewGuid(), instanceId, discovered.Name, discovered.IsSystemDatabase,
                available, now, discovered.RecoveryModel.ToString(), state));
        }
        foreach (var entity in remaining.Values)
            entity.RefreshDiscovery(entity.LastDiscoveredAtUtc, entity.IsSystemDatabase, false, entity.RecoveryModel, "NotDiscovered");
    }

    private async Task<ManagementResult<T>> MutateAsync<T>(AdminSession actor, Func<PlatformDbContext, Task<T>> action,
        CancellationToken cancellationToken) => await GuardAsync(async () =>
    {
        await using var strategy = await factory.CreateDbContextAsync(cancellationToken);
        return await strategy.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
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
            || !string.Equals(admin.SecurityStamp, actor.SecurityStamp, StringComparison.Ordinal)) Reject(ManagementCode.AuthenticationRequired);
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
    private static void Audit(PlatformDbContext db, AdminSession actor, string action, string type, Guid id, string? error = null) =>
        db.AuditRecords.Add(new AuditRecord(actor.AdminUserId, action, type, id.ToString("N"), error is null ? "Succeeded" : "Failed", error));
    private static void Reject(ManagementCode code) => throw new Rejected(code);
    private sealed class Rejected(ManagementCode code) : Exception { public ManagementCode Code { get; } = code; }
    private sealed record ProbeSnapshot(InstanceItem Instance, string ServerVersion, string CredentialVersion);
    private static ServerItem Map(DatabaseServer x) => new(x.Id,
        new(x.Name, x.LocalBackupRootPath, (int)x.StagingAccessProtocol, x.StagingAccessHost, x.StagingAccessPort,
            x.StagingAccessBasePath, x.StagingCredentialReferenceId, x.StagingSftpHostKeyFingerprint, x.Description, x.IsEnabled),
        Convert.ToBase64String(x.RowVersion));
    private static InstanceItem Map(DatabaseInstance x) => new(x.Id,
        new(x.ServerId, x.Name, x.ConnectionAddress, x.SqlCredentialReferenceId, x.TrustServerCertificate,
            x.CertificateTrustReason, x.ConnectionTimeoutSeconds, x.IsEnabled, x.AllowLegacyTls, x.LegacyTlsReason), Convert.ToBase64String(x.RowVersion),
        x.ConnectionStatus.ToString(), x.ProductVersion, x.Edition, x.LastConnectionCheckedAtUtc, x.LastConnectionErrorCode);
    private static DatabaseItem Map(ManagedDatabase x) => new(x.Id, x.InstanceId, x.DatabaseName, x.IsSystemDatabase,
        x.IsAvailable, x.IsManaged, x.StateDescription, x.RecoveryModel, x.LastDiscoveredAtUtc, Convert.ToBase64String(x.RowVersion));
}
