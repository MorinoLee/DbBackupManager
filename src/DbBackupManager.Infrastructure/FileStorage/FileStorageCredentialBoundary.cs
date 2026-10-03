using System.Data.Common;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DbBackupManager.Infrastructure.FileStorage;

internal interface IFileStorageCredentialResolver
{
    ValueTask<FileStorageCredentialResolution> ResolveAsync(
        Guid credentialReferenceId,
        FileTransferProtocol protocol,
        CancellationToken cancellationToken);
}

internal sealed class FileStorageCredentialResolution : IDisposable
{
    private FileStorageCredentialResolution(
        FileStorageCredentialLease? credential,
        BackupFileStorageFailureCode? failureCode)
    {
        if ((credential is null) == (failureCode is null))
        {
            throw new ArgumentException("凭据解析结果必须且只能包含凭据或失败分类。");
        }

        Credential = credential;
        FailureCode = failureCode;
    }

    public FileStorageCredentialLease? Credential { get; }

    public BackupFileStorageFailureCode? FailureCode { get; }

    public static FileStorageCredentialResolution Succeeded(
        FileStorageCredentialLease credential) =>
        new(credential, null);

    public static FileStorageCredentialResolution Failed(
        BackupFileStorageFailureCode failureCode)
    {
        if (failureCode is not (BackupFileStorageFailureCode.CredentialUnavailable
            or BackupFileStorageFailureCode.CredentialProtectionUnavailable
            or BackupFileStorageFailureCode.CredentialInvalid))
        {
            throw new ArgumentOutOfRangeException(nameof(failureCode));
        }

        return new(null, failureCode);
    }

    public void Dispose()
    {
        Credential?.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal sealed class FileStorageCredentialLease : IDisposable
{
    private char[]? _primarySecret;
    private char[]? _secondarySecret;

    private FileStorageCredentialLease(
        CredentialKind kind,
        string username,
        char[] primarySecret,
        char[]? secondarySecret)
    {
        Kind = kind;
        Username = username;
        _primarySecret = primarySecret;
        _secondarySecret = secondarySecret;
    }

    public CredentialKind Kind { get; }

    public string Username { get; }

    public ReadOnlySpan<char> PrimarySecret => _primarySecret
        ?? throw new ObjectDisposedException(nameof(FileStorageCredentialLease));

    public ReadOnlySpan<char> SecondarySecret => _secondarySecret;

    public bool HasSecondarySecret => _secondarySecret is not null;

    public static FileStorageCredentialLease CreateAndClear(
        CredentialKind kind,
        string username,
        char[] primarySecret,
        char[]? secondarySecret)
    {
        ArgumentNullException.ThrowIfNull(primarySecret);
        try
        {
            var normalizedUsername = username?.Trim();
            if (string.IsNullOrEmpty(normalizedUsername)
                || normalizedUsername.Length > 256
                || normalizedUsername.Any(char.IsControl)
                || primarySecret.Length is 0 or > 65_535
                || secondarySecret is { Length: 0 or > 4_096 })
            {
                throw new ArgumentException("文件访问凭据格式无效。");
            }

            var primaryCopy = primarySecret.ToArray();
            var secondaryCopy = secondarySecret?.ToArray();
            return new FileStorageCredentialLease(
                kind,
                normalizedUsername,
                primaryCopy,
                secondaryCopy);
        }
        finally
        {
            Zero(primarySecret);
            Zero(secondarySecret);
        }
    }

    public void Dispose()
    {
        Zero(_primarySecret);
        Zero(_secondarySecret);
        _primarySecret = null;
        _secondarySecret = null;
        GC.SuppressFinalize(this);
    }

    private static void Zero(char[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(value.AsSpan()));
        }
    }
}

internal sealed class FileStorageCredentialResolver(
    IDbContextFactory<PlatformDbContext> contextFactory,
    IConfiguration configuration) : IFileStorageCredentialResolver
{
    public async ValueTask<FileStorageCredentialResolution> ResolveAsync(
        Guid credentialReferenceId,
        FileTransferProtocol protocol,
        CancellationToken cancellationToken)
    {
        if (credentialReferenceId == Guid.Empty || !Enum.IsDefined(protocol))
        {
            return FileStorageCredentialResolution.Failed(
                BackupFileStorageFailureCode.CredentialInvalid);
        }

        CredentialEnvelope? envelope;
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            envelope = await context.CredentialReferences.AsNoTracking()
                .Where(credential => credential.Id == credentialReferenceId)
                .Select(credential => new CredentialEnvelope(
                    credential.Kind,
                    credential.Username,
                    credential.ProtectedSecret,
                    credential.ProtectedSecondarySecret,
                    credential.ProtectionVersion,
                    credential.IsEnabled))
                .SingleOrDefaultAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            return FileStorageCredentialResolution.Failed(
                BackupFileStorageFailureCode.CredentialUnavailable);
        }

        if (envelope is null || !envelope.IsEnabled)
        {
            return FileStorageCredentialResolution.Failed(
                BackupFileStorageFailureCode.CredentialUnavailable);
        }

        var expected = ExpectedCredential(protocol, envelope.Kind);
        if (expected is null
            || !string.Equals(envelope.ProtectionVersion, expected.Value.Version, StringComparison.Ordinal)
            || envelope.Kind != CredentialKind.SftpPrivateKey
                && envelope.ProtectedSecondarySecret is not null)
        {
            return FileStorageCredentialResolution.Failed(
                BackupFileStorageFailureCode.CredentialInvalid);
        }

        var primary = Unprotect(envelope.ProtectedSecret, expected.Value.PrimaryPurpose);
        if (primary.Status != BusinessCredentialProtectionStatus.Succeeded)
        {
            return FileStorageCredentialResolution.Failed(MapProtectionFailure(primary.Status));
        }

        BusinessCredentialUnprotectResult? secondary = null;
        try
        {
            if (envelope.ProtectedSecondarySecret is not null)
            {
                secondary = Unprotect(
                    envelope.ProtectedSecondarySecret,
                    expected.Value.SecondaryPurpose!);
                if (secondary.Status != BusinessCredentialProtectionStatus.Succeeded)
                {
                    return FileStorageCredentialResolution.Failed(
                        MapProtectionFailure(secondary.Status));
                }
            }

            try
            {
                return FileStorageCredentialResolution.Succeeded(
                    FileStorageCredentialLease.CreateAndClear(
                        envelope.Kind,
                        envelope.Username,
                        primary.Secret!,
                        secondary?.Secret));
            }
            catch (ArgumentException)
            {
                return FileStorageCredentialResolution.Failed(
                    BackupFileStorageFailureCode.CredentialInvalid);
            }
        }
        finally
        {
            Zero(primary.Secret);
            Zero(secondary?.Secret);
        }
    }

    private BusinessCredentialUnprotectResult Unprotect(string protectedSecret, string purpose) =>
        new BusinessCredentialDataProtector(
            configuration[BusinessCredentialDataProtector.KeyRingPathConfigurationKey],
            purpose: purpose)
            .UnprotectSqlPassword(protectedSecret);

    private static CredentialExpectation? ExpectedCredential(
        FileTransferProtocol protocol,
        CredentialKind kind) => (protocol, kind) switch
        {
            (FileTransferProtocol.Smb, CredentialKind.SmbPassword) => new(
                "dp-smb-password-v1",
                "SmbPassword.v1",
                null),
            (FileTransferProtocol.Sftp, CredentialKind.SftpPassword) => new(
                "dp-sftp-password-v1",
                "SftpPassword.v1",
                null),
            (FileTransferProtocol.Sftp, CredentialKind.SftpPrivateKey) => new(
                "dp-sftp-private-key-v1",
                "SftpPrivateKey.v1",
                "SftpPrivateKeyPassphrase.v1"),
            _ => null,
        };

    private static BackupFileStorageFailureCode MapProtectionFailure(
        BusinessCredentialProtectionStatus status) =>
        status == BusinessCredentialProtectionStatus.Unavailable
            ? BackupFileStorageFailureCode.CredentialProtectionUnavailable
            : BackupFileStorageFailureCode.CredentialInvalid;

    private static void Zero(char[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(value.AsSpan()));
        }
    }

    private sealed record CredentialEnvelope(
        CredentialKind Kind,
        string Username,
        string ProtectedSecret,
        string? ProtectedSecondarySecret,
        string ProtectionVersion,
        bool IsEnabled);

    private readonly record struct CredentialExpectation(
        string Version,
        string PrimaryPurpose,
        string? SecondaryPurpose);
}
