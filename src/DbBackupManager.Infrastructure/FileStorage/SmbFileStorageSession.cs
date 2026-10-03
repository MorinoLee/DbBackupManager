using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.Configuration;
using Microsoft.Win32.SafeHandles;

namespace DbBackupManager.Infrastructure.FileStorage;

internal sealed class SmbFileStorageSessionFactory(
    IFileStorageCredentialResolver credentialResolver) : IFileStorageProtocolSessionFactory
{
    public FileTransferProtocol Protocol => FileTransferProtocol.Smb;

    public async ValueTask<IFileStorageProtocolSession> OpenAsync(
        BackupFileEndpointInput endpoint,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new FileStorageAdapterException(
                BackupFileStorageFailureCode.PlatformUnsupported,
                BackupFileStorageFailurePhase.ConnectionOpen);
        }

        using var resolution = await credentialResolver.ResolveAsync(
            endpoint.CredentialReferenceId,
            FileTransferProtocol.Smb,
            cancellationToken);
        if (resolution.Credential is not { Kind: CredentialKind.SmbPassword } credential)
        {
            throw new FileStorageAdapterException(
                resolution.FailureCode ?? BackupFileStorageFailureCode.CredentialInvalid,
                BackupFileStorageFailurePhase.CredentialResolution);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var token = WindowsNetworkLogon.Logon(credential.Username, credential.PrimarySecret);
        return new SmbFileStorageSession(endpoint, token);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class SmbFileStorageSession(
    BackupFileEndpointInput endpoint,
    SafeAccessTokenHandle accessToken) : IFileStorageProtocolSession
{
    public ValueTask PrepareParentDirectoryAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsIdentity.RunImpersonated(
            accessToken,
            () => SmbFileOperations.PrepareParentDirectory(
                endpoint,
                filePath,
                cancellationToken));
        return ValueTask.CompletedTask;
    }

    public ValueTask<BackupFileMetadata> InspectAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = WindowsIdentity.RunImpersonated(
            accessToken,
            () => SmbFileOperations.Inspect(endpoint, path, cancellationToken));
        return ValueTask.FromResult(result);
    }

    public ValueTask<Stream> OpenReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = WindowsIdentity.RunImpersonated(
            accessToken,
            () => SmbFileOperations.OpenRead(endpoint, path, cancellationToken));
        return ValueTask.FromResult<Stream>(stream);
    }

    public ValueTask<Stream> CreateNewAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = WindowsIdentity.RunImpersonated(
            accessToken,
            () => SmbFileOperations.CreateNew(endpoint, path, cancellationToken));
        return ValueTask.FromResult<Stream>(stream);
    }

    public ValueTask RenameNoReplaceAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsIdentity.RunImpersonated(
            accessToken,
            () => SmbFileOperations.RenameNoReplace(
                endpoint,
                sourcePath,
                destinationPath,
                cancellationToken));
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsIdentity.RunImpersonated(
            accessToken,
            () => SmbFileOperations.Delete(endpoint, path, cancellationToken));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        accessToken.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class SmbPathGuard
{
    public static string ValidateExactFilePath(
        BackupFileEndpointInput endpoint,
        string path)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Protocol != FileTransferProtocol.Smb
            || string.IsNullOrWhiteSpace(path)
            || path.Length > 2_048
            || !path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || path.Contains('/')
            || path.Any(char.IsControl))
        {
            throw Rejected();
        }

        var parts = path[2..].Split('\\');
        var rootParts = endpoint.RootPath.Split('\\');
        if (parts.Length <= rootParts.Length + 1
            || parts.Any(InvalidSegment)
            || !string.Equals(parts[0], endpoint.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw Rejected();
        }

        for (var index = 0; index < rootParts.Length; index++)
        {
            if (!string.Equals(
                parts[index + 1],
                rootParts[index],
                StringComparison.OrdinalIgnoreCase))
            {
                throw Rejected();
            }
        }

        return $"\\\\{string.Join('\\', parts)}";
    }

    public static void EnsureSameDirectory(
        BackupFileEndpointInput endpoint,
        string sourcePath,
        string destinationPath)
    {
        var source = ValidateExactFilePath(endpoint, sourcePath);
        var destination = ValidateExactFilePath(endpoint, destinationPath);
        if (!string.Equals(
            Path.GetDirectoryName(source),
            Path.GetDirectoryName(destination),
            StringComparison.OrdinalIgnoreCase))
        {
            throw Rejected();
        }
    }

    public static IReadOnlyList<string> ParentDirectoryPaths(
        BackupFileEndpointInput endpoint,
        string filePath)
    {
        var exactPath = ValidateExactFilePath(endpoint, filePath);
        var parts = exactPath[2..].Split('\\');
        var directories = new List<string>(parts.Length - 2);
        var current = $"\\\\{parts[0]}\\{parts[1]}";
        directories.Add(current);
        for (var index = 2; index < parts.Length - 1; index++)
        {
            current = $"{current}\\{parts[index]}";
            directories.Add(current);
        }

        return directories;
    }

    public static void ValidateDirectoryAttributes(
        FileAttributes attributes,
        BackupFileStorageFailurePhase phase)
    {
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileStorageAdapterException(
                BackupFileStorageFailureCode.PathRejected,
                phase);
        }
    }

    private static bool InvalidSegment(string segment) =>
        string.IsNullOrWhiteSpace(segment)
        || segment is "." or ".."
        || segment.EndsWith('.')
        || segment.EndsWith(' ')
        || segment.IndexOfAny([':', '*', '?', '<', '>', '|', '"']) >= 0;

    private static FileStorageAdapterException Rejected() => new(
        BackupFileStorageFailureCode.PathRejected,
        BackupFileStorageFailurePhase.RequestValidation);
}

[SupportedOSPlatform("windows")]
internal static class SmbFileOperations
{
    public static void PrepareParentDirectory(
        BackupFileEndpointInput endpoint,
        string filePath,
        CancellationToken cancellationToken) =>
        EnsureParentDirectories(
            endpoint,
            filePath,
            BackupFileStorageFailurePhase.DirectoryPrepare,
            cancellationToken,
            prepareMissingDirectories: true);

    public static BackupFileMetadata Inspect(
        BackupFileEndpointInput endpoint,
        string path,
        CancellationToken cancellationToken)
    {
        var exactPath = SmbPathGuard.ValidateExactFilePath(endpoint, path);
        EnsureParentDirectories(endpoint, exactPath, BackupFileStorageFailurePhase.SourceInspection, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var attributes = File.GetAttributes(exactPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw PathRejected(BackupFileStorageFailurePhase.SourceInspection);
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                return new BackupFileMetadata(true, false, null);
            }

            using var stream = OpenVerified(
                exactPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None,
                BackupFileStorageFailurePhase.SourceInspection);
            return new BackupFileMetadata(
                true,
                true,
                stream.Length,
                TryGetIdentity(stream.SafeFileHandle));
        }
        catch (FileNotFoundException)
        {
            return new BackupFileMetadata(false, false, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SmbNativeFailure.Translate(exception, BackupFileStorageFailurePhase.SourceInspection);
        }
    }

    public static FileStream OpenRead(
        BackupFileEndpointInput endpoint,
        string path,
        CancellationToken cancellationToken)
    {
        var exactPath = SmbPathGuard.ValidateExactFilePath(endpoint, path);
        EnsureParentDirectories(endpoint, exactPath, BackupFileStorageFailurePhase.SourceOpen, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(exactPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SmbNativeFailure.Translate(exception, BackupFileStorageFailurePhase.SourceOpen);
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw PathRejected(BackupFileStorageFailurePhase.SourceOpen);
        }

        try
        {
            return OpenVerified(
                exactPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.Asynchronous | FileOptions.SequentialScan,
                BackupFileStorageFailurePhase.SourceOpen);
        }
        catch (FileNotFoundException)
        {
            throw new FileStorageAdapterException(
                BackupFileStorageFailureCode.FileNotFound,
                BackupFileStorageFailurePhase.SourceOpen);
        }
    }

    public static FileStream CreateNew(
        BackupFileEndpointInput endpoint,
        string path,
        CancellationToken cancellationToken)
    {
        var exactPath = SmbPathGuard.ValidateExactFilePath(endpoint, path);
        EnsureParentDirectories(endpoint, exactPath, BackupFileStorageFailurePhase.TargetCreate, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return OpenVerified(
                exactPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                FileOptions.Asynchronous | FileOptions.SequentialScan,
                BackupFileStorageFailurePhase.TargetCreate);
        }
        catch (IOException exception) when (IsAlreadyExists(exception))
        {
            throw new FileStorageAdapterException(
                BackupFileStorageFailureCode.FileAlreadyExists,
                BackupFileStorageFailurePhase.TargetCreate);
        }
    }

    public static void RenameNoReplace(
        BackupFileEndpointInput endpoint,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        SmbPathGuard.EnsureSameDirectory(endpoint, sourcePath, destinationPath);
        var source = SmbPathGuard.ValidateExactFilePath(endpoint, sourcePath);
        var destination = SmbPathGuard.ValidateExactFilePath(endpoint, destinationPath);
        EnsureParentDirectories(endpoint, source, BackupFileStorageFailurePhase.Rename, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            File.Move(source, destination, overwrite: false);
        }
        catch (IOException exception) when (IsAlreadyExists(exception))
        {
            throw new FileStorageAdapterException(
                BackupFileStorageFailureCode.FileAlreadyExists,
                BackupFileStorageFailurePhase.Rename);
        }
    }

    public static void Delete(
        BackupFileEndpointInput endpoint,
        string path,
        CancellationToken cancellationToken)
    {
        var exactPath = SmbPathGuard.ValidateExactFilePath(endpoint, path);
        EnsureParentDirectories(endpoint, exactPath, BackupFileStorageFailurePhase.Delete, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(exactPath);
    }

    private static FileStream OpenVerified(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options,
        BackupFileStorageFailurePhase phase)
    {
        var stream = new FileStream(path, mode, access, share, 128 * 1024, options);
        try
        {
            var resolvedPath = GetFinalPath(stream.SafeFileHandle);
            if (!string.Equals(resolvedPath, path, StringComparison.OrdinalIgnoreCase))
            {
                throw PathRejected(phase);
            }

            return stream;
        }
        catch (FileStorageAdapterException exception) when (
            mode == FileMode.CreateNew && !exception.Indeterminate)
        {
            stream.Dispose();
            throw new FileStorageAdapterException(
                exception.Code,
                exception.Phase,
                indeterminate: true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void EnsureParentDirectories(
        BackupFileEndpointInput endpoint,
        string exactPath,
        BackupFileStorageFailurePhase phase,
        CancellationToken cancellationToken,
        bool prepareMissingDirectories = false)
    {
        var directories = SmbPathGuard.ParentDirectoryPaths(endpoint, exactPath);
        var configuredRootDepth = endpoint.RootPath.Split('\\').Length;
        for (var index = 0; index < directories.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = directories[index];
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (
                prepareMissingDirectories
                && index >= configuredRootDepth
                && exception is FileNotFoundException or DirectoryNotFoundException)
            {
                try
                {
                    Directory.CreateDirectory(current);
                    attributes = File.GetAttributes(current);
                }
                catch (Exception createException) when (
                    createException is IOException or UnauthorizedAccessException)
                {
                    throw SmbNativeFailure.Translate(createException, phase);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw SmbNativeFailure.Translate(exception, phase);
            }

            SmbPathGuard.ValidateDirectoryAttributes(attributes, phase);
        }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32_768];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            throw new FileStorageAdapterException(
                BackupFileStorageFailureCode.InvalidResponse,
                BackupFileStorageFailurePhase.ResponseProcessing);
        }

        var path = new string(buffer, 0, (int)length);
        return path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)
            ? $"\\\\{path[8..]}"
            : path;
    }

    private static string? TryGetIdentity(SafeFileHandle handle)
    {
        return GetFileInformationByHandle(handle, out var information)
            ? $"{information.VolumeSerialNumber:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}"
            : null;
    }

    private static FileStorageAdapterException PathRejected(
        BackupFileStorageFailurePhase phase) => new(
        BackupFileStorageFailureCode.PathRejected,
        phase);

    private static bool IsAlreadyExists(IOException exception) =>
        (exception.HResult & 0xFFFF) is 80 or 183;

#pragma warning disable SYSLIB1054 // 受限 Win32 句柄 API，项目当前不启用 unsafe 源生成封送。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle handle,
        [Out] char[] path,
        uint length,
        uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}

[SupportedOSPlatform("windows")]
internal static class WindowsNetworkLogon
{
    public static SafeAccessTokenHandle Logon(string username, ReadOnlySpan<char> password)
    {
        var parts = username.Split('\\', 2);
        var domain = parts.Length == 2 ? parts[0] : null;
        var user = parts.Length == 2 ? parts[1] : username;
        if (string.IsNullOrWhiteSpace(user))
        {
            throw new FileStorageAdapterException(
                BackupFileStorageFailureCode.CredentialInvalid,
                BackupFileStorageFailurePhase.CredentialResolution);
        }

        var terminated = new char[password.Length + 1];
        password.CopyTo(terminated);
        var pinned = GCHandle.Alloc(terminated, GCHandleType.Pinned);
        try
        {
            if (LogonUserW(
                user,
                domain,
                pinned.AddrOfPinnedObject(),
                logonType: 9,
                logonProvider: 3,
                out var accessToken))
            {
                return accessToken;
            }

            accessToken?.Dispose();
            var error = Marshal.GetLastPInvokeError();
            throw new FileStorageAdapterException(
                error == 1326
                    ? BackupFileStorageFailureCode.AuthenticationFailed
                    : error == 5
                        ? BackupFileStorageFailureCode.AuthorizationDenied
                        : BackupFileStorageFailureCode.ConnectionFailed,
                BackupFileStorageFailurePhase.ConnectionOpen);
        }
        finally
        {
            pinned.Free();
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(terminated.AsSpan()));
        }
    }

#pragma warning disable SYSLIB1054 // LogonUserW 需要传入可清零的固定字符缓冲区。
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUserW(
        string username,
        string? domain,
        IntPtr password,
        int logonType,
        int logonProvider,
        out SafeAccessTokenHandle accessToken);
#pragma warning restore SYSLIB1054
}
