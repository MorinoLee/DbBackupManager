using DbBackupManager.Application.Identity;
using DbBackupManager.Application.Servers;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.Servers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class ServerManagementServiceTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    private readonly FakeProbe _probe = new();
    private async Task<(AdminSession Actor, ServerItem Server, InstanceItem Instance)> SeedAsync(ServerManagementService service)
    {
        var id = Guid.NewGuid();
        var actor = new AdminSession(id, $"admin-{id:N}", $"stamp-{id:N}");
        var sqlId = Guid.NewGuid(); var fileId = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            db.AdminUsers.Add(new AdminUser(id, actor.Username, actor.Username.ToUpperInvariant(), "synthetic-hash", actor.SecurityStamp));
            db.CredentialReferences.Add(new(sqlId, $"sql-{id:N}", CredentialKind.SqlPassword, "synthetic-user", "synthetic-cipher", "synthetic-version"));
            db.CredentialReferences.Add(new(fileId, $"smb-{id:N}", CredentialKind.SmbPassword, "synthetic-user", "synthetic-cipher", "synthetic-version"));
            await db.SaveChangesAsync();
        }
        var server = await service.SaveServerAsync(actor, null, null,
            new($"server-{id:N}", @"D:\SyntheticStaging", 1, "synthetic-host", null, "synthetic-share", fileId, null, null, true));
        Assert.Equal(ManagementCode.Succeeded, server.Code);
        var instance = await service.SaveInstanceAsync(actor, null, null,
            new(server.Value!.Id, "default", $"synthetic-{id:N}", sqlId, false, null, 15, true));
        Assert.Equal(ManagementCode.Succeeded, instance.Code);
        return (actor, server.Value, instance.Value!);
    }

    [Fact]
    public async Task LegacyExceptionIsValidatedPersistedAuditedAndPassedToProbe()
    {
        using var provider = database.CreateServiceProvider();
        var service = new ServerManagementService(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(), _probe);
        var seed = await SeedAsync(service);
        var invalid = await service.SaveInstanceAsync(seed.Actor, seed.Instance.Id, seed.Instance.Version,
            seed.Instance.Settings with { AllowLegacyTls = true });
        Assert.Equal(ManagementCode.ValidationFailed, invalid.Code);
        var saved = await service.SaveInstanceAsync(seed.Actor, seed.Instance.Id, seed.Instance.Version,
            seed.Instance.Settings with { AllowLegacyTls = true, LegacyTlsReason = "legacy fixture" });
        Assert.Equal(ManagementCode.Succeeded, saved.Code);
        Assert.True(saved.Value!.Settings.AllowLegacyTls);
        var result = await service.ProbeAsync(seed.Actor, saved.Value.Id, saved.Value.Version, false);
        Assert.Equal(ManagementCode.Succeeded, result.Code);
        Assert.True(_probe.LastInput!.AllowLegacyTls);
        await using var db = database.CreateContext();
        Assert.True(await db.AuditRecords.AnyAsync(x => x.TargetId == seed.Instance.Id.ToString("N") && x.Action == "instance.legacy_tls.enabled"));
        var disabled = await service.SaveInstanceAsync(seed.Actor, result.Value!.Id, result.Value.Version,
            result.Value.Settings with { AllowLegacyTls = false, LegacyTlsReason = null });
        Assert.Equal(ManagementCode.Succeeded, disabled.Code);
        Assert.False(disabled.Value!.Settings.AllowLegacyTls);
    }

    [Fact]
    public async Task DiscoverDefaultsToUnmanagedExcludesUnsafeDatabasesAndPreservesMissingHistory()
    {
        using var provider = database.CreateServiceProvider();
        var tracking = new TrackingFactory(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>());
        var service = new ServerManagementService(tracking, _probe);
        var s = await SeedAsync(service);
        _probe.BeforeProbe = () =>
        {
            foreach (var context in tracking.Created)
                Assert.Throws<ObjectDisposedException>(() => context.ChangeTracker.Entries().ToArray());
            return Task.CompletedTask;
        };
        _probe.Catalog = [Db("business"), Db("master", system: true), Db("snapshot", snapshot: true), Db("offline", online: false)];
        var result = await service.ProbeAsync(s.Actor, s.Instance.Id, s.Instance.Version, true);
        Assert.Equal(ManagementCode.Succeeded, result.Code);
        var rows = (await service.ListAsync(s.Actor)).Value!.Databases.Where(x => x.InstanceId == s.Instance.Id).ToArray();
        Assert.Equal(4, rows.Length); Assert.All(rows, x => Assert.False(x.IsManaged));
        var business = rows.Single(x => x.Name == "business");
        var managed = await service.SetManagedAsync(s.Actor, business.Id, business.Version, true);
        Assert.True(managed.Value!.IsManaged);
        foreach (var row in rows.Where(x => x.Name != "business"))
            Assert.Equal(ManagementCode.ValidationFailed, (await service.SetManagedAsync(s.Actor, row.Id, row.Version, true)).Code);
        _probe.Catalog = [Db("business")];
        result = await service.ProbeAsync(s.Actor, s.Instance.Id, result.Value!.Version, true);
        Assert.Equal(ManagementCode.Succeeded, result.Code);
        rows = (await service.ListAsync(s.Actor)).Value!.Databases.Where(x => x.InstanceId == s.Instance.Id).ToArray();
        Assert.True(rows.Single(x => x.Name == "business").IsManaged);
        Assert.Equal(4, rows.Length);
        Assert.All(rows.Where(x => x.Name != "business"), x => Assert.False(x.IsAvailable));
        await using var verify = database.CreateContext();
        Assert.Equal(2, await verify.AuditRecords.CountAsync(x => x.TargetId == s.Instance.Id.ToString("N") && x.Action == "instance.discover"));
    }

    [Theory]
    [InlineData("instance", ManagementCode.Conflict)]
    [InlineData("server", ManagementCode.Conflict)]
    [InlineData("credential", ManagementCode.Conflict)]
    [InlineData("identity", ManagementCode.AuthenticationRequired)]
    public async Task ChangedConfigurationOrRevokedIdentityRejectsInFlightResults(string change, ManagementCode expected)
    {
        using var provider = database.CreateServiceProvider();
        var service = new ServerManagementService(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(), _probe);
        var s = await SeedAsync(service);
        _probe.BeforeProbe = async () =>
        {
            await using var db = database.CreateContext();
            switch (change)
            {
                case "instance": (await db.DatabaseInstances.FindAsync(s.Instance.Id))!.Rename("changed"); break;
                case "server": (await db.DatabaseServers.FindAsync(s.Server.Id))!.SetEnabled(false); break;
                case "credential": (await db.CredentialReferences.FindAsync(s.Instance.Settings.SqlCredentialId))!.SetEnabled(false); break;
                case "identity": (await db.AdminUsers.FindAsync(s.Actor.AdminUserId))!.ChangePassword("synthetic-new-hash", "revoked"); break;
            }
            await db.SaveChangesAsync();
        };
        Assert.Equal(expected, (await service.ProbeAsync(s.Actor, s.Instance.Id, s.Instance.Version, true)).Code);
        await using var verify = database.CreateContext();
        Assert.False(await verify.ManagedDatabases.AnyAsync(x => x.InstanceId == s.Instance.Id));
        Assert.False(await verify.AuditRecords.AnyAsync(x => x.TargetId == s.Instance.Id.ToString("N") && x.Action == "instance.discover"));
    }

    [Fact]
    public async Task ProbeFailureIsAuditedAndCancelledProbeDoesNotWriteResults()
    {
        using var provider = database.CreateServiceProvider();
        var service = new ServerManagementService(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(), _probe);
        var s = await SeedAsync(service);
        _probe.Fail = true;
        var failed = await service.ProbeAsync(s.Actor, s.Instance.Id, s.Instance.Version, true);
        Assert.Equal(ManagementCode.TargetFailed, failed.Code);
        Assert.Equal("TransportSecurityFailed", failed.Value!.ErrorCode);
        Assert.Equal(0, _probe.DiscoveryCalls);
        _probe.Fail = false;
        using var cancelled = new CancellationTokenSource();
        _probe.BeforeProbe = () => { cancelled.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProbeAsync(s.Actor, s.Instance.Id, failed.Value.Version, true, cancelled.Token));
        await using var verify = database.CreateContext();
        Assert.Single(await verify.AuditRecords.Where(x => x.TargetId == s.Instance.Id.ToString("N") && x.Action == "instance.discover").ToArrayAsync());
    }

    [Fact]
    public async Task InvalidTypesAndTrustExceptionsAreRejectedAndConfigurationEditInvalidatesDiscovery()
    {
        using var provider = database.CreateServiceProvider();
        var service = new ServerManagementService(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(), _probe);
        var s = await SeedAsync(service);
        Assert.Equal(ManagementCode.ValidationFailed, (await service.SaveServerAsync(s.Actor, null, null,
            s.Server.Settings with { CredentialId = s.Instance.Settings.SqlCredentialId, Name = "wrong-kind" })).Code);
        Assert.Equal(ManagementCode.ValidationFailed, (await service.SaveInstanceAsync(s.Actor, s.Instance.Id, s.Instance.Version,
            s.Instance.Settings with { TrustServerCertificate = true })).Code);
        Assert.Equal(ManagementCode.ValidationFailed, (await service.SaveInstanceAsync(s.Actor, s.Instance.Id, s.Instance.Version,
            s.Instance.Settings with { ConnectionAddress = "host;Password=synthetic" })).Code);
        var discovered = await service.ProbeAsync(s.Actor, s.Instance.Id, s.Instance.Version, true);
        var row = (await service.ListAsync(s.Actor)).Value!.Databases.Single(x => x.InstanceId == s.Instance.Id);
        Assert.Equal(ManagementCode.Succeeded, (await service.SetManagedAsync(s.Actor, row.Id, row.Version, true)).Code);
        var updated = await service.SaveInstanceAsync(s.Actor, s.Instance.Id, discovered.Value!.Version,
            s.Instance.Settings with { TrustServerCertificate = true, CertificateTrustReason = "synthetic-test-exception" });
        Assert.Equal(ManagementCode.Succeeded, updated.Code);
        Assert.Equal("Unknown", updated.Value!.ConnectionStatus);
        row = (await service.ListAsync(s.Actor)).Value!.Databases.Single(x => x.InstanceId == s.Instance.Id);
        Assert.False(row.IsAvailable); Assert.False(row.IsManaged);
        Assert.Equal(ManagementCode.Conflict, (await service.ProbeAsync(s.Actor, s.Instance.Id, s.Instance.Version, false)).Code);
        await using var verify = database.CreateContext();
        Assert.True(await verify.AuditRecords.AnyAsync(x => x.TargetId == s.Instance.Id.ToString("N") && x.Action == "instance.certificate_trust_exception"));
    }

    [Fact]
    public async Task AmbiguousDatabaseNamesRollbackEntireDiscoveryAndConcurrentUpdatesHaveOneWinner()
    {
        using var provider = database.CreateServiceProvider();
        var service = new ServerManagementService(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(), _probe);
        var s = await SeedAsync(service);
        _probe.Catalog = [Db("CaseDb"), Db("casedb")];
        Assert.Equal(ManagementCode.Conflict, (await service.ProbeAsync(s.Actor, s.Instance.Id, s.Instance.Version, true)).Code);
        var rows = (await service.ListAsync(s.Actor)).Value!.Databases.Where(x => x.InstanceId == s.Instance.Id);
        Assert.Empty(rows);
        var results = await Task.WhenAll(service.SaveServerAsync(s.Actor, s.Server.Id, s.Server.Version, s.Server.Settings with { Description = "first" }),
            service.SaveServerAsync(s.Actor, s.Server.Id, s.Server.Version, s.Server.Settings with { Description = "second" }));
        Assert.Single(results, x => x.Code == ManagementCode.Succeeded);
        Assert.Single(results, x => x.Code == ManagementCode.Conflict);
    }

    private static TargetSqlDatabaseInfo Db(string name, bool system = false, bool snapshot = false, bool online = true) =>
        new(name, online ? TargetSqlDatabaseState.Online : TargetSqlDatabaseState.Offline, TargetSqlRecoveryModel.Full, system, snapshot, !system && !snapshot && online);
    private sealed class FakeProbe : ITargetSqlReadOnlyProbe
    {
        public TargetSqlConnectionInput? LastInput { get; private set; }
        public Func<Task>? BeforeProbe { get; set; }
        public bool Fail { get; set; }
        public int DiscoveryCalls { get; private set; }
        public IReadOnlyList<TargetSqlDatabaseInfo> Catalog { get; set; } = [Db("business")];
        public async Task<TargetSqlResult<TargetSqlServerInfo>> ProbeServerAsync(TargetSqlConnectionInput connection, CancellationToken cancellationToken = default)
        {
            LastInput = connection;
            if (BeforeProbe is not null) await BeforeProbe();
            cancellationToken.ThrowIfCancellationRequested();
            return Fail ? TargetSqlResult.ConfirmedFailure<TargetSqlServerInfo>(TargetSqlFailureCode.TransportSecurityFailed, TargetSqlFailurePhase.ConnectionOpen)
                : TargetSqlResult.Succeeded(new TargetSqlServerInfo("15.0.synthetic", "synthetic", "synthetic", 3, 15, true));
        }
        public Task<TargetSqlResult<TargetSqlDatabaseCatalog>> DiscoverDatabasesAsync(TargetSqlConnectionInput connection, CancellationToken cancellationToken = default)
        { DiscoveryCalls++; return Task.FromResult(TargetSqlResult.Succeeded(new TargetSqlDatabaseCatalog(Catalog))); }
        public Task<TargetSqlResult<TargetSqlBackupVerification>> VerifyBackupAsync(TargetSqlConnectionInput connection, TargetSqlBackupVerificationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class TrackingFactory(IDbContextFactory<PlatformDbContext> inner) : IDbContextFactory<PlatformDbContext>
    {
        public List<PlatformDbContext> Created { get; } = [];
        public PlatformDbContext CreateDbContext() { var context = inner.CreateDbContext(); Created.Add(context); return context; }
        public async Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        { var context = await inner.CreateDbContextAsync(cancellationToken); Created.Add(context); return context; }
    }
}
