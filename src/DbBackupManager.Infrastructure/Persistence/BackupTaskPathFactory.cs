using System.Diagnostics.CodeAnalysis;
using System.Text;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Infrastructure.Persistence;

internal static class BackupTaskPathFactory
{
    internal const string CurrentVersion = "v2";
    internal const int MaximumSegmentLength = 80;
    internal const int MaximumSqlServerBackupPathLength = 259;
    internal const int MaximumStoredPathLength = 2_048;

    public static bool TryCreate(
        BackupTaskSnapshot snapshot,
        Guid attemptId,
        DateTimeOffset preparedAtUtc,
        [NotNullWhen(true)] out BackupAttemptPaths? paths)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException("备份尝试标识不能为空。", nameof(attemptId));
        }

        try
        {
            paths = Create(snapshot, attemptId, preparedAtUtc);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            paths = null;
            return false;
        }
    }

    public static BackupAttemptPaths Create(
        BackupTaskSnapshot snapshot,
        Guid attemptId,
        DateTimeOffset preparedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException("备份尝试标识不能为空。", nameof(attemptId));
        }

        if (!string.Equals(snapshot.FileNameRuleVersion, CurrentVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("任务快照包含不支持的备份路径规则版本。");
        }

        return CreateCurrent(snapshot, $"{preparedAtUtc:yyyyMMddHHmmss}_{attemptId:N}.bak");
    }

    private static BackupAttemptPaths CreateCurrent(
        BackupTaskSnapshot snapshot,
        string fileName)
    {
        var relativeSegments = new[]
        {
            BuildIdentitySegment(snapshot.ServerName, snapshot.ServerId),
            BuildIdentitySegment(snapshot.InstanceName, snapshot.InstanceId),
            BuildIdentitySegment(snapshot.DatabaseName, snapshot.DatabaseId),
            BackupTypeSegment(snapshot.BackupType),
        };
        var localPath = BuildLocalPath(snapshot.LocalSqlBackupRootPath, relativeSegments, fileName);
        var workerPath = BuildEndpointPath(
            snapshot.SourceAccessProtocol,
            snapshot.SourceAccessHost,
            snapshot.SourceAccessBasePath,
            relativeSegments,
            fileName);

        if (snapshot.StorageTargetId is null)
        {
            return new BackupAttemptPaths(localPath, workerPath, null, null, null);
        }

        var remoteFinal = BuildEndpointPath(
            snapshot.RemoteProtocol!.Value,
            snapshot.RemoteHost!,
            snapshot.RemoteBasePath!,
            relativeSegments,
            fileName);
        var remotePartial = $"{remoteFinal}.part";
        EnsureLength(remotePartial, MaximumStoredPathLength);
        return new BackupAttemptPaths(
            localPath,
            workerPath,
            snapshot.StorageTargetId,
            remotePartial,
            remoteFinal);
    }

    private static string BuildLocalPath(
        string root,
        IReadOnlyList<string> relativeSegments,
        string fileName)
    {
        var result = Combine(root, string.Join('\\', relativeSegments.Append(fileName)), '\\');
        EnsureLength(result, MaximumSqlServerBackupPathLength);
        return result;
    }

    private static string BuildEndpointPath(
        FileTransferProtocol protocol,
        string host,
        string basePath,
        IReadOnlyList<string> relativeSegments,
        string fileName)
    {
        if (!Enum.IsDefined(protocol))
        {
            throw new InvalidOperationException("任务快照包含不支持的文件协议。");
        }

        var result = BackupFileEndpointInput.CombineChildPath(
            protocol,
            host,
            basePath,
            relativeSegments.Append(fileName).ToArray());
        EnsureLength(result, MaximumStoredPathLength);
        return result;
    }

    internal static string BuildIdentitySegment(string value, Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("路径身份标识不能为空。", nameof(id));
        }

        const int suffixLength = 10;
        var safe = SanitizeFileSegment(value);
        var maximumPrefixLength = MaximumSegmentLength - suffixLength;
        if (safe.Length > maximumPrefixLength)
        {
            safe = safe[..maximumPrefixLength];
        }

        safe = safe.TrimEnd('.', ' ');
        if (string.IsNullOrEmpty(safe))
        {
            safe = "item";
        }

        return $"{safe}--{id:N}"[..(safe.Length + suffixLength)];
    }

    private static string BackupTypeSegment(BackupType backupType) => backupType switch
    {
        BackupType.Full => "full",
        _ => throw new InvalidOperationException("任务快照包含不支持的备份类型。"),
    };

    private static string Combine(string root, string fileName, char separator) =>
        $"{root.TrimEnd('\\', '/')}{separator}{fileName}";

    private static string SanitizeFileSegment(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        var result = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            result.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '_');
        }
        var safe = result.ToString().TrimEnd('.', ' ');
        return string.IsNullOrEmpty(safe) ? "item" : safe;
    }

    private static void EnsureLength(string path, int maximumLength)
    {
        if (path.Length > maximumLength)
        {
            throw new InvalidOperationException("生成的备份路径超过支持的长度。");
        }
    }
}
