using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Infrastructure.Persistence;

internal static class BackupTaskStoreMappings
{
    public static BackupTaskStateModel ToStateModel(this BackupTask task)
    {
        return new BackupTaskStateModel(
            task.Id,
            task.Status,
            task.CurrentStage,
            task.CurrentBackupAttemptId,
            task.CancellationRequestedAtUtc,
            task.RetryCount,
            task.ErrorCode,
            task.ErrorMessage,
            task.StartedAtUtc,
            task.CompletedAtUtc,
            task.ReconciliationAttemptCount,
            task.NextReconciliationAtUtc);
    }

    public static BackupTaskSnapshotModel ToSnapshotModel(this BackupTaskSnapshot snapshot)
    {
        var remoteEndpoint = snapshot.StorageTargetId is null
            ? null
            : new BackupFileEndpointModel(
                snapshot.RemoteProtocol!.Value,
                snapshot.RemoteHost!,
                snapshot.RemotePort,
                snapshot.RemoteBasePath!,
                snapshot.RemoteCredentialReferenceId!.Value,
                snapshot.RemoteSftpHostKeyFingerprint);

        return new BackupTaskSnapshotModel(
            snapshot.TaskId,
            snapshot.PolicyName,
            new BackupTaskIdentityModel(
                snapshot.ServerId,
                snapshot.ServerName,
                snapshot.InstanceId,
                snapshot.InstanceName,
                snapshot.DatabaseId,
                snapshot.DatabaseName),
            new BackupSqlTargetModel(
                snapshot.ConnectionAddress,
                snapshot.SqlCredentialReferenceId,
                snapshot.EncryptConnection,
                snapshot.TrustServerCertificate,
                snapshot.CertificateTrustReason,
                snapshot.ConnectionTimeoutSeconds, snapshot.AllowLegacyTls, snapshot.LegacyTlsReason),
            snapshot.LocalSqlBackupRootPath,
            snapshot.FileNameRuleVersion,
            new BackupFileEndpointModel(
                snapshot.SourceAccessProtocol,
                snapshot.SourceAccessHost,
                snapshot.SourceAccessPort,
                snapshot.SourceAccessBasePath,
                snapshot.SourceCredentialReferenceId,
                snapshot.SourceSftpHostKeyFingerprint),
            new BackupTaskPolicyModel(
                snapshot.StorageMode,
                snapshot.StorageTargetId,
                remoteEndpoint,
                snapshot.LocalRetentionDays,
                snapshot.RemoteRetentionDays,
                snapshot.UseChecksum,
                snapshot.UseCompression,
                snapshot.UseCopyOnly,
                snapshot.BackupTimeoutMinutes,
                snapshot.VerifyTimeoutMinutes,
                snapshot.TransferTimeoutMinutes,
                snapshot.TimeZoneId),
            snapshot.BackupType,
            snapshot.Purpose);
    }

    public static BackupAttemptModel ToAttemptModel(this BackupAttempt attempt)
    {
        return new BackupAttemptModel(
            attempt.Id,
            attempt.AttemptNumber,
            attempt.BackupInvocationStatus,
            attempt.LocalSqlFilePath,
            attempt.WorkerSourceFilePath,
            attempt.RemoteStorageTargetId,
            attempt.RemotePartialFilePath,
            attempt.RemoteFinalFilePath,
            attempt.SourceLengthBytes,
            attempt.LocalVerifiedAtUtc,
            attempt.RemoteValidatedAtUtc,
            attempt.LocalCleanupCompletedAtUtc,
            attempt.RowVersion, attempt.ExpectedDatabaseGuid, attempt.ExpectedFamilyGuid,
            attempt.AdmittedFullBackupSetId, attempt.AdmissionRecoveryForkId, attempt.AdmissionObservedAtUtc);
    }

    public static LeaseHandle ToLeaseHandle(this BackupTask task)
    {
        if (task.CurrentStage is null
            || task.CurrentBackupAttemptId is null
            || task.LeaseToken is null
            || task.LeasePurpose is null
            || task.LeaseExpiresAtUtc is null)
        {
            throw new InvalidOperationException("任务没有完整的活动租约，不能创建 LeaseHandle。");
        }

        return new LeaseHandle(
            task.Id,
            task.LeaseToken.Value,
            task.LeasePurpose.Value,
            task.CurrentStage.Value,
            task.CurrentBackupAttemptId.Value,
            task.LeaseExpiresAtUtc.Value,
            task.RowVersion);
    }
}
