using System.Text.Json;
using DbBackupManager.Application.FileCredentials;
using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.FileCredentials;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class FileCredentialServiceTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>, IDisposable
{
    private const string KeyPrefix = "DbBackupManagerP72Keys_";
    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"{KeyPrefix}{Guid.NewGuid():N}");
    private FileCredentialService Service(IServiceProvider provider, bool configured = true) => new(
        provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [BusinessCredentialDataProtector.KeyRingPathConfigurationKey] = configured ? _keyPath : null }).Build());

    [Theory]
    [InlineData(FileCredentialKind.SmbPassword, "SmbPassword.v1", "dp-smb-password-v1")]
    [InlineData(FileCredentialKind.SftpPassword, "SftpPassword.v1", "dp-sftp-password-v1")]
    public async Task FilePasswordsAreEncryptedIsolatedAndRotatedWithVersion(FileCredentialKind kind, string purpose, string protectionVersion)
    {
        var actor = await CreateActorAsync();
        using var provider = database.CreateServiceProvider();
        var service = Service(provider);
        var password = new string('x', 256);
        var created = await service.CreateAsync(actor, $"file-{Guid.NewGuid():N}", "synthetic-user", kind, password);
        Assert.Equal(FileCredentialResultCode.Succeeded, created.Code);
        var item = created.Value!;
        await using (var db = database.CreateContext())
        {
            var entity = await db.CredentialReferences.FindAsync(item.Id);
            Assert.Equal(protectionVersion, entity!.ProtectionVersion);
            Assert.DoesNotContain(password, entity.ProtectedSecret, StringComparison.Ordinal);
            var result = new BusinessCredentialDataProtector(_keyPath, purpose: purpose).UnprotectSqlPassword(entity.ProtectedSecret);
            Assert.Equal(password, new string(result.Secret!)); Array.Clear(result.Secret!);
            Assert.Equal(BusinessCredentialProtectionStatus.Invalid,
                new BusinessCredentialDataProtector(_keyPath).UnprotectSqlPassword(entity.ProtectedSecret).Status);
            var wrongPurpose = purpose == "SmbPassword.v1" ? "SftpPassword.v1" : "SmbPassword.v1";
            Assert.Equal(BusinessCredentialProtectionStatus.Invalid,
                new BusinessCredentialDataProtector(_keyPath, purpose: wrongPurpose).UnprotectSqlPassword(entity.ProtectedSecret).Status);
            Assert.DoesNotContain(entity.ProtectedSecret, JsonSerializer.Serialize((await service.ListAsync(actor)).Value), StringComparison.Ordinal);
        }
        var rotation = await service.RotatePasswordAsync(actor, item.Id, item.Version, "synthetic-new");
        Assert.Equal(FileCredentialResultCode.Succeeded, rotation.Code);
        Assert.Equal(kind, rotation.Value!.Kind);
        Assert.Equal(FileCredentialResultCode.Conflict, (await service.SetEnabledAsync(actor, item.Id, item.Version, false)).Code);
        var disabled = await service.SetEnabledAsync(actor, item.Id, rotation.Value.Version, false);
        Assert.False(disabled.Value!.IsEnabled);
        await using var verify = database.CreateContext();
        var saved = await verify.CredentialReferences.FindAsync(item.Id);
        var decrypted = new BusinessCredentialDataProtector(_keyPath, purpose: purpose).UnprotectSqlPassword(saved!.ProtectedSecret);
        Assert.Equal("synthetic-new", new string(decrypted.Secret!)); Array.Clear(decrypted.Secret!);
        Assert.Equal(3, await verify.AuditRecords.CountAsync(x => x.TargetId == item.Id.ToString("N")));
    }

    [Fact]
    public async Task MissingKeysInvalidKindsAndRevokedIdentityFailClosed()
    {
        var actor = await CreateActorAsync();
        using var provider = database.CreateServiceProvider();
        var service = Service(provider);
        Assert.Equal(FileCredentialResultCode.ValidationFailed,
            (await service.CreateAsync(actor, "invalid", "user", (FileCredentialKind)1, "secret")).Code);
        Assert.Equal(FileCredentialResultCode.ProtectionUnavailable,
            (await Service(provider, false).CreateAsync(actor, "missing", "user", FileCredentialKind.SmbPassword, "secret")).Code);
        var revoked = actor with { SecurityStamp = "revoked" };
        Assert.Equal(FileCredentialResultCode.AuthenticationRequired, (await service.ListAsync(revoked)).Code);
        Assert.Equal(FileCredentialResultCode.AuthenticationRequired,
            (await service.CreateAsync(revoked, "invalid", "user", FileCredentialKind.SmbPassword, "secret")).Code);
        Assert.False(Directory.Exists(_keyPath));
    }

    [Fact]
    public async Task SqlCredentialCannotBeRotatedThroughFileServiceAndConcurrentRotationHasOneWinner()
    {
        var actor = await CreateActorAsync();
        using var provider = database.CreateServiceProvider();
        var service = Service(provider);
        await using var db = database.CreateContext();
        var sql = new Domain.Configuration.CredentialReference(Guid.NewGuid(), $"sql-{Guid.NewGuid():N}",
            Domain.Configuration.CredentialKind.SqlPassword, "user", "synthetic-cipher", "synthetic-version");
        db.CredentialReferences.Add(sql); await db.SaveChangesAsync();
        Assert.Equal(FileCredentialResultCode.NotFound,
            (await service.RotatePasswordAsync(actor, sql.Id, Convert.ToBase64String(sql.RowVersion), "secret")).Code);
        Assert.Equal(FileCredentialResultCode.ValidationFailed,
            (await service.CreateAsync(actor, $"key-{Guid.NewGuid():N}", "user", FileCredentialKind.SftpPrivateKey, "secret")).Code);
        var item = (await service.CreateAsync(actor, $"file-{Guid.NewGuid():N}", "user", FileCredentialKind.SmbPassword, "secret")).Value!;
        var createdKey = await service.CreatePrivateKeyAsync(actor, $"key-{Guid.NewGuid():N}", "user",
            "-----BEGIN OPENSSH PRIVATE KEY-----\nsynthetic\n-----END OPENSSH PRIVATE KEY-----", "passphrase");
        Assert.Equal(FileCredentialResultCode.Succeeded, createdKey.Code);
        Assert.True(createdKey.Value!.HasPassphrase);
        Assert.Equal(FileCredentialKind.SftpPrivateKey, createdKey.Value.Kind);
        await using (var keyDb = database.CreateContext())
        {
            var key = await keyDb.CredentialReferences.FindAsync(createdKey.Value.Id);
            Assert.Equal("dp-sftp-private-key-v1", key!.ProtectionVersion);
            Assert.DoesNotContain("synthetic", key.ProtectedSecret, StringComparison.Ordinal);
            Assert.NotNull(key.ProtectedSecondarySecret);
            var decrypted = new BusinessCredentialDataProtector(_keyPath, purpose: "SftpPrivateKey.v1")
                .UnprotectSqlPassword(key.ProtectedSecret);
            Assert.Contains("OPENSSH PRIVATE KEY", new string(decrypted.Secret!), StringComparison.Ordinal);
            Array.Clear(decrypted.Secret!);
        }
        Assert.Equal(FileCredentialResultCode.NotFound,
            (await service.RotatePasswordAsync(actor, createdKey.Value.Id, createdKey.Value.Version, "secret")).Code);

        var results = await Task.WhenAll(service.RotatePasswordAsync(actor, item.Id, item.Version, "first"),
            service.RotatePasswordAsync(actor, item.Id, item.Version, "second"));
        Assert.Single(results, x => x.Code == FileCredentialResultCode.Succeeded);
        Assert.Single(results, x => x.Code == FileCredentialResultCode.Conflict);
    }
    private async Task<AdminSession> CreateActorAsync()
    {
        var id = Guid.NewGuid();
        var actor = new AdminSession(id, $"admin-{id:N}", Guid.NewGuid().ToString("N"));
        await using var context = database.CreateContext();
        context.AdminUsers.Add(new AdminUser(id, actor.Username, actor.Username.ToUpperInvariant(), "synthetic-hash", actor.SecurityStamp));
        await context.SaveChangesAsync();
        return actor;
    }

    public void Dispose()
    {
        var path = Path.GetFullPath(_keyPath);
        if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("拒绝清理非测试密钥目录。");
        }
        if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
    }
}
