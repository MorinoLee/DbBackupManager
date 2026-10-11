using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.FileStorage;

public enum BackupFileStorageOutcome
{
    Succeeded,
    ConfirmedFailed,
    Indeterminate,
}

public enum BackupFileStorageFailurePhase
{
    RequestValidation,
    CredentialResolution,
    ConnectionOpen,
    SourceInspection,
    SourceOpen,
    DirectoryPrepare,
    TargetCreate,
    DataTransfer,
    TargetFlush,
    Rename,
    Delete,
    ResponseProcessing,
}

public enum BackupFileStorageFailureCode
{
    InvalidRequest,
    PlatformUnsupported,
    CredentialUnavailable,
    CredentialProtectionUnavailable,
    CredentialInvalid,
    AuthenticationFailed,
    AuthorizationDenied,
    HostKeyMismatch,
    ConnectionFailed,
    PathRejected,
    FileNotFound,
    FileAlreadyExists,
    NotRegularFile,
    SourceChanged,
    LengthMismatch,
    TimedOut,
    Cancelled,
    ConnectionInterrupted,
    OperationRejected,
    InvalidResponse,
}

public sealed record BackupFileStorageFailure(
    BackupFileStorageFailureCode Code,
    BackupFileStorageFailurePhase Phase);

public sealed class BackupFileStorageResult<T>
    where T : class
{
    internal BackupFileStorageResult(
        BackupFileStorageOutcome outcome,
        T? value,
        BackupFileStorageFailure? failure)
    {
        if (outcome == BackupFileStorageOutcome.Succeeded)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (failure is not null)
            {
                throw new ArgumentException("成功结果不能包含失败分类。", nameof(failure));
            }
        }
        else
        {
            ArgumentNullException.ThrowIfNull(failure);
            if (value is not null)
            {
                throw new ArgumentException("失败或不确定结果不能包含成功值。", nameof(value));
            }
        }

        Outcome = outcome;
        Value = value;
        Failure = failure;
    }

    public BackupFileStorageOutcome Outcome { get; }

    public T? Value { get; }

    public BackupFileStorageFailure? Failure { get; }

    public bool IsSucceeded => Outcome == BackupFileStorageOutcome.Succeeded;
}

public static class BackupFileStorageResult
{
    public static BackupFileStorageResult<T> Succeeded<T>(T value)
        where T : class =>
        new(BackupFileStorageOutcome.Succeeded, value, null);

    public static BackupFileStorageResult<T> ConfirmedFailure<T>(
        BackupFileStorageFailureCode code,
        BackupFileStorageFailurePhase phase)
        where T : class =>
        new(
            BackupFileStorageOutcome.ConfirmedFailed,
            null,
            new BackupFileStorageFailure(code, phase));

    public static BackupFileStorageResult<T> Indeterminate<T>(
        BackupFileStorageFailureCode code,
        BackupFileStorageFailurePhase phase)
        where T : class =>
        new(
            BackupFileStorageOutcome.Indeterminate,
            null,
            new BackupFileStorageFailure(code, phase));
}

public sealed class BackupFileEndpointInput
{
    public BackupFileEndpointInput(
        FileTransferProtocol protocol,
        string host,
        int? port,
        string rootPath,
        Guid credentialReferenceId,
        string? sftpHostKeyFingerprint)
    {
        if (!Enum.IsDefined(protocol))
        {
            throw new ArgumentOutOfRangeException(nameof(protocol));
        }

        Protocol = protocol;
        Host = BackupFileStorageValues.Host(host, protocol, nameof(host));
        RootPath = BackupFileStorageValues.RootPath(rootPath, protocol, nameof(rootPath));
        CredentialReferenceId = BackupFileStorageValues.Id(
            credentialReferenceId,
            nameof(credentialReferenceId));

        if (protocol == FileTransferProtocol.Smb)
        {
            if (port is not null || !string.IsNullOrWhiteSpace(sftpHostKeyFingerprint))
            {
                throw new ArgumentException("SMB 端点不能配置端口或 SFTP 主机密钥指纹。");
            }

            return;
        }

        Port = port is > 0 and <= 65_535
            ? port
            : throw new ArgumentOutOfRangeException(nameof(port));
        SftpHostKeyFingerprint = BackupFileStorageValues.Sha256Fingerprint(
            sftpHostKeyFingerprint,
            nameof(sftpHostKeyFingerprint));
    }

    public FileTransferProtocol Protocol { get; }

    public string Host { get; }

    public int? Port { get; }

    public string RootPath { get; }

    public Guid CredentialReferenceId { get; }

    public string? SftpHostKeyFingerprint { get; }

    public string ChildFile(string fileName) =>
        CombineChildFile(Protocol, Host, RootPath, fileName);

    public static string CombineChildFile(
        FileTransferProtocol protocol,
        string host,
        string rootPath,
        string fileName) =>
        BackupFileStorageValues.ChildFilePath(protocol, host, rootPath, fileName);

    public static string CombineChildPath(
        FileTransferProtocol protocol,
        string host,
        string rootPath,
        IReadOnlyList<string> segments) =>
        BackupFileStorageValues.ChildPath(protocol, host, rootPath, segments);
}

public sealed class BackupFileMetadata
{
    public BackupFileMetadata(
        bool exists,
        bool isRegularFile,
        long? lengthBytes,
        string? identity = null)
    {
        if (!exists && (isRegularFile || lengthBytes is not null || identity is not null))
        {
            throw new ArgumentException("不存在的路径不能包含文件元数据。", nameof(exists));
        }

        if (exists && isRegularFile != (lengthBytes is not null))
        {
            throw new ArgumentException("普通文件必须且只能包含长度。", nameof(lengthBytes));
        }

        if (lengthBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthBytes));
        }

        Exists = exists;
        IsRegularFile = isRegularFile;
        LengthBytes = lengthBytes;
        Identity = identity is null
            ? null
            : BackupFileStorageValues.Text(identity, 256, nameof(identity));
    }

    public bool Exists { get; }

    public bool IsRegularFile { get; }

    public long? LengthBytes { get; }

    public string? Identity { get; }
}

public sealed class BackupFileTransferRequest
{
    public BackupFileTransferRequest(
        BackupFileEndpointInput sourceEndpoint,
        string sourcePath,
        BackupFileEndpointInput targetEndpoint,
        string targetPartialPath,
        long expectedLengthBytes,
        int timeoutSeconds)
    {
        SourceEndpoint = sourceEndpoint
            ?? throw new ArgumentNullException(nameof(sourceEndpoint));
        TargetEndpoint = targetEndpoint
            ?? throw new ArgumentNullException(nameof(targetEndpoint));
        SourcePath = BackupFileStorageValues.ExactPath(sourcePath, nameof(sourcePath));
        TargetPartialPath = BackupFileStorageValues.ExactPath(
            targetPartialPath,
            nameof(targetPartialPath));
        if (!TargetPartialPath.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("传输目标必须使用任务专属 .part 路径。", nameof(targetPartialPath));
        }

        ExpectedLengthBytes = BackupFileStorageValues.PositiveLength(
            expectedLengthBytes,
            nameof(expectedLengthBytes));
        TimeoutSeconds = BackupFileStorageValues.Timeout(timeoutSeconds, nameof(timeoutSeconds));
    }

    public BackupFileEndpointInput SourceEndpoint { get; }

    public string SourcePath { get; }

    public BackupFileEndpointInput TargetEndpoint { get; }

    public string TargetPartialPath { get; }

    public long ExpectedLengthBytes { get; }

    public int TimeoutSeconds { get; }
}

public sealed class BackupDirectoryPreparationRequest
{
    public BackupDirectoryPreparationRequest(
        BackupFileEndpointInput endpoint,
        string filePath,
        int timeoutSeconds)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        FilePath = BackupFileStorageValues.ExactPath(filePath, nameof(filePath));
        TimeoutSeconds = BackupFileStorageValues.Timeout(timeoutSeconds, nameof(timeoutSeconds));
    }

    public BackupFileEndpointInput Endpoint { get; }

    public string FilePath { get; }

    public int TimeoutSeconds { get; }
}

public sealed class BackupFileRenameRequest
{
    public BackupFileRenameRequest(
        BackupFileEndpointInput endpoint,
        string partialPath,
        string finalPath,
        long expectedLengthBytes,
        int timeoutSeconds)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        PartialPath = BackupFileStorageValues.ExactPath(partialPath, nameof(partialPath));
        FinalPath = BackupFileStorageValues.ExactPath(finalPath, nameof(finalPath));
        if (!PartialPath.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("重命名源必须使用任务专属 .part 路径。", nameof(partialPath));
        }

        if (!FinalPath.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("远程最终文件必须使用 .bak 路径。", nameof(finalPath));
        }

        ExpectedLengthBytes = BackupFileStorageValues.PositiveLength(
            expectedLengthBytes,
            nameof(expectedLengthBytes));
        TimeoutSeconds = BackupFileStorageValues.Timeout(timeoutSeconds, nameof(timeoutSeconds));
    }

    public BackupFileEndpointInput Endpoint { get; }

    public string PartialPath { get; }

    public string FinalPath { get; }

    public long ExpectedLengthBytes { get; }

    public int TimeoutSeconds { get; }
}

public sealed class BackupFileDeleteRequest
{
    public BackupFileDeleteRequest(
        BackupFileEndpointInput endpoint,
        string path,
        long expectedLengthBytes,
        string? expectedIdentity,
        int timeoutSeconds,
        BackupArtifactDeletionOwner? owner = null)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        Path = BackupFileStorageValues.ExactPath(path, nameof(path));
        ExpectedLengthBytes = BackupFileStorageValues.PositiveLength(
            expectedLengthBytes,
            nameof(expectedLengthBytes));
        ExpectedIdentity = expectedIdentity is null
            ? null
            : BackupFileStorageValues.Text(expectedIdentity, 256, nameof(expectedIdentity));
        TimeoutSeconds = BackupFileStorageValues.Timeout(timeoutSeconds, nameof(timeoutSeconds));
        Owner = owner;
    }

    public BackupFileEndpointInput Endpoint { get; }

    public string Path { get; }

    public long ExpectedLengthBytes { get; }

    public string? ExpectedIdentity { get; }
    public BackupArtifactDeletionOwner? Owner { get; }

    public int TimeoutSeconds { get; }
}

public sealed record BackupFileTransferReceipt(long LengthBytes);

public sealed record BackupFileMutationReceipt
{
    public static BackupFileMutationReceipt Instance { get; } = new();
}

internal static class BackupFileStorageValues
{
    public static Guid Id(Guid value, string parameterName) => value == Guid.Empty
        ? throw new ArgumentException("标识不能为空。", parameterName)
        : value;

    public static string Host(
        string? value,
        FileTransferProtocol protocol,
        string parameterName)
    {
        var host = Text(value, 255, parameterName);
        if (host.IndexOfAny(['/', '\\']) >= 0
            || protocol == FileTransferProtocol.Smb && host.IndexOfAny([':', '*', '?']) >= 0)
        {
            throw new ArgumentException("文件端点主机格式无效。", parameterName);
        }

        return host;
    }

    public static string RootPath(
        string? value,
        FileTransferProtocol protocol,
        string parameterName)
    {
        var path = Text(value, 2_048, parameterName);
        var separator = protocol == FileTransferProtocol.Smb ? '\\' : '/';
        var forbiddenSeparator = protocol == FileTransferProtocol.Smb ? '/' : '\\';
        if (path.Contains(forbiddenSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException("根路径使用了错误的协议分隔符。", parameterName);
        }

        if (protocol == FileTransferProtocol.Smb)
        {
            if (path.StartsWith('\\') || path.EndsWith('\\'))
            {
                throw new ArgumentException("SMB 根路径必须是共享名及其可选子目录。", parameterName);
            }
        }
        else if (!path.StartsWith('/') || path == "/" || path.EndsWith('/'))
        {
            throw new ArgumentException("SFTP 根路径必须是非根绝对目录。", parameterName);
        }

        var parts = path.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => InvalidPathSegment(part, protocol)))
        {
            throw new ArgumentException("根路径包含不安全的路径段。", parameterName);
        }

        return protocol == FileTransferProtocol.Smb
            ? string.Join('\\', parts)
            : $"/{string.Join('/', parts)}";
    }

    public static string FileName(
        string? value,
        FileTransferProtocol protocol,
        string parameterName)
    {
        var name = Text(value, 255, parameterName);
        if (name.IndexOfAny(['/', '\\']) >= 0 || InvalidPathSegment(name, protocol))
        {
            throw new ArgumentException("文件名必须是单个安全路径段。", parameterName);
        }

        return name;
    }

    public static string ChildFilePath(
        FileTransferProtocol protocol,
        string host,
        string rootPath,
        string fileName) =>
        ChildPath(protocol, host, rootPath, [fileName]);

    public static string ChildPath(
        FileTransferProtocol protocol,
        string host,
        string rootPath,
        IReadOnlyList<string> segments)
    {
        if (!Enum.IsDefined(protocol))
        {
            throw new ArgumentOutOfRangeException(nameof(protocol));
        }

        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            throw new ArgumentException("子路径至少需要一个路径段。", nameof(segments));
        }

        var normalizedHost = Host(host, protocol, nameof(host));
        var normalizedRoot = RootPath(rootPath, protocol, nameof(rootPath));
        var normalizedSegments = segments
            .Select((segment, index) => FileName(segment, protocol, $"{nameof(segments)}[{index}]"))
            .ToArray();
        var result = protocol == FileTransferProtocol.Smb
            ? $"\\\\{normalizedHost}\\{normalizedRoot}\\{string.Join('\\', normalizedSegments)}"
            : $"{normalizedRoot}/{string.Join('/', normalizedSegments)}";
        return Text(result, 2_048, nameof(segments));
    }

    public static string ExactPath(string? value, string parameterName) =>
        Text(value, 2_048, parameterName);

    public static long PositiveLength(long value, string parameterName) => value > 0
        ? value
        : throw new ArgumentOutOfRangeException(parameterName);

    public static int Timeout(int value, string parameterName) => value is > 0 and <= 86_400
        ? value
        : throw new ArgumentOutOfRangeException(parameterName);

    public static string Sha256Fingerprint(string? value, string parameterName)
    {
        var fingerprint = Text(value, 100, parameterName);
        const string prefix = "SHA256:";
        if (!fingerprint.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("SFTP 主机密钥仅接受 SHA-256 指纹。", parameterName);
        }

        var encoded = fingerprint[prefix.Length..].TrimEnd('=');
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
        }
        catch (FormatException)
        {
            throw new ArgumentException("SFTP 主机密钥指纹格式无效。", parameterName);
        }

        if (bytes.Length != 32)
        {
            throw new ArgumentException("SFTP 主机密钥指纹必须是 SHA-256 值。", parameterName);
        }

        return $"{prefix}{Convert.ToBase64String(bytes).TrimEnd('=')}";
    }

    public static string Text(string? value, int maximumLength, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)
            || normalized.Length > maximumLength
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("文本不能为空、超长或包含控制字符。", parameterName);
        }

        return normalized;
    }

    private static bool InvalidPathSegment(string segment, FileTransferProtocol protocol) =>
        segment is "." or ".."
        || protocol == FileTransferProtocol.Smb
            && (segment.EndsWith('.')
                || segment.EndsWith(' ')
                || segment.IndexOfAny([':', '*', '?', '<', '>', '|', '"']) >= 0);
}
