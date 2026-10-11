using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupTasks;
using static DbBackupManager.Application.BackupTasks.BackupStageResult;

namespace DbBackupManager.Application.BackupTasks;

internal sealed class BackupRemoteStageExecutor(
    IBackupSourceProbe files,
    IBackupFileStorageProbe remoteProbe,
    IBackupDirectoryPreparer directoryPreparation,
    IBackupFileTransferExecutor transfer,
    IBackupFileDeletionExecutor deletion,
    IBackupExecutionGuard guard)
{
    internal async Task<BackupStageResult> ExecuteAsync(
        BackupExecutionWorkItem work,
        CancellationToken token)
    {
        if (work.Snapshot.Purpose is not null || work.Snapshot.FileNameRuleVersion == "v3")
            return new(BackupStageOutcome.Indeterminate, "plan.copy.protected");
        // 调用写入适配器前即标记开始；此后取消或抛异常时，外部结果必须交给恢复流程核对。
        var invoked = false;
        try
        {
            return await ExecuteCoreAsync(work, () => invoked = true, token);
        }
        catch (OperationCanceledException)
        {
            return new(invoked ? BackupStageOutcome.Indeterminate : BackupStageOutcome.Cancelled,
                "execution_cancelled");
        }
        catch (Exception)
        {
            return new(invoked ? BackupStageOutcome.Indeterminate : BackupStageOutcome.ConfirmedFailed,
                "stage_adapter_failed");
        }
    }

    private async Task<BackupStageResult> ExecuteCoreAsync(
        BackupExecutionWorkItem work,
        Action markStarted,
        CancellationToken token)
    {
        if (work.Attempt.SourceLengthBytes is not > 0)
        {
            return new(BackupStageOutcome.ConfirmedFailed, "source_length_unavailable");
        }

        var expected = work.Attempt.SourceLengthBytes.Value;
        if (work.Lease.Stage == BackupTaskStage.Cleanup)
        {
            return await ExecuteCleanupAsync(work, expected, markStarted, token);
        }

        if (work.Snapshot.Policy.RemoteEndpoint is null
            || string.IsNullOrWhiteSpace(work.Attempt.RemotePartialFilePath)
            || string.IsNullOrWhiteSpace(work.Attempt.RemoteFinalFilePath))
        {
            return new(BackupStageOutcome.ConfirmedFailed, "remote_endpoint_unavailable");
        }

        var source = await files.InspectAsync(work.Snapshot, work.Attempt, token);
        if (!source.Succeeded)
        {
            return new(BackupStageOutcome.ConfirmedFailed, source.ErrorCode ?? "source_unavailable");
        }

        if (!source.Exists || source.Length != expected)
        {
            return new(BackupStageOutcome.ConfirmedFailed, source.Exists ? "source_length_mismatch" : "verified_file_unavailable");
        }

        var remote = Endpoint(work.Snapshot.Policy.RemoteEndpoint);
        if (work.Lease.Stage == BackupTaskStage.Transfer)
        {
            var prepared = await directoryPreparation.PrepareParentAsync(
                new(
                    remote,
                    work.Attempt.RemotePartialFilePath,
                    TimeoutSeconds(work)),
                token);
            if (!prepared.IsSucceeded)
            {
                return prepared.Failure!.Code == BackupFileStorageFailureCode.Cancelled
                    ? new(BackupStageOutcome.Cancelled, "execution_cancelled")
                    : MapFile(prepared);
            }
        }

        var part = await InspectRemoteAsync(remote, work.Attempt.RemotePartialFilePath, token);
        if (part.Failure is not null) return part.Failure;
        var final = await InspectRemoteAsync(remote, work.Attempt.RemoteFinalFilePath, token);
        if (final.Failure is not null) return final.Failure;

        var partState = Classify(part.Value, expected);
        var finalState = Classify(final.Value, expected);
        if (partState == RemoteFileState.Conflict
            || finalState == RemoteFileState.Conflict
            || finalState == RemoteFileState.Incomplete
            || (partState != RemoteFileState.Missing && finalState != RemoteFileState.Missing))
        {
            return new(BackupStageOutcome.Indeterminate, "remote_copy_conflict");
        }

        if (work.Lease.Stage == BackupTaskStage.Transfer)
        {
            return await ExecuteTransferAsync(
                work, remote, expected, partState, finalState, part.Value, markStarted, token);
        }

        return await ExecuteValidateCopyAsync(
            work, remote, expected, partState, finalState, markStarted, token);
    }

    private async Task<BackupStageResult> ExecuteTransferAsync(
        BackupExecutionWorkItem work,
        BackupFileEndpointInput remote,
        long expected,
        RemoteFileState partState,
        RemoteFileState finalState,
        BackupFileMetadata? part,
        Action markStarted,
        CancellationToken token)
    {
        if (finalState == RemoteFileState.Complete || partState == RemoteFileState.Complete)
        {
            return new(BackupStageOutcome.Succeeded, Length: expected);
        }

        if (partState == RemoteFileState.Incomplete)
        {
            markStarted();
            var cleaned = await deletion.DeleteAsync(
                new(
                    remote,
                    work.Attempt.RemotePartialFilePath!,
                    part!.LengthBytes!.Value,
                    part.Identity,
                    TimeoutSeconds(work),
                    new(work.Lease.TaskId, work.Lease.BackupAttemptId, null, BackupArtifactPathRole.RemotePartial,
                        work.Lease.LeaseToken, work.Lease.RowVersion)),
                token);
            if (!cleaned.IsSucceeded
                && cleaned.Failure!.Code != BackupFileStorageFailureCode.FileNotFound)
            {
                return MapFile(cleaned);
            }
        }

        token.ThrowIfCancellationRequested();
        if (!await guard.IsEnabledAsync(work.Snapshot, token))
        {
            return new(BackupStageOutcome.ConfirmedFailed, "execution_configuration_disabled");
        }

        markStarted();
        var copied = await transfer.TransferAsync(
            new(
                Endpoint(work.Snapshot.WorkerSourceEndpoint),
                work.Attempt.WorkerSourceFilePath,
                remote,
                work.Attempt.RemotePartialFilePath!,
                expected,
                TimeoutSeconds(work)),
            token);
        return copied.IsSucceeded
            ? new(BackupStageOutcome.Succeeded, Length: copied.Value!.LengthBytes)
            : MapFile(copied);
    }

    private async Task<BackupStageResult> ExecuteValidateCopyAsync(
        BackupExecutionWorkItem work,
        BackupFileEndpointInput remote,
        long expected,
        RemoteFileState partState,
        RemoteFileState finalState,
        Action markStarted,
        CancellationToken token)
    {
        if (finalState == RemoteFileState.Complete)
        {
            return new(BackupStageOutcome.Succeeded, Length: expected);
        }

        if (partState == RemoteFileState.Incomplete)
        {
            return new(BackupStageOutcome.ConfirmedFailed, "partial_incomplete");
        }

        if (partState == RemoteFileState.Missing)
        {
            return new(BackupStageOutcome.ConfirmedFailed, "remote_copy_missing");
        }

        token.ThrowIfCancellationRequested();
        if (!await guard.IsEnabledAsync(work.Snapshot, token))
        {
            return new(BackupStageOutcome.ConfirmedFailed, "execution_configuration_disabled");
        }

        markStarted();
        var renamed = await transfer.RenameAsync(
            new(
                remote,
                work.Attempt.RemotePartialFilePath!,
                work.Attempt.RemoteFinalFilePath!,
                expected,
                TimeoutSeconds(work)),
            token);
        return renamed.IsSucceeded
            ? new(BackupStageOutcome.Succeeded, Length: expected)
            : MapFile(renamed);
    }

    private async Task<BackupStageResult> ExecuteCleanupAsync(
        BackupExecutionWorkItem work,
        long expected,
        Action markStarted,
        CancellationToken token)
    {
        var source = await files.InspectAsync(work.Snapshot, work.Attempt, token);
        if (!source.Succeeded)
        {
            return new(BackupStageOutcome.ConfirmedFailed, source.ErrorCode ?? "source_unavailable");
        }

        if (!source.Exists)
        {
            return new(BackupStageOutcome.Succeeded, Length: expected);
        }

        token.ThrowIfCancellationRequested();
        if (!await guard.IsEnabledAsync(work.Snapshot, token))
        {
            return new(BackupStageOutcome.ConfirmedFailed, "execution_configuration_disabled");
        }

        markStarted();
        var deleted = await deletion.DeleteAsync(
            new(
                Endpoint(work.Snapshot.WorkerSourceEndpoint),
                work.Attempt.WorkerSourceFilePath,
                expected,
                null,
                TimeoutSeconds(work),
                new(work.Lease.TaskId, work.Lease.BackupAttemptId, null, BackupArtifactPathRole.RemoteOnlySource,
                    work.Lease.LeaseToken, work.Lease.RowVersion)),
            token);
        if (deleted.IsSucceeded
            || deleted.Failure?.Code == BackupFileStorageFailureCode.FileNotFound)
        {
            return new(BackupStageOutcome.Succeeded, Length: expected);
        }

        return MapFile(deleted);
    }

    private async Task<(BackupFileMetadata? Value, BackupStageResult? Failure)> InspectRemoteAsync(
        BackupFileEndpointInput endpoint,
        string path,
        CancellationToken token)
    {
        var result = await remoteProbe.InspectAsync(endpoint, path, token);
        return result.IsSucceeded
            ? (result.Value, null)
            : (null, MapFile(result));
    }

    private static RemoteFileState Classify(BackupFileMetadata? metadata, long expected)
    {
        if (metadata is null || !metadata.Exists)
        {
            return RemoteFileState.Missing;
        }

        if (!metadata.IsRegularFile || metadata.LengthBytes is null)
        {
            return RemoteFileState.Conflict;
        }

        if (metadata.LengthBytes == expected)
        {
            return RemoteFileState.Complete;
        }

        return metadata.LengthBytes > 0
            ? RemoteFileState.Incomplete
            : RemoteFileState.Conflict;
    }

    internal static int TimeoutSeconds(BackupExecutionWorkItem work) =>
        Math.Clamp(work.Snapshot.Policy.TransferTimeoutMinutes * 60, 1, 86_400);

    internal static BackupFileEndpointInput Endpoint(BackupFileEndpointModel model) =>
        new(
            model.Protocol,
            model.Host,
            model.Port,
            model.BasePath,
            model.CredentialReferenceId,
            model.SftpHostKeyFingerprint);

    private enum RemoteFileState
    {
        Missing,
        Complete,
        Incomplete,
        Conflict,
    }

}
