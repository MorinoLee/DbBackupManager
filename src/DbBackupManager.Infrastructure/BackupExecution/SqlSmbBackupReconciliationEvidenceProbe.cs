using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Infrastructure.BackupExecution;

internal sealed class SqlSmbBackupReconciliationEvidenceProbe(
    ITargetSqlBackupEvidenceProbe backupEvidence,
    ITargetSqlReadOnlyProbe targetSql,
    IBackupSourceProbe backupFiles,
    IBackupFileStorageProbe remoteFiles,
    TimeProvider clock,
    BackupReconciliationOptions options) : IBackupReconciliationEvidenceProbe
{
    public async Task<BackupReconciliationEvidence> InspectAsync(
        BackupExecutionWorkItem workItem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        options.Validate();
        if (workItem.Lease.Stage is BackupTaskStage.Transfer
            or BackupTaskStage.ValidateCopy
            or BackupTaskStage.Cleanup)
        {
            return await InspectRemoteStageAsync(workItem, cancellationToken);
        }

        var file = await InspectStableFileAsync(workItem, cancellationToken);
        var connection = CreateConnection(workItem.Snapshot);
        var identityResult = await backupEvidence.InspectBackupIdentityAsync(
            connection,
            new TargetSqlBackupIdentityRequest(
                workItem.Snapshot.Identity.DatabaseName,
                workItem.Attempt.LocalSqlFilePath,
                workItem.Snapshot.Policy.VerifyTimeoutMinutes * 60),
            cancellationToken);
        var targetObservation = MapIdentity(identityResult);

        if (file.Artifact != BackupArtifactObservation.PresentStable
            || file.Length is null)
        {
            return Evidence(
                workItem,
                BackupReconciliationConclusion.Inconclusive,
                file.Artifact,
                targetObservation,
                BackupVerificationObservation.NotAttempted,
                file.ReasonCode ?? IdentityReason(identityResult));
        }

        if (targetObservation == TargetBackupObservation.IdentityMismatch)
        {
            return Evidence(
                workItem,
                BackupReconciliationConclusion.ConfirmedFailed,
                file.Artifact,
                targetObservation,
                BackupVerificationObservation.NotAttempted,
                "reconciliation.backup_identity_mismatch");
        }

        if (targetObservation != TargetBackupObservation.CompletedMatching)
        {
            return Evidence(
                workItem,
                BackupReconciliationConclusion.Inconclusive,
                file.Artifact,
                targetObservation,
                BackupVerificationObservation.NotAttempted,
                IdentityReason(identityResult));
        }

        if (workItem.Lease.Stage == BackupTaskStage.Backup)
        {
            return Evidence(
                workItem,
                BackupReconciliationConclusion.Succeeded,
                file.Artifact,
                targetObservation,
                BackupVerificationObservation.NotAttempted,
                "reconciliation.backup_completed");
        }

        var verification = await targetSql.VerifyBackupAsync(
            connection,
            new TargetSqlBackupVerificationRequest(
                workItem.Attempt.LocalSqlFilePath,
                workItem.Snapshot.Policy.UseChecksum,
                workItem.Snapshot.Policy.VerifyTimeoutMinutes * 60),
            cancellationToken);
        if (verification.IsSucceeded)
        {
            return Evidence(
                workItem,
                BackupReconciliationConclusion.Succeeded,
                file.Artifact,
                targetObservation,
                BackupVerificationObservation.Succeeded,
                "reconciliation.verify_succeeded",
                file.Length);
        }

        var confirmedInvalid = verification.Outcome == TargetSqlOutcome.ConfirmedFailed
            && verification.Failure?.Code == TargetSqlFailureCode.CommandRejected;
        return Evidence(
            workItem,
            confirmedInvalid
                ? BackupReconciliationConclusion.ConfirmedFailed
                : BackupReconciliationConclusion.Inconclusive,
            file.Artifact,
            targetObservation,
            confirmedInvalid
                ? BackupVerificationObservation.ConfirmedFailed
                : BackupVerificationObservation.Indeterminate,
            confirmedInvalid
                ? "reconciliation.verify_confirmed_failed"
                : "reconciliation.verify_indeterminate");
    }

    private async Task<BackupReconciliationEvidence> InspectRemoteStageAsync(
        BackupExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        if (workItem.Lease.Stage == BackupTaskStage.Cleanup)
        {
            var source = await InspectStableFileAsync(workItem, cancellationToken);
            if (source.Artifact == BackupArtifactObservation.Unknown)
            {
                return Evidence(
                    workItem,
                    BackupReconciliationConclusion.Inconclusive,
                    source.Artifact,
                    TargetBackupObservation.Unknown,
                    BackupVerificationObservation.NotAttempted,
                    source.ReasonCode ?? "reconciliation.file_probe_failed");
            }

            return source.Artifact == BackupArtifactObservation.Missing
                ? Evidence(
                    workItem,
                    BackupReconciliationConclusion.Succeeded,
                    BackupArtifactObservation.Missing,
                    TargetBackupObservation.Unknown,
                    BackupVerificationObservation.NotAttempted,
                    "reconciliation.cleanup_source_absent")
                : Evidence(
                    workItem,
                    BackupReconciliationConclusion.Inconclusive,
                    source.Artifact,
                    TargetBackupObservation.Unknown,
                    BackupVerificationObservation.NotAttempted,
                    "reconciliation.cleanup_source_remains");
        }

        if (workItem.Attempt.SourceLengthBytes is not > 0
            || workItem.Snapshot.Policy.RemoteEndpoint is null
            || string.IsNullOrWhiteSpace(workItem.Attempt.RemotePartialFilePath)
            || string.IsNullOrWhiteSpace(workItem.Attempt.RemoteFinalFilePath))
        {
            return Evidence(
                workItem,
                BackupReconciliationConclusion.Inconclusive,
                BackupArtifactObservation.Unknown,
                TargetBackupObservation.Unknown,
                BackupVerificationObservation.NotAttempted,
                "reconciliation.remote_evidence_unavailable");
        }

        var expected = workItem.Attempt.SourceLengthBytes.Value;
        var remote = new BackupFileEndpointInput(
            workItem.Snapshot.Policy.RemoteEndpoint.Protocol,
            workItem.Snapshot.Policy.RemoteEndpoint.Host,
            workItem.Snapshot.Policy.RemoteEndpoint.Port,
            workItem.Snapshot.Policy.RemoteEndpoint.BasePath,
            workItem.Snapshot.Policy.RemoteEndpoint.CredentialReferenceId,
            workItem.Snapshot.Policy.RemoteEndpoint.SftpHostKeyFingerprint);
        var part = await InspectStableRemoteAsync(
            remote, workItem.Attempt.RemotePartialFilePath, cancellationToken);
        var final = await InspectStableRemoteAsync(
            remote, workItem.Attempt.RemoteFinalFilePath, cancellationToken);
        if (part.Artifact == BackupArtifactObservation.Unknown
            || final.Artifact == BackupArtifactObservation.Unknown)
        {
            return Evidence(
                workItem,
                BackupReconciliationConclusion.Inconclusive,
                BackupArtifactObservation.Unknown,
                TargetBackupObservation.Unknown,
                BackupVerificationObservation.NotAttempted,
                part.ReasonCode ?? final.ReasonCode ?? "reconciliation.file_probe_failed");
        }

        var partComplete = part.Artifact == BackupArtifactObservation.PresentStable
            && part.Length == expected;
        var finalComplete = final.Artifact == BackupArtifactObservation.PresentStable
            && final.Length == expected;
        var partMissing = part.Artifact == BackupArtifactObservation.Missing;
        var finalMissing = final.Artifact == BackupArtifactObservation.Missing;
        var transferComplete = (partComplete && finalMissing) || (finalComplete && partMissing);
        var copyValidated = finalComplete && partMissing;
        var canSucceed = workItem.Lease.Stage == BackupTaskStage.Transfer
            ? transferComplete
            : copyValidated;
        if (canSucceed)
        {
            return Evidence(
                workItem,
                BackupReconciliationConclusion.Succeeded,
                BackupArtifactObservation.PresentStable,
                TargetBackupObservation.CompletedMatching,
                BackupVerificationObservation.NotAttempted,
                copyValidated
                    ? "reconciliation.remote_copy_complete"
                    : "reconciliation.transfer_partial_complete",
                expected);
        }

        return Evidence(
            workItem,
            BackupReconciliationConclusion.Inconclusive,
            BackupArtifactObservation.Unknown,
            TargetBackupObservation.Unknown,
            BackupVerificationObservation.NotAttempted,
            "reconciliation.remote_copy_unconfirmed");
    }

    private async Task<FileObservation> InspectStableRemoteAsync(
        BackupFileEndpointInput endpoint,
        string path,
        CancellationToken cancellationToken)
    {
        var first = await remoteFiles.InspectAsync(endpoint, path, cancellationToken);
        if (!first.IsSucceeded)
        {
            return new FileObservation(
                BackupArtifactObservation.Unknown,
                null,
                "reconciliation.file_probe_failed");
        }

        if (!first.Value!.Exists)
        {
            return new FileObservation(
                BackupArtifactObservation.Missing,
                null,
                "reconciliation.file_missing");
        }

        await Task.Delay(options.FileStabilityInterval, clock, cancellationToken);
        var second = await remoteFiles.InspectAsync(endpoint, path, cancellationToken);
        if (!second.IsSucceeded)
        {
            return new FileObservation(
                BackupArtifactObservation.Unknown,
                null,
                "reconciliation.file_probe_failed");
        }

        if (!second.Value!.Exists || first.Value.LengthBytes != second.Value.LengthBytes)
        {
            return new FileObservation(
                BackupArtifactObservation.PresentChanging,
                null,
                "reconciliation.file_changing");
        }

        return second.Value.LengthBytes > 0
            ? new FileObservation(
                BackupArtifactObservation.PresentStable,
                second.Value.LengthBytes,
                null)
            : new FileObservation(
                BackupArtifactObservation.PresentStable,
                null,
                "reconciliation.file_empty");
    }

    private async Task<FileObservation> InspectStableFileAsync(
        BackupExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        var first = await backupFiles.InspectAsync(
            workItem.Snapshot,
            workItem.Attempt,
            cancellationToken);
        if (!first.Succeeded)
        {
            return new FileObservation(
                BackupArtifactObservation.Unknown,
                null,
                "reconciliation.smb_probe_failed");
        }

        if (!first.Exists)
        {
            return new FileObservation(
                BackupArtifactObservation.Missing,
                null,
                "reconciliation.file_missing");
        }

        await Task.Delay(options.FileStabilityInterval, clock, cancellationToken);
        var second = await backupFiles.InspectAsync(
            workItem.Snapshot,
            workItem.Attempt,
            cancellationToken);
        if (!second.Succeeded)
        {
            return new FileObservation(
                BackupArtifactObservation.Unknown,
                null,
                "reconciliation.smb_probe_failed");
        }

        if (!second.Exists || first.Length != second.Length)
        {
            return new FileObservation(
                BackupArtifactObservation.PresentChanging,
                null,
                "reconciliation.file_changing");
        }

        return second.Length > 0
            ? new FileObservation(BackupArtifactObservation.PresentStable, second.Length, null)
            : new FileObservation(
                BackupArtifactObservation.PresentStable,
                null,
                "reconciliation.file_empty");
    }

    private BackupReconciliationEvidence Evidence(
        BackupExecutionWorkItem workItem,
        BackupReconciliationConclusion conclusion,
        BackupArtifactObservation artifact,
        TargetBackupObservation targetBackup,
        BackupVerificationObservation verification,
        string reasonCode,
        long? sourceLengthBytes = null)
    {
        return new BackupReconciliationEvidence(
            workItem.Task.TaskId,
            workItem.Attempt.Id,
            workItem.Lease.Stage,
            clock.GetUtcNow(),
            conclusion,
            artifact,
            targetBackup,
            verification,
            reasonCode,
            sourceLengthBytes);
    }

    private static TargetSqlConnectionInput CreateConnection(BackupTaskSnapshotModel snapshot)
    {
        return new TargetSqlConnectionInput(
            snapshot.SqlTarget.ConnectionAddress,
            snapshot.SqlTarget.SqlCredentialReferenceId,
            snapshot.SqlTarget.EncryptConnection,
            snapshot.SqlTarget.TrustServerCertificate,
            snapshot.SqlTarget.CertificateTrustReason,
            snapshot.SqlTarget.ConnectionTimeoutSeconds,
            snapshot.SqlTarget.AllowLegacyTls,
            snapshot.SqlTarget.LegacyTlsReason);
    }

    private static TargetBackupObservation MapIdentity(
        TargetSqlResult<TargetSqlBackupIdentity> result)
    {
        return result.Value?.Status switch
        {
            TargetSqlBackupIdentityStatus.NotFound => TargetBackupObservation.NotFound,
            TargetSqlBackupIdentityStatus.InProgress => TargetBackupObservation.InProgress,
            TargetSqlBackupIdentityStatus.CompletedMatching => TargetBackupObservation.CompletedMatching,
            TargetSqlBackupIdentityStatus.IdentityMismatch => TargetBackupObservation.IdentityMismatch,
            _ => TargetBackupObservation.Unknown,
        };
    }

    private static string IdentityReason(TargetSqlResult<TargetSqlBackupIdentity> result)
    {
        return result.Value?.Status switch
        {
            TargetSqlBackupIdentityStatus.NotFound => "reconciliation.backup_record_not_found",
            TargetSqlBackupIdentityStatus.InProgress => "reconciliation.backup_still_running",
            TargetSqlBackupIdentityStatus.IdentityMismatch => "reconciliation.backup_identity_mismatch",
            TargetSqlBackupIdentityStatus.CompletedMatching => "reconciliation.file_evidence_insufficient",
            _ => "reconciliation.sql_probe_failed",
        };
    }

    private sealed record FileObservation(
        BackupArtifactObservation Artifact,
        long? Length,
        string? ReasonCode);
}
