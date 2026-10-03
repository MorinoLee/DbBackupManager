using System.Text.Json;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.SqlCredentials;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.SqlCredentials;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class SqlCredentialServiceTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>, IDisposable
{
    private const string KeyPrefix = "DbBackupManagerP71Keys_";
    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"{KeyPrefix}{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateStoresCiphertextAndAuditAndListNeverReturnsPassword()
    {
        var actor = await CreateActorAsync();
        using var provider = database.CreateServiceProvider();
        var service = CreateService(provider);
        var result = await service.CreateAsync(actor, $"SQL {Guid.NewGuid():N}", "synthetic-login", "synthetic-password");
        Assert.Equal(SqlCredentialResultCode.Succeeded, result.Code);

        await using var context = database.CreateContext();
        var entity = await context.CredentialReferences.FindAsync(result.Value!.Id);
        Assert.NotNull(entity);
        Assert.DoesNotContain("synthetic-password", entity.ProtectedSecret, StringComparison.Ordinal);
        var protector = new BusinessCredentialDataProtector(_keyPath);
        var decrypted = protector.UnprotectSqlPassword(entity.ProtectedSecret);
        Assert.Equal("synthetic-password", new string(decrypted.Secret!));
        Array.Clear(decrypted.Secret!);
        var audit = await context.AuditRecords.SingleAsync(x => x.TargetId == entity.Id.ToString("N"));
        Assert.Equal(actor.AdminUserId, audit.ActorAdminUserId);
        Assert.Equal("credential.create", audit.Action);

        var list = await service.ListAsync(actor);
        var json = JsonSerializer.Serialize(list.Value);
        Assert.Contains("synthetic-login", json, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-password", json, StringComparison.Ordinal);
        Assert.DoesNotContain(entity.ProtectedSecret, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotationAndDisableRejectStaleVersionAndDisabledResolverFailsClosed()
    {
        var actor = await CreateActorAsync();
        using var provider = database.CreateServiceProvider();
        var service = CreateService(provider);
        var created = (await service.CreateAsync(actor, $"SQL {Guid.NewGuid():N}", "synthetic-login", "old-password")).Value!;
        var rotated = await service.RotatePasswordAsync(actor, created.Id, created.Version, "new-password");
        Assert.Equal(SqlCredentialResultCode.Succeeded, rotated.Code);
        Assert.NotEqual(created.Version, rotated.Value!.Version);
        Assert.Equal(SqlCredentialResultCode.Conflict,
            (await service.SetEnabledAsync(actor, created.Id, created.Version, false)).Code);

        var disabled = await service.SetEnabledAsync(actor, created.Id, rotated.Value.Version, false);
        Assert.Equal(SqlCredentialResultCode.Succeeded, disabled.Code);
        Assert.False(disabled.Value!.IsEnabled);
        var resolver = new SqlPasswordCredentialResolver(
            provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(), new BusinessCredentialDataProtector(_keyPath));
        using var resolved = await resolver.ResolveSqlPasswordAsync(created.Id, CancellationToken.None);
        Assert.Equal(Application.TargetSql.TargetSqlFailureCode.CredentialUnavailable, resolved.FailureCode);

        await using var context = database.CreateContext();
        var entity = await context.CredentialReferences.FindAsync(created.Id);
        var decrypted = new BusinessCredentialDataProtector(_keyPath).UnprotectSqlPassword(entity!.ProtectedSecret);
        Assert.Equal("new-password", new string(decrypted.Secret!));
        Array.Clear(decrypted.Secret!);
        Assert.Equal(3, await context.AuditRecords.CountAsync(x => x.TargetId == created.Id.ToString("N")));
    }

    [Fact]
    public async Task InvalidSessionAndMissingProtectionCannotCreateAnyCredential()
    {
        var actor = await CreateActorAsync();
        using var provider = database.CreateServiceProvider();
        var service = CreateService(provider);
        var stale = actor with { SecurityStamp = "stale" };
        Assert.Equal(SqlCredentialResultCode.AuthenticationRequired, (await service.ListAsync(stale)).Code);
        Assert.Equal(SqlCredentialResultCode.AuthenticationRequired,
            (await service.CreateAsync(stale, "not-created", "synthetic-login", "synthetic-password")).Code);
        Assert.False(Directory.Exists(_keyPath));

        var missing = new SqlCredentialService(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(),
            new ConfigurationBuilder().Build());
        Assert.Equal(SqlCredentialResultCode.ProtectionUnavailable,
            (await missing.CreateAsync(actor, "not-created", "synthetic-login", "synthetic-password")).Code);
        await using var context = database.CreateContext();
        Assert.False(await context.AuditRecords.AnyAsync(x => x.ActorAdminUserId == actor.AdminUserId));
    }

    [Fact]
    public async Task DuplicateNameAndEmptyRotationLeaveExistingCredentialUnchanged()
    {
        var actor = await CreateActorAsync();
        using var provider = database.CreateServiceProvider();
        var service = CreateService(provider);
        var name = $"SQL {Guid.NewGuid():N}";
        var created = (await service.CreateAsync(actor, name, "synthetic-login", "synthetic-password")).Value!;
        Assert.Equal(SqlCredentialResultCode.Conflict,
            (await service.CreateAsync(actor, name.ToUpperInvariant(), "another-login", "another-password")).Code);
        Assert.Equal(SqlCredentialResultCode.ValidationFailed,
            (await service.RotatePasswordAsync(actor, created.Id, created.Version, string.Empty)).Code);
        Assert.Equal(SqlCredentialResultCode.ValidationFailed,
            (await service.SetEnabledAsync(actor, created.Id, "invalid", false)).Code);
        Assert.Equal(created, Assert.Single((await service.ListAsync(actor)).Value!, x => x.Id == created.Id));
    }

    private SqlCredentialService CreateService(IServiceProvider provider) => new(
        provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BusinessCredentialDataProtector.KeyRingPathConfigurationKey] = _keyPath,
        }).Build());

    [Fact]
    public async Task CompetingPasswordUpdatesOnlyOneWinsAndRevokedActorCannotReadOrWrite()
    {
        var actor = await CreateActorAsync();
        using var provider = database.CreateServiceProvider();
        var service = CreateService(provider);
        var item = (await service.CreateAsync(actor, $"SQL {Guid.NewGuid():N}", "synthetic-login", "initial-password")).Value!;
        var results = await Task.WhenAll(
            service.RotatePasswordAsync(actor, item.Id, item.Version, "first-password"),
            service.RotatePasswordAsync(actor, item.Id, item.Version, "second-password"));
        Assert.Single(results, x => x.Code == SqlCredentialResultCode.Succeeded);
        Assert.Single(results, x => x.Code == SqlCredentialResultCode.Conflict);

        await using (var context = database.CreateContext())
        {
            var admin = await context.AdminUsers.FindAsync(actor.AdminUserId);
            admin!.ChangePassword("changed-synthetic-hash", "changed-stamp");
            await context.SaveChangesAsync();
        }

        Assert.Equal(SqlCredentialResultCode.AuthenticationRequired, (await service.ListAsync(actor)).Code);
        var version = results.Single(x => x.Code == SqlCredentialResultCode.Succeeded).Value!.Version;
        Assert.Equal(SqlCredentialResultCode.AuthenticationRequired,
            (await service.SetEnabledAsync(actor, item.Id, version, false)).Code);
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
