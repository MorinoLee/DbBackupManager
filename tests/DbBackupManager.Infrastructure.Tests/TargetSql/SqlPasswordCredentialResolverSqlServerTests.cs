using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class SqlPasswordCredentialResolverSqlServerTests(
    PlatformDatabaseSqlServerFixture database) : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    private const string TestDirectoryPrefix = "DbBackupManagerP56ResolverKeys_";
    private readonly string _keyRingPath = Path.Combine(
        Path.GetTempPath(),
        $"{TestDirectoryPrefix}{Guid.NewGuid():N}");

    [Fact]
    public async Task EnabledSqlPasswordIsReadByShortFactoryContextAndResolvedAfterProjection()
    {
        try
        {
            var credentialId = Guid.NewGuid();
            var protector = new BusinessCredentialDataProtector(_keyRingPath);
            var protectedSecret = protector.ProtectSqlPassword("synthetic-password".AsSpan());
            await using (var context = database.CreateContext())
            {
                context.CredentialReferences.Add(new CredentialReference(
                    credentialId,
                    $"P5.6 SQL {credentialId:N}",
                    CredentialKind.SqlPassword,
                    "synthetic-login",
                    protectedSecret,
                    BusinessCredentialDataProtector.SqlPasswordProtectionVersion));
                await context.SaveChangesAsync();
            }

            using var provider = database.CreateServiceProvider();
            var resolver = new SqlPasswordCredentialResolver(
                provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(),
                protector);

            using var resolution = await resolver.ResolveSqlPasswordAsync(
                credentialId,
                CancellationToken.None);

            Assert.Null(resolution.FailureCode);
            Assert.Equal("synthetic-login", resolution.Credential!.UserName);
            Assert.Equal("synthetic-login", resolution.Credential.CreateSqlCredential().UserId);
        }
        finally
        {
            DeleteKeyRing();
        }
    }

    [Theory]
    [InlineData(CredentialKind.SmbPassword, true, "dp-sql-password-v1", TargetSqlFailureCode.CredentialInvalid)]
    [InlineData(CredentialKind.SqlPassword, false, "dp-sql-password-v1", TargetSqlFailureCode.CredentialUnavailable)]
    [InlineData(CredentialKind.SqlPassword, true, "future-version", TargetSqlFailureCode.CredentialInvalid)]
    public async Task InvalidCredentialMetadataFailsClosed(
        CredentialKind kind,
        bool isEnabled,
        string protectionVersion,
        TargetSqlFailureCode expected)
    {
        try
        {
            var credentialId = Guid.NewGuid();
            var protector = new BusinessCredentialDataProtector(_keyRingPath);
            var protectedSecret = protector.ProtectSqlPassword("synthetic-password".AsSpan());
            await using (var context = database.CreateContext())
            {
                context.CredentialReferences.Add(new CredentialReference(
                    credentialId,
                    $"P5.6 invalid {credentialId:N}",
                    kind,
                    "synthetic-login",
                    protectedSecret,
                    protectionVersion,
                    isEnabled: isEnabled));
                await context.SaveChangesAsync();
            }

            using var provider = database.CreateServiceProvider();
            var resolver = new SqlPasswordCredentialResolver(
                provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(),
                protector);

            using var resolution = await resolver.ResolveSqlPasswordAsync(
                credentialId,
                CancellationToken.None);

            Assert.Equal(expected, resolution.FailureCode);
            Assert.Null(resolution.Credential);
        }
        finally
        {
            DeleteKeyRing();
        }
    }

    [Fact]
    public async Task MissingCredentialAndUnavailableKeyRingUseStableCodes()
    {
        using var provider = database.CreateServiceProvider();
        var contextFactory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var missingResolver = new SqlPasswordCredentialResolver(
            contextFactory,
            new BusinessCredentialDataProtector(_keyRingPath));

        using var missing = await missingResolver.ResolveSqlPasswordAsync(
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal(TargetSqlFailureCode.CredentialUnavailable, missing.FailureCode);

        var credentialId = Guid.NewGuid();
        var writer = new BusinessCredentialDataProtector(_keyRingPath);
        var protectedSecret = writer.ProtectSqlPassword("synthetic-password".AsSpan());
        await using (var context = database.CreateContext())
        {
            context.CredentialReferences.Add(new CredentialReference(
                credentialId,
                $"P5.6 unavailable {credentialId:N}",
                CredentialKind.SqlPassword,
                "synthetic-login",
                protectedSecret,
                BusinessCredentialDataProtector.SqlPasswordProtectionVersion));
            await context.SaveChangesAsync();
        }

        var unavailableResolver = new SqlPasswordCredentialResolver(
            contextFactory,
            new BusinessCredentialDataProtector(null));
        using var unavailable = await unavailableResolver.ResolveSqlPasswordAsync(
            credentialId,
            CancellationToken.None);

        Assert.Equal(TargetSqlFailureCode.CredentialUnavailable, unavailable.FailureCode);
        DeleteKeyRing();
    }

    private void DeleteKeyRing()
    {
        if (!Directory.Exists(_keyRingPath))
        {
            return;
        }

        if (!Path.GetFileName(_keyRingPath).StartsWith(TestDirectoryPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("拒绝清理不属于 P5.6 的临时 Key Ring。");
        }

        Directory.Delete(_keyRingPath, recursive: true);
    }
}
