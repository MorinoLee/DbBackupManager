using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.BackupManagement;
using DbBackupManager.Infrastructure.FileStorage;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.BackupExecution;

public static class BackupExecutionRegistration
{
    public static IServiceCollection AddBackupExecution(this IServiceCollection services)
    {
        services.AddBackupFileStorage();
        services.AddScoped<IBackupExecutionGuard, BackupExecutionGuard>();
        services.AddScoped<IBackupSourceProbe, SmbBackupSourceProbe>();
        services.AddScoped<IBackupReconciliationEvidenceProbe, SqlSmbBackupReconciliationEvidenceProbe>();
        services.AddScoped<IBackupTaskRecovery, BackupTaskRecovery>();
        services.AddSingleton<IWorkerStartupDiagnostics, WorkerStartupDiagnostics>();
        services.AddScoped<BackupTaskRunner>();
        services.AddScoped<IBackupFileRetentionStore, BackupFileRetentionStore>();
        services.AddScoped<BackupFileRetentionRunner>();
        services.AddScoped<ISchedulableBackupPolicyReader, SchedulableBackupPolicyReader>();
        services.AddScoped<IBackupTaskScheduler, BackupTaskScheduler>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(BackupRunnerOptions.Default);
        services.AddSingleton(BackupReconciliationOptions.Default);
        services.AddSingleton(BackupFileRetentionOptions.Default);
        services.AddSingleton(BackupScheduleWorkerOptions.Default);
        return services;
    }
}

internal sealed class BackupExecutionGuard(IDbContextFactory<PlatformDbContext> factory) : IBackupExecutionGuard
{
    public async Task<bool> IsEnabledAsync(
        BackupTaskSnapshotModel snapshot,
        CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await context.DatabaseServers.AnyAsync(
                server => server.Id == snapshot.Identity.ServerId && server.IsEnabled,
                cancellationToken)
            && await context.DatabaseInstances.AnyAsync(
                instance => instance.Id == snapshot.Identity.InstanceId && instance.IsEnabled,
                cancellationToken)
            && await context.CredentialReferences.AnyAsync(
                credential => credential.Id == snapshot.SqlTarget.SqlCredentialReferenceId
                    && credential.IsEnabled
                    && credential.Kind == CredentialKind.SqlPassword,
                cancellationToken)
            && await MatchesFileCredentialAsync(
                context,
                snapshot.WorkerSourceEndpoint,
                cancellationToken)
            && await MatchesRemoteAsync(context, snapshot, cancellationToken);
    }

    private static async Task<bool> MatchesRemoteAsync(
        PlatformDbContext context,
        BackupTaskSnapshotModel snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Policy.StorageMode == BackupStorageMode.LocalOnly)
        {
            return true;
        }

        if (snapshot.Policy.StorageTargetId is null || snapshot.Policy.RemoteEndpoint is null)
        {
            return false;
        }

        return await context.StorageTargets.AnyAsync(
                target => target.Id == snapshot.Policy.StorageTargetId
                    && target.IsEnabled,
                cancellationToken)
            && await MatchesFileCredentialAsync(context, snapshot.Policy.RemoteEndpoint, cancellationToken);
    }

    private static Task<bool> MatchesFileCredentialAsync(
        PlatformDbContext context,
        BackupFileEndpointModel endpoint,
        CancellationToken cancellationToken) =>
        context.CredentialReferences.AnyAsync(
            credential => credential.Id == endpoint.CredentialReferenceId
                && credential.IsEnabled
                && (endpoint.Protocol == FileTransferProtocol.Smb
                    ? credential.Kind == CredentialKind.SmbPassword
                    : credential.Kind == CredentialKind.SftpPassword
                        || credential.Kind == CredentialKind.SftpPrivateKey),
            cancellationToken);
}

internal sealed class SmbBackupSourceProbe(IBackupFileStorageProbe fileStorage) : IBackupSourceProbe
{
    internal SmbBackupSourceProbe(
        IDbContextFactory<PlatformDbContext> factory,
        IConfiguration configuration)
        : this(new BackupFileStorageAdapter(
        [
            new SmbFileStorageSessionFactory(
                new FileStorageCredentialResolver(factory, configuration)),
        ]))
    {
    }

    public async Task<BackupFileProbeResult> InspectAsync(
        BackupTaskSnapshotModel snapshot,
        BackupAttemptModel attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidatePath(snapshot, attempt);
            var endpoint = CreateEndpoint(snapshot.WorkerSourceEndpoint);
            var result = await fileStorage.InspectAsync(
                endpoint,
                attempt.WorkerSourceFilePath,
                cancellationToken);
            if (!result.IsSucceeded)
            {
                return new BackupFileProbeResult(
                    false,
                    false,
                    null,
                    MapFailure(result.Failure!.Code));
            }

            if (result.Value!.Exists && !result.Value.IsRegularFile)
            {
                return new BackupFileProbeResult(
                    false,
                    false,
                    null,
                    "smb_source_not_regular");
            }

            return new BackupFileProbeResult(
                true,
                result.Value.Exists,
                result.Value.LengthBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return new BackupFileProbeResult(false, false, null, "smb_path_rejected");
        }
    }

    internal static void ValidatePath(
        BackupTaskSnapshotModel snapshot,
        BackupAttemptModel attempt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateAttemptFileName(attempt);
        var endpoint = snapshot.WorkerSourceEndpoint;
        var input = CreateEndpoint(endpoint);
        var localRoot = snapshot.LocalSqlBackupRootPath.TrimEnd('\\', '/');
        var prefix = $"{localRoot}\\";
        if (!attempt.LocalSqlFilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("本地备份路径必须位于快照根目录下。", nameof(attempt));
        }

        var segments = attempt.LocalSqlFilePath[prefix.Length..].Split('\\');
        var expected = BackupFileEndpointInput.CombineChildPath(
            endpoint.Protocol,
            endpoint.Host,
            endpoint.BasePath,
            segments);
        if (endpoint.Protocol == FileTransferProtocol.Smb)
        {
            _ = SmbPathGuard.ValidateExactFilePath(input, attempt.WorkerSourceFilePath);
            if (!string.Equals(
                attempt.WorkerSourceFilePath,
                expected,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("工作端路径必须与 SQL 路径使用相同相对层级。", nameof(attempt));
            }

            return;
        }

        _ = SftpPathGuard.ValidateExactFilePath(input, attempt.WorkerSourceFilePath);
        if (!string.Equals(attempt.WorkerSourceFilePath, expected, StringComparison.Ordinal))
        {
            throw new ArgumentException("工作端路径必须与 SQL 路径使用相同相对层级。", nameof(attempt));
        }
    }

    internal static void ValidateLegacyPath(
        BackupFileEndpointModel endpoint,
        BackupAttemptModel attempt)
    {
        var input = CreateEndpoint(endpoint);
        ValidateAttemptFileName(attempt);
        var name = attempt.LocalSqlFilePath.Split('\\', '/').Last();

        if (endpoint.Protocol == FileTransferProtocol.Smb)
        {
            _ = SmbPathGuard.ValidateExactFilePath(input, attempt.WorkerSourceFilePath);
            if (!string.Equals(
                attempt.WorkerSourceFilePath,
                $"\\\\{endpoint.Host}\\{endpoint.BasePath}\\{name}",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("暂存文件必须属于当前 Attempt。", nameof(attempt));
            }

            return;
        }

        _ = SftpPathGuard.ValidateExactFilePath(input, attempt.WorkerSourceFilePath);
        if (!string.Equals(
            attempt.WorkerSourceFilePath,
            $"{endpoint.BasePath.TrimEnd('/')}/{name}",
            StringComparison.Ordinal))
        {
            throw new ArgumentException("暂存文件必须属于当前 Attempt。", nameof(attempt));
        }
    }

    private static void ValidateAttemptFileName(BackupAttemptModel attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var name = attempt.LocalSqlFilePath.Split('\\', '/').Last();
        if (!name.EndsWith($"_{attempt.Id:N}.bak", StringComparison.Ordinal))
        {
            throw new ArgumentException("暂存文件必须属于当前 Attempt。", nameof(attempt));
        }
    }

    private static BackupFileEndpointInput CreateEndpoint(BackupFileEndpointModel endpoint) =>
        new(
            endpoint.Protocol,
            endpoint.Host,
            endpoint.Port,
            endpoint.BasePath,
            endpoint.CredentialReferenceId,
            endpoint.SftpHostKeyFingerprint);

    private static string MapFailure(BackupFileStorageFailureCode code) => code switch
    {
        BackupFileStorageFailureCode.PlatformUnsupported => "smb_platform_unsupported",
        BackupFileStorageFailureCode.CredentialUnavailable => "smb_credential_reference_invalid",
        BackupFileStorageFailureCode.CredentialProtectionUnavailable => "smb_key_ring_unavailable",
        BackupFileStorageFailureCode.CredentialInvalid => "smb_credential_decryption_failed",
        BackupFileStorageFailureCode.AuthenticationFailed => "smb_authentication_failed",
        BackupFileStorageFailureCode.AuthorizationDenied => "smb_authorization_denied",
        BackupFileStorageFailureCode.HostKeyMismatch => "sftp_host_key_mismatch",
        BackupFileStorageFailureCode.PathRejected => "smb_path_rejected",
        BackupFileStorageFailureCode.FileNotFound => "smb_source_missing",
        _ => "smb_source_unavailable",
    };
}
