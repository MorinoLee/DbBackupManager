using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.Configuration;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace DbBackupManager.Infrastructure.FileStorage;

internal sealed class SftpFileStorageSessionFactory(
    IFileStorageCredentialResolver credentialResolver) : IFileStorageProtocolSessionFactory
{
    public FileTransferProtocol Protocol => FileTransferProtocol.Sftp;

    public async ValueTask<IFileStorageProtocolSession> OpenAsync(
        BackupFileEndpointInput endpoint,
        CancellationToken cancellationToken)
    {
        if (endpoint.Protocol != FileTransferProtocol.Sftp
            || endpoint.Port is null
            || endpoint.SftpHostKeyFingerprint is null)
        {
            throw Rejected(BackupFileStorageFailureCode.InvalidRequest);
        }

        using var resolution = await credentialResolver.ResolveAsync(
            endpoint.CredentialReferenceId,
            FileTransferProtocol.Sftp,
            cancellationToken);
        if (resolution.Credential is not { } credential)
        {
            throw new FileStorageAdapterException(
                resolution.FailureCode ?? BackupFileStorageFailureCode.CredentialInvalid,
                BackupFileStorageFailurePhase.CredentialResolution);
        }

        SftpConnectionResources? resources = null;
        SftpClient? client = null;
        var ownershipTransferred = false;
        try
        {
            resources = SftpConnectionResources.Create(credential);
            var connectionInfo = new ConnectionInfo(
                endpoint.Host,
                endpoint.Port.Value,
                credential.Username,
                resources.AuthenticationMethod)
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            client = new SftpClient(connectionInfo);
            var hostKeyMatched = false;
            var hostKeyObserved = false;
            client.HostKeyReceived += (_, args) =>
            {
                hostKeyObserved = true;
                hostKeyMatched = SftpHostKeyVerifier.Matches(
                    endpoint.SftpHostKeyFingerprint,
                    args.HostKey);
                args.CanTrust = hostKeyMatched;
            };

            try
            {
                await client.ConnectAsync(cancellationToken);
            }
            catch (Exception) when (hostKeyObserved && !hostKeyMatched)
            {
                throw Rejected(BackupFileStorageFailureCode.HostKeyMismatch);
            }

            if (!hostKeyObserved || !hostKeyMatched || !client.IsConnected)
            {
                throw Rejected(
                    hostKeyObserved
                        ? BackupFileStorageFailureCode.HostKeyMismatch
                        : BackupFileStorageFailureCode.InvalidResponse);
            }

            resources.ClearTransientSecrets();
            ownershipTransferred = true;
            return new SftpFileStorageSession(endpoint, client, resources);
        }
        catch (SshAuthenticationException)
        {
            throw Rejected(BackupFileStorageFailureCode.AuthenticationFailed);
        }
        catch (Exception exception) when (
            exception is SshConnectionException or SshOperationTimeoutException or SocketException)
        {
            throw Rejected(BackupFileStorageFailureCode.ConnectionFailed);
        }
        finally
        {
            if (!ownershipTransferred)
            {
                try
                {
                    client?.Dispose();
                }
                finally
                {
                    resources?.Dispose();
                }
            }
        }
    }

    private static FileStorageAdapterException Rejected(BackupFileStorageFailureCode code) =>
        new(code, BackupFileStorageFailurePhase.ConnectionOpen);
}

internal sealed class SftpFileStorageSession(
    BackupFileEndpointInput endpoint,
    ISftpClient client,
    SftpConnectionResources resources) : IFileStorageProtocolSession
{
    public async ValueTask PrepareParentDirectoryAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var exactPath = SftpPathGuard.ValidateExactFilePath(endpoint, filePath);
        await EnsureParentsAreDirectoriesAsync(
            exactPath,
            cancellationToken,
            BackupFileStorageFailurePhase.DirectoryPrepare,
            prepareMissingDirectories: true);
    }

    public async ValueTask<BackupFileMetadata> InspectAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var exactPath = SftpPathGuard.ValidateExactFilePath(endpoint, path);
        await EnsureParentsAreDirectoriesAsync(exactPath, cancellationToken);
        return await InspectValidatedPathAsync(
            exactPath,
            BackupFileStorageFailurePhase.SourceInspection,
            cancellationToken);
    }

    private async ValueTask<BackupFileMetadata> InspectValidatedPathAsync(
        string exactPath,
        BackupFileStorageFailurePhase phase,
        CancellationToken cancellationToken)
    {
        try
        {
            var attributes = await client.GetAttributesAsync(exactPath, cancellationToken);
            if (attributes.IsSymbolicLink)
            {
                throw PathRejected(phase);
            }

            return attributes.IsRegularFile
                ? new BackupFileMetadata(true, true, attributes.Size)
                : new BackupFileMetadata(true, false, null);
        }
        catch (SftpPathNotFoundException)
        {
            return new BackupFileMetadata(false, false, null);
        }
        catch (Exception exception)
        {
            throw Translate(exception, phase);
        }
    }

    public async ValueTask<Stream> OpenReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var exactPath = SftpPathGuard.ValidateExactFilePath(endpoint, path);
        await EnsureRegularFileAsync(
            exactPath,
            BackupFileStorageFailurePhase.SourceOpen,
            cancellationToken);
        try
        {
            return await client.OpenAsync(
                exactPath,
                FileMode.Open,
                FileAccess.Read,
                cancellationToken);
        }
        catch (Exception exception)
        {
            throw Translate(exception, BackupFileStorageFailurePhase.SourceOpen);
        }
    }

    public async ValueTask<Stream> CreateNewAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var exactPath = SftpPathGuard.ValidateExactFilePath(endpoint, path);
        await EnsureParentsAreDirectoriesAsync(exactPath, cancellationToken);
        try
        {
            return await client.OpenAsync(
                exactPath,
                FileMode.CreateNew,
                FileAccess.Write,
                cancellationToken);
        }
        catch (Exception exception)
        {
            throw Translate(exception, BackupFileStorageFailurePhase.TargetCreate);
        }
    }

    public async ValueTask RenameNoReplaceAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        SftpPathGuard.EnsureSameDirectory(endpoint, sourcePath, destinationPath);
        var source = SftpPathGuard.ValidateExactFilePath(endpoint, sourcePath);
        var destination = SftpPathGuard.ValidateExactFilePath(endpoint, destinationPath);
        await EnsureParentsAreDirectoriesAsync(source, cancellationToken);
        if ((await InspectValidatedPathAsync(
            destination,
            BackupFileStorageFailurePhase.SourceInspection,
            cancellationToken)).Exists)
        {
            throw new FileStorageAdapterException(
                BackupFileStorageFailureCode.FileAlreadyExists,
                BackupFileStorageFailurePhase.Rename);
        }

        try
        {
            // SFTP v3 rename 不采用会覆盖目标的 POSIX 扩展；目标冲突必须失败。
            await client.RenameFileAsync(source, destination, cancellationToken);
        }
        catch (Exception exception)
        {
            throw Translate(
                exception,
                BackupFileStorageFailurePhase.Rename,
                indeterminateOnDisconnect: true);
        }
    }

    public async ValueTask DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var exactPath = SftpPathGuard.ValidateExactFilePath(endpoint, path);
        await EnsureRegularFileAsync(
            exactPath,
            BackupFileStorageFailurePhase.Delete,
            cancellationToken);
        try
        {
            await client.DeleteFileAsync(exactPath, cancellationToken);
        }
        catch (Exception exception)
        {
            throw Translate(
                exception,
                BackupFileStorageFailurePhase.Delete,
                indeterminateOnDisconnect: true);
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            client.Dispose();
        }
        finally
        {
            resources.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private async ValueTask EnsureRegularFileAsync(
        string exactPath,
        BackupFileStorageFailurePhase phase,
        CancellationToken cancellationToken)
    {
        await EnsureParentsAreDirectoriesAsync(exactPath, cancellationToken);
        var metadata = await InspectValidatedPathAsync(exactPath, phase, cancellationToken);
        if (!metadata.Exists)
        {
            throw new FileStorageAdapterException(
                BackupFileStorageFailureCode.FileNotFound,
                phase);
        }
        if (!metadata.IsRegularFile)
        {
            throw PathRejected(phase);
        }
    }

    private async ValueTask EnsureParentsAreDirectoriesAsync(
        string exactPath,
        CancellationToken cancellationToken,
        BackupFileStorageFailurePhase phase = BackupFileStorageFailurePhase.RequestValidation,
        bool prepareMissingDirectories = false)
    {
        foreach (var parent in SftpPathGuard.ParentPaths(exactPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SftpFileAttributes attributes;
            try
            {
                attributes = await client.GetAttributesAsync(parent, cancellationToken);
            }
            catch (SftpPathNotFoundException) when (
                prepareMissingDirectories && SftpPathGuard.IsDescendantOfRoot(endpoint, parent))
            {
                attributes = await CreateAndInspectDirectoryAsync(parent, phase, cancellationToken);
            }
            catch (SftpPathNotFoundException) when (!prepareMissingDirectories)
            {
                throw PathRejected(phase);
            }
            catch (Exception exception)
            {
                throw Translate(exception, phase);
            }

            EnsureSafeDirectory(attributes, phase);
        }
    }

    private async Task<SftpFileAttributes> CreateAndInspectDirectoryAsync(
        string path,
        BackupFileStorageFailurePhase phase,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.CreateDirectoryAsync(path, cancellationToken);
            return await client.GetAttributesAsync(path, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception createException)
        {
            // 并发创建可能返回“已存在”；只在重新读取为安全目录后接受。
            try
            {
                return await client.GetAttributesAsync(path, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                throw Translate(createException, phase);
            }
        }
    }

    private static void EnsureSafeDirectory(
        SftpFileAttributes attributes,
        BackupFileStorageFailurePhase phase)
    {
        if (attributes.IsSymbolicLink || !attributes.IsDirectory)
        {
            throw PathRejected(phase);
        }
    }

    private static Exception Translate(
        Exception exception,
        BackupFileStorageFailurePhase phase,
        bool indeterminateOnDisconnect = false)
    {
        if (exception is OperationCanceledException)
        {
            return exception;
        }

        if (exception is FileStorageAdapterException adapter)
        {
            return adapter;
        }

        var code = exception switch
        {
            SftpPermissionDeniedException => BackupFileStorageFailureCode.AuthorizationDenied,
            SftpPathNotFoundException => BackupFileStorageFailureCode.FileNotFound,
            SshOperationTimeoutException => BackupFileStorageFailureCode.TimedOut,
            SshConnectionException or SocketException =>
                BackupFileStorageFailureCode.ConnectionInterrupted,
            SshException => BackupFileStorageFailureCode.OperationRejected,
            _ => BackupFileStorageFailureCode.InvalidResponse,
        };
        var indeterminate = indeterminateOnDisconnect
            && exception is SshConnectionException or SshOperationTimeoutException or SocketException;
        return new FileStorageAdapterException(code, phase, indeterminate);
    }

    private static FileStorageAdapterException PathRejected(
        BackupFileStorageFailurePhase phase) =>
        new(BackupFileStorageFailureCode.PathRejected, phase);
}

internal static class SftpPathGuard
{
    public static string ValidateExactFilePath(
        BackupFileEndpointInput endpoint,
        string path)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Protocol != FileTransferProtocol.Sftp
            || string.IsNullOrWhiteSpace(path)
            || path.Length > 2_048
            || path[0] != '/'
            || path == "/"
            || path.EndsWith('/')
            || path.Contains('\\')
            || path.Any(char.IsControl))
        {
            throw Rejected();
        }

        var parts = path[1..].Split('/');
        var rootParts = endpoint.RootPath[1..].Split('/');
        if (parts.Length <= rootParts.Length
            || parts.Any(InvalidSegment))
        {
            throw Rejected();
        }

        for (var index = 0; index < rootParts.Length; index++)
        {
            if (!string.Equals(parts[index], rootParts[index], StringComparison.Ordinal))
            {
                throw Rejected();
            }
        }

        return $"/{string.Join('/', parts)}";
    }

    public static void EnsureSameDirectory(
        BackupFileEndpointInput endpoint,
        string sourcePath,
        string destinationPath)
    {
        var source = ValidateExactFilePath(endpoint, sourcePath);
        var destination = ValidateExactFilePath(endpoint, destinationPath);
        if (!string.Equals(
            source[..source.LastIndexOf('/')],
            destination[..destination.LastIndexOf('/')],
            StringComparison.Ordinal))
        {
            throw Rejected();
        }
    }

    public static IEnumerable<string> ParentPaths(string exactPath)
    {
        var parts = exactPath[1..].Split('/');
        var current = string.Empty;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            current += $"/{parts[index]}";
            yield return current;
        }
    }

    public static bool IsDescendantOfRoot(
        BackupFileEndpointInput endpoint,
        string path) =>
        path.StartsWith($"{endpoint.RootPath}/", StringComparison.Ordinal);

    private static bool InvalidSegment(string segment) =>
        string.IsNullOrEmpty(segment) || segment is "." or "..";

    private static FileStorageAdapterException Rejected() => new(
        BackupFileStorageFailureCode.PathRejected,
        BackupFileStorageFailurePhase.RequestValidation);
}

internal static class SftpHostKeyVerifier
{
    public static bool Matches(string expectedFingerprint, ReadOnlySpan<byte> hostKey)
    {
        if (!TryDecode(expectedFingerprint, out var expectedHash))
        {
            return false;
        }

        try
        {
            Span<byte> actualHash = stackalloc byte[32];
            SHA256.HashData(hostKey, actualHash);
            return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedHash);
        }
    }

    private static bool TryDecode(string fingerprint, out byte[] hash)
    {
        const string prefix = "SHA256:";
        hash = [];
        if (!fingerprint.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var encoded = fingerprint[prefix.Length..];
            hash = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            return hash.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed class SftpConnectionResources : IDisposable
{
    private byte[]? _transientSecret;
    private byte[]? _privateKeyBytes;
    private readonly PrivateKeyFile? _privateKey;

    private SftpConnectionResources(
        AuthenticationMethod authenticationMethod,
        byte[]? transientSecret,
        byte[]? privateKeyBytes,
        PrivateKeyFile? privateKey)
    {
        AuthenticationMethod = authenticationMethod;
        _transientSecret = transientSecret;
        _privateKeyBytes = privateKeyBytes;
        _privateKey = privateKey;
    }

    public AuthenticationMethod AuthenticationMethod { get; }

    public static SftpConnectionResources Create(FileStorageCredentialLease credential)
    {
        if (credential.Kind == CredentialKind.SftpPassword)
        {
            var password = EncodeUtf8(credential.PrimarySecret);
            return new SftpConnectionResources(
                new PasswordAuthenticationMethod(credential.Username, password),
                password,
                null,
                null);
        }

        if (credential.Kind != CredentialKind.SftpPrivateKey)
        {
            throw new FileStorageAdapterException(
                BackupFileStorageFailureCode.CredentialInvalid,
                BackupFileStorageFailurePhase.CredentialResolution);
        }

        var privateKeyBytes = EncodeUtf8(credential.PrimarySecret);
        try
        {
            using var stream = new MemoryStream(privateKeyBytes, writable: false);
            // SSH.NET 的私钥口令 API 只接受 string；仅在构造解析器期间保留该短生命周期副本。
            var passphrase = credential.HasSecondarySecret
                ? CreateStringAndClearCopy(credential.SecondarySecret)
                : null;
            var privateKey = passphrase is null
                ? new PrivateKeyFile(stream)
                : new PrivateKeyFile(stream, passphrase);
            return new SftpConnectionResources(
                new PrivateKeyAuthenticationMethod(credential.Username, privateKey),
                null,
                privateKeyBytes,
                privateKey);
        }
        catch (Exception exception) when (
            exception is SshException or ArgumentException or InvalidOperationException)
        {
            CryptographicOperations.ZeroMemory(privateKeyBytes);
            throw new FileStorageAdapterException(
                BackupFileStorageFailureCode.CredentialInvalid,
                BackupFileStorageFailurePhase.CredentialResolution);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(privateKeyBytes);
            throw;
        }
    }

    public void ClearTransientSecrets()
    {
        Zero(ref _transientSecret);
        Zero(ref _privateKeyBytes);
    }

    public void Dispose()
    {
        ClearTransientSecrets();
        _privateKey?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void Zero(ref byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
            value = null;
        }
    }

    private static byte[] EncodeUtf8(ReadOnlySpan<char> value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value)];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    private static string CreateStringAndClearCopy(ReadOnlySpan<char> value)
    {
        var copy = value.ToArray();
        try
        {
            return new string(copy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(copy.AsSpan()));
        }
    }
}
