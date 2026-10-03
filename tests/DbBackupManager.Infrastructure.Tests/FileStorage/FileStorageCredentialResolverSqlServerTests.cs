using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.FileStorage;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests.FileStorage;

public sealed class FileStorageCredentialResolverSqlServerTests(
    PlatformDatabaseSqlServerFixture database) :
    IClassFixture<PlatformDatabaseSqlServerFixture>,
    IDisposable
{
    private const string TestDirectoryPrefix = "DbBackupManagerP58FileKeys_";
    private readonly string _keyRingPath = Path.Combine(
        Path.GetTempPath(),
        $"{TestDirectoryPrefix}{Guid.NewGuid():N}");

    [Fact]
    public async Task EnabledSmbPasswordUsesShortContextAndPurposeIsolatedDecryption()
    {
        var credentialId = Guid.NewGuid();
        var protector = new BusinessCredentialDataProtector(
            _keyRingPath,
            purpose: "SmbPassword.v1");
        var protectedSecret = protector.ProtectFilePassword("synthetic-password".AsSpan());
        await using (var context = database.CreateContext())
        {
            context.CredentialReferences.Add(new CredentialReference(
                credentialId,
                $"P5.8 SMB {credentialId:N}",
                CredentialKind.SmbPassword,
                "SYNTHETIC\\worker",
                protectedSecret,
                "dp-smb-password-v1"));
            await context.SaveChangesAsync();
        }

        using var provider = database.CreateServiceProvider();
        var resolver = CreateResolver(
            provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(),
            _keyRingPath);

        using var resolution = await resolver.ResolveAsync(
            credentialId,
            FileTransferProtocol.Smb,
            CancellationToken.None);

        Assert.Null(resolution.FailureCode);
        Assert.Equal(CredentialKind.SmbPassword, resolution.Credential!.Kind);
        Assert.Equal("SYNTHETIC\\worker", resolution.Credential.Username);
        Assert.Equal("synthetic-password", new string(resolution.Credential.PrimarySecret));
        Assert.False(resolution.Credential.HasSecondarySecret);
    }

    [Fact]
    public async Task SftpPasswordUsesItsOwnPurposeAndReturnsNoSecondarySecret()
    {
        var credentialId = Guid.NewGuid();
        var protectedSecret = new BusinessCredentialDataProtector(
            _keyRingPath,
            purpose: "SftpPassword.v1")
            .ProtectFilePassword("synthetic-sftp-password".AsSpan());
        await SaveAsync(new CredentialReference(
            credentialId,
            $"P5.8 SFTP password {credentialId:N}",
            CredentialKind.SftpPassword,
            "synthetic-sftp-user",
            protectedSecret,
            "dp-sftp-password-v1"));

        using var provider = database.CreateServiceProvider();
        var resolver = CreateResolver(
            provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(),
            _keyRingPath);

        using var resolution = await resolver.ResolveAsync(
            credentialId,
            FileTransferProtocol.Sftp,
            CancellationToken.None);

        Assert.Null(resolution.FailureCode);
        Assert.Equal(CredentialKind.SftpPassword, resolution.Credential!.Kind);
        Assert.Equal("synthetic-sftp-password", new string(resolution.Credential.PrimarySecret));
        Assert.False(resolution.Credential.HasSecondarySecret);
    }

    [Fact]
    public async Task SftpPrivateKeyUsesIndependentKeyAndPassphrasePurposes()
    {
        var credentialId = Guid.NewGuid();
        var protectedKey = new BusinessCredentialDataProtector(
            _keyRingPath,
            purpose: "SftpPrivateKey.v1")
            .ProtectFilePassword("synthetic-private-key".AsSpan());
        var protectedPassphrase = new BusinessCredentialDataProtector(
            _keyRingPath,
            purpose: "SftpPrivateKeyPassphrase.v1")
            .ProtectFilePassword("synthetic-passphrase".AsSpan());
        await SaveAsync(new CredentialReference(
            credentialId,
            $"P5.8 SFTP key {credentialId:N}",
            CredentialKind.SftpPrivateKey,
            "synthetic-key-user",
            protectedKey,
            "dp-sftp-private-key-v1",
            protectedPassphrase));

        using var provider = database.CreateServiceProvider();
        var resolver = CreateResolver(
            provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(),
            _keyRingPath);

        using var resolution = await resolver.ResolveAsync(
            credentialId,
            FileTransferProtocol.Sftp,
            CancellationToken.None);

        Assert.Null(resolution.FailureCode);
        Assert.Equal(CredentialKind.SftpPrivateKey, resolution.Credential!.Kind);
        Assert.Equal("synthetic-private-key", new string(resolution.Credential.PrimarySecret));
        Assert.Equal("synthetic-passphrase", new string(resolution.Credential.SecondarySecret));
    }

    [Fact]
    public async Task SftpPrivateKeyWithWrongPassphrasePurposeFailsClosed()
    {
        var credentialId = Guid.NewGuid();
        var protectedKey = new BusinessCredentialDataProtector(
            _keyRingPath,
            purpose: "SftpPrivateKey.v1")
            .ProtectFilePassword("synthetic-private-key".AsSpan());
        var protectedPassphrase = new BusinessCredentialDataProtector(
            _keyRingPath,
            purpose: "SftpPassword.v1")
            .ProtectFilePassword("synthetic-passphrase".AsSpan());
        await SaveAsync(new CredentialReference(
            credentialId,
            $"P5.8 wrong purpose {credentialId:N}",
            CredentialKind.SftpPrivateKey,
            "synthetic-key-user",
            protectedKey,
            "dp-sftp-private-key-v1",
            protectedPassphrase));

        using var provider = database.CreateServiceProvider();
        var resolver = CreateResolver(
            provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(),
            _keyRingPath);

        using var resolution = await resolver.ResolveAsync(
            credentialId,
            FileTransferProtocol.Sftp,
            CancellationToken.None);

        Assert.Equal(BackupFileStorageFailureCode.CredentialInvalid, resolution.FailureCode);
        Assert.Null(resolution.Credential);
    }

    [Theory]
    [InlineData(CredentialKind.SftpPassword, true, "dp-sftp-password-v1", BackupFileStorageFailureCode.CredentialInvalid)]
    [InlineData(CredentialKind.SmbPassword, false, "dp-smb-password-v1", BackupFileStorageFailureCode.CredentialUnavailable)]
    [InlineData(CredentialKind.SmbPassword, true, "future-version", BackupFileStorageFailureCode.CredentialInvalid)]
    public async Task InvalidSmbCredentialMetadataFailsClosed(
        CredentialKind kind,
        bool isEnabled,
        string protectionVersion,
        BackupFileStorageFailureCode expected)
    {
        var credentialId = Guid.NewGuid();
        var protectedSecret = new BusinessCredentialDataProtector(
            _keyRingPath,
            purpose: kind == CredentialKind.SmbPassword
                ? "SmbPassword.v1"
                : "SftpPassword.v1")
            .ProtectFilePassword("synthetic-password".AsSpan());
        await using (var context = database.CreateContext())
        {
            context.CredentialReferences.Add(new CredentialReference(
                credentialId,
                $"P5.8 invalid {credentialId:N}",
                kind,
                "synthetic-worker",
                protectedSecret,
                protectionVersion,
                isEnabled: isEnabled));
            await context.SaveChangesAsync();
        }

        using var provider = database.CreateServiceProvider();
        var resolver = CreateResolver(
            provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(),
            _keyRingPath);

        using var resolution = await resolver.ResolveAsync(
            credentialId,
            FileTransferProtocol.Smb,
            CancellationToken.None);

        Assert.Equal(expected, resolution.FailureCode);
        Assert.Null(resolution.Credential);
    }

    [Fact]
    public async Task MissingKeyRingFailsClosedWithoutReturningCredential()
    {
        var credentialId = Guid.NewGuid();
        var protectedSecret = new BusinessCredentialDataProtector(
            _keyRingPath,
            purpose: "SmbPassword.v1")
            .ProtectFilePassword("synthetic-password".AsSpan());
        await using (var context = database.CreateContext())
        {
            context.CredentialReferences.Add(new CredentialReference(
                credentialId,
                $"P5.8 missing keys {credentialId:N}",
                CredentialKind.SmbPassword,
                "synthetic-worker",
                protectedSecret,
                "dp-smb-password-v1"));
            await context.SaveChangesAsync();
        }

        using var provider = database.CreateServiceProvider();
        var resolver = CreateResolver(
            provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(),
            keyRingPath: null);

        using var resolution = await resolver.ResolveAsync(
            credentialId,
            FileTransferProtocol.Smb,
            CancellationToken.None);

        Assert.Equal(
            BackupFileStorageFailureCode.CredentialProtectionUnavailable,
            resolution.FailureCode);
        Assert.Null(resolution.Credential);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_keyRingPath))
        {
            return;
        }

        if (!Path.GetFileName(_keyRingPath).StartsWith(TestDirectoryPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("拒绝清理不属于 P5.8 的临时 Key Ring。");
        }

        Directory.Delete(_keyRingPath, recursive: true);
    }

    private static FileStorageCredentialResolver CreateResolver(
        IDbContextFactory<PlatformDbContext> contextFactory,
        string? keyRingPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BusinessCredentialDataProtector.KeyRingPathConfigurationKey] = keyRingPath,
            })
            .Build();
        return new FileStorageCredentialResolver(contextFactory, configuration);
    }

    private async Task SaveAsync(CredentialReference credential)
    {
        await using var context = database.CreateContext();
        context.CredentialReferences.Add(credential);
        await context.SaveChangesAsync();
    }
}
