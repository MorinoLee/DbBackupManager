using System.Diagnostics.CodeAnalysis;
using System.Text;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed record BackupPlanPathInput(
    BackupTaskIdentitySnapshot Identity,
    BackupSourceSnapshot Source,
    BackupRemoteTargetSnapshot? RemoteTarget,
    BackupRunPurpose Purpose);

internal static class BackupTaskPathFactory
{
    internal const string CurrentVersion = "v2";
    internal const string PlanVersion = "v3";
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

        if (snapshot.BackupType != BackupType.Full)
        {
            throw new InvalidOperationException("旧任务快照只支持完整备份。");
        }

        return CreatePaths(
            new(snapshot.ServerId, snapshot.ServerName, snapshot.InstanceId, snapshot.InstanceName, snapshot.DatabaseId, snapshot.DatabaseName),
            new(snapshot.LocalSqlBackupRootPath, snapshot.FileNameRuleVersion,
                new(snapshot.SourceAccessProtocol, snapshot.SourceAccessHost, snapshot.SourceAccessPort, snapshot.SourceAccessBasePath,
                    snapshot.SourceCredentialReferenceId, snapshot.SourceSftpHostKeyFingerprint)),
            snapshot.StorageTargetId is { } targetId
                ? new(targetId, new(snapshot.RemoteProtocol!.Value, snapshot.RemoteHost!, snapshot.RemotePort, snapshot.RemoteBasePath!,
                    snapshot.RemoteCredentialReferenceId!.Value, snapshot.RemoteSftpHostKeyFingerprint))
                : null,
            BackupTypeSegment(snapshot.BackupType),
            $"{preparedAtUtc:yyyyMMddHHmmss}_{attemptId:N}.bak");
    }

    public static BackupAttemptPaths Create(BackupPlanPathInput input, Guid attemptId, DateTimeOffset preparedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Identity);
        ArgumentNullException.ThrowIfNull(input.Source);
        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException("备份尝试标识不能为空。", nameof(attemptId));
        }

        if (preparedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("备份路径时间必须使用 UTC。", nameof(preparedAtUtc));
        }

        if (input.Source.FileNameRuleVersion != PlanVersion)
        {
            throw new InvalidOperationException("计划备份路径必须显式选用 v3 规则。");
        }

        var backupType = BackupPlanRules.ToBackupType(input.Purpose);
        if (backupType == BackupType.Log)
        {
            throw new InvalidOperationException("当前路径规则尚不支持日志备份。");
        }

        var suffix = BackupPlanRules.UseCopyOnly(input.Purpose) ? "COPYONLY"
            : backupType == BackupType.Differential ? "DIFF" : "FULL";
        return CreatePaths(input.Identity, input.Source, input.RemoteTarget, BackupTypeSegment(backupType),
            $"{preparedAtUtc:yyyyMMddHHmmss}_{attemptId:N}_{suffix}.bak");
    }

    private static BackupAttemptPaths CreatePaths(
        BackupTaskIdentitySnapshot identity, BackupSourceSnapshot source, BackupRemoteTargetSnapshot? remote,
        string typeSegment, string fileName)
    {
        var relativeSegments = new[]
        {
            BuildIdentitySegment(identity.ServerName, identity.ServerId),
            BuildIdentitySegment(identity.InstanceName, identity.InstanceId),
            BuildIdentitySegment(identity.DatabaseName, identity.DatabaseId),
            typeSegment,
        };
        var localPath = BuildLocalPath(source.LocalSqlBackupRootPath, relativeSegments, fileName);
        var endpoint = source.WorkerAccess;
        var workerPath = BuildEndpointPath(endpoint.Protocol, endpoint.Host, endpoint.BasePath, relativeSegments, fileName);
        if (remote is null)
        {
            return new BackupAttemptPaths(localPath, workerPath, null, null, null);
        }

        var target = remote.Endpoint;
        var remoteFinal = BuildEndpointPath(target.Protocol, target.Host, target.BasePath, relativeSegments, fileName);
        var remotePartial = $"{remoteFinal}.part";
        EnsureLength(remotePartial, MaximumStoredPathLength);
        return new BackupAttemptPaths(localPath, workerPath, remote.StorageTargetId, remotePartial, remoteFinal);
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
        BackupType.Differential => "diff",
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
