using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed record BackupTaskConfigurationSnapshot(
    BackupTaskIdentitySnapshot Identity, BackupSqlTargetSnapshot SqlTarget,
    BackupSourceSnapshot Source, BackupRemoteTargetSnapshot? RemoteTarget);

internal static class BackupTaskConfigurationReader
{
    internal static async Task<BackupTaskConfigurationSnapshot?> ReadAsync(
        PlatformDbContext context, Guid databaseId, Guid? storageTargetId,
        string fileNameRuleVersion, CancellationToken cancellationToken)
    {
        var database = await context.ManagedDatabases.SingleOrDefaultAsync(
            item => item.Id == databaseId,
            cancellationToken);
        if (database is null
            || database.IsSystemDatabase
            || !database.IsManaged
            || !database.IsAvailable)
        {
            return null;
        }

        var instance = await context.DatabaseInstances.SingleOrDefaultAsync(
            item => item.Id == database.InstanceId,
            cancellationToken);
        var server = instance is null
            ? null
            : await context.DatabaseServers.SingleOrDefaultAsync(
                item => item.Id == instance.ServerId,
                cancellationToken);
        if (instance is null || !instance.IsEnabled || server is null || !server.IsEnabled)
        {
            return null;
        }

        var sqlCredential = await context.CredentialReferences.SingleOrDefaultAsync(
            item => item.Id == instance.SqlCredentialReferenceId,
            cancellationToken);
        var sourceCredential = await context.CredentialReferences.SingleOrDefaultAsync(
            item => item.Id == server.StagingCredentialReferenceId,
            cancellationToken);
        if (!IsCredentialUsable(sqlCredential, CredentialKind.SqlPassword)
            || !IsEndpointCredentialUsable(sourceCredential, server.StagingAccessProtocol))
        {
            return null;
        }

        BackupRemoteTargetSnapshot? remoteTarget = null;
        if (storageTargetId is not null)
        {
            var target = await context.StorageTargets.SingleOrDefaultAsync(
                item => item.Id == storageTargetId.Value,
                cancellationToken);
            if (target is null || !target.IsEnabled)
            {
                return null;
            }

            var targetCredential = await context.CredentialReferences.SingleOrDefaultAsync(
                item => item.Id == target.CredentialReferenceId,
                cancellationToken);
            if (!IsEndpointCredentialUsable(targetCredential, target.Protocol))
            {
                return null;
            }

            remoteTarget = new BackupRemoteTargetSnapshot(
                target.Id,
                new FileEndpointSettings(
                    target.Protocol,
                    target.Host,
                    target.Port,
                    target.BasePath,
                    target.CredentialReferenceId,
                    target.SftpHostKeyFingerprint));
        }

        return new(
            new(server.Id, server.Name, instance.Id, instance.Name, database.Id, database.DatabaseName),
            new(instance.ConnectionAddress, instance.SqlCredentialReferenceId, instance.EncryptConnection,
                instance.TrustServerCertificate, instance.CertificateTrustReason, instance.ConnectionTimeoutSeconds,
                instance.AllowLegacyTls, instance.LegacyTlsReason),
            new(server.LocalBackupRootPath, fileNameRuleVersion,
                new(server.StagingAccessProtocol, server.StagingAccessHost, server.StagingAccessPort,
                    server.StagingAccessBasePath, server.StagingCredentialReferenceId, server.StagingSftpHostKeyFingerprint)),
            remoteTarget);
    }

    private static bool IsCredentialUsable(
        CredentialReference? credential,
        CredentialKind requiredKind)
    {
        return credential is not null && credential.IsEnabled && credential.Kind == requiredKind;
    }

    private static bool IsEndpointCredentialUsable(
        CredentialReference? credential,
        FileTransferProtocol protocol)
    {
        return credential is not null
            && credential.IsEnabled
            && protocol switch
            {
                FileTransferProtocol.Smb => credential.Kind == CredentialKind.SmbPassword,
                FileTransferProtocol.Sftp => credential.Kind is CredentialKind.SftpPassword
                    or CredentialKind.SftpPrivateKey,
                _ => false,
            };
    }
}
