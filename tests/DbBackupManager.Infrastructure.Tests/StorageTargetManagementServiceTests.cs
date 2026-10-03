using DbBackupManager.Application.FileStorage;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.Servers;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.StorageTargets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class StorageTargetManagementServiceTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    private const string Fingerprint = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task SaveRequiresMatchingCredentialAndRejectsInvalidSftpFingerprint()
    {
        var probe = new FakeFileProbe();
        using var provider = database.CreateServiceProvider();
        var service = new StorageTargetManagementService(
            provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(), probe);
        var seed = await SeedAsync();
        Assert.Equal(ManagementCode.ValidationFailed, (await service.SaveAsync(seed.Actor, null, null,
            new("sftp-bad", 2, "synthetic-host", 22, "/archives", seed.SmbId, "SHA256:bad", true))).Code);
        Assert.Equal(ManagementCode.ValidationFailed, (await service.SaveAsync(seed.Actor, null, null,
            new("smb-wrong-kind", 1, "synthetic-host", null, "share", seed.SftpId, null, true))).Code);
        var created = await service.SaveAsync(seed.Actor, null, null,
            new($"smb-{Guid.NewGuid():N}", 1, "synthetic-host", null, "share", seed.SmbId, null, true));
        Assert.Equal(ManagementCode.Succeeded, created.Code);
        var sftp = await service.SaveAsync(seed.Actor, null, null,
            new($"sftp-{Guid.NewGuid():N}", 2, "synthetic-host", 22, "/archives", seed.SftpId, Fingerprint, true));
        Assert.Equal(ManagementCode.Succeeded, sftp.Code);
        Assert.Equal(Fingerprint, sftp.Value!.Settings.Fingerprint);
        await using var db = database.CreateContext();
        Assert.True(await db.AuditRecords.AnyAsync(x => x.Action == "storage_target.create"
            && x.TargetId == created.Value!.Id.ToString("N")));
    }

    [Fact]
    public async Task ProbeReleasesContextThenRecordsFailureWithoutWritingFiles()
    {
        var probe = new FakeFileProbe
        {
            Result = BackupFileStorageResult.ConfirmedFailure<BackupFileMetadata>(
                BackupFileStorageFailureCode.AuthenticationFailed,
                BackupFileStorageFailurePhase.ConnectionOpen),
        };
        using var provider = database.CreateServiceProvider();
        var tracking = new TrackingFactory(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>());
        var service = new StorageTargetManagementService(tracking, probe);
        var seed = await SeedAsync();
        var created = await service.SaveAsync(seed.Actor, null, null,
            new($"probe-{Guid.NewGuid():N}", 1, "synthetic-host", null, "share", seed.SmbId, null, true));
        probe.BeforeInspect = () =>
        {
            foreach (var context in tracking.Created)
                Assert.Throws<ObjectDisposedException>(() => context.ChangeTracker.Entries().ToArray());
            return Task.CompletedTask;
        };
        var probed = await service.TestConnectionAsync(seed.Actor, created.Value!.Id, created.Value.Version);
        Assert.Equal(ManagementCode.TargetFailed, probed.Code);
        Assert.Equal(nameof(BackupFileStorageFailureCode.AuthenticationFailed), probed.TargetError);
        Assert.Equal(@"\\synthetic-host\share\DbBackupManager.connection-probe", probe.LastPath);
        await using var db = database.CreateContext();
        Assert.True(await db.AuditRecords.AnyAsync(x =>
            x.Action == "storage_target.probe" && x.Result == "Failed"
            && x.TargetId == created.Value.Id.ToString("N")));
    }

    [Fact]
    public async Task ConcurrentRenameHasOneWinnerAndRevokedIdentityFailsClosed()
    {
        var probe = new FakeFileProbe();
        using var provider = database.CreateServiceProvider();
        var service = new StorageTargetManagementService(
            provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(), probe);
        var seed = await SeedAsync();
        var created = await service.SaveAsync(seed.Actor, null, null,
            new($"race-{Guid.NewGuid():N}", 1, "synthetic-host", null, "share", seed.SmbId, null, true));
        var first = created.Value!;
        var results = await Task.WhenAll(
            service.SaveAsync(seed.Actor, first.Id, first.Version, first.Settings with { Name = "first" }),
            service.SaveAsync(seed.Actor, first.Id, first.Version, first.Settings with { Name = "second" }));
        Assert.Single(results, x => x.Code == ManagementCode.Succeeded);
        Assert.Single(results, x => x.Code == ManagementCode.Conflict);
        var revoked = seed.Actor with { SecurityStamp = "revoked" };
        Assert.Equal(ManagementCode.AuthenticationRequired, (await service.ListAsync(revoked)).Code);
    }

    private async Task<(AdminSession Actor, Guid SmbId, Guid SftpId)> SeedAsync()
    {
        var id = Guid.NewGuid();
        var actor = new AdminSession(id, $"admin-{id:N}", $"stamp-{id:N}");
        var smbId = Guid.NewGuid();
        var sftpId = Guid.NewGuid();
        await using var db = database.CreateContext();
        db.AdminUsers.Add(new AdminUser(id, actor.Username, actor.Username.ToUpperInvariant(), "synthetic-hash", actor.SecurityStamp));
        db.CredentialReferences.Add(new(smbId, $"smb-{id:N}", CredentialKind.SmbPassword, "user", "synthetic-cipher", "synthetic-version"));
        db.CredentialReferences.Add(new(sftpId, $"sftp-{id:N}", CredentialKind.SftpPassword, "user", "synthetic-cipher", "synthetic-version"));
        await db.SaveChangesAsync();
        return (actor, smbId, sftpId);
    }

    private sealed class FakeFileProbe : IBackupFileStorageProbe
    {
        public BackupFileStorageResult<BackupFileMetadata> Result { get; set; } =
            BackupFileStorageResult.Succeeded(new BackupFileMetadata(false, false, null));
        public Func<Task>? BeforeInspect { get; set; }
        public string? LastPath { get; private set; }

        public async Task<BackupFileStorageResult<BackupFileMetadata>> InspectAsync(
            BackupFileEndpointInput endpoint, string path, CancellationToken cancellationToken = default)
        {
            LastPath = path;
            if (BeforeInspect is not null) await BeforeInspect();
            return Result;
        }
    }

    private sealed class TrackingFactory(IDbContextFactory<PlatformDbContext> inner) : IDbContextFactory<PlatformDbContext>
    {
        public List<PlatformDbContext> Created { get; } = [];
        public PlatformDbContext CreateDbContext()
        {
            var context = inner.CreateDbContext();
            Created.Add(context);
            return context;
        }

        public async Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            var context = await inner.CreateDbContextAsync(cancellationToken);
            Created.Add(context);
            return context;
        }
    }
}
