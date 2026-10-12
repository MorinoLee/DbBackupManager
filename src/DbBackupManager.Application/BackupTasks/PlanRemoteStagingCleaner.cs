using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.BackupTasks;

/// <summary>
/// 显式清理 RemoteOnly 本地暂存。当前文件和远端恢复依赖未确认时不删除。
/// 不注册后台循环。未指定任务的领取会跳过仍在 Running 的所属任务，避免和它自己的 Cleanup 抢文件。
/// </summary>
public sealed class PlanRemoteStagingCleaner(
    IPlanLocationRetentionStore retention,
    IBackupPlanExecutionStore execution,
    IBackupFileStorageProbe probe,
    IBackupFileDeletionExecutor deletion,
    TimeProvider clock)
{
    public async Task<BackupExecutionContractCode> CleanOwnAsync(
        BackupPlanExecutionSession session,
        CancellationToken cancellationToken = default)
    {
        var work = session.Work;
        if (work.Snapshot.Policy.StorageMode != BackupStorageMode.RemoteOnly || work.Lease.Stage != BackupTaskStage.Cleanup)
        {
            return BackupExecutionContractCode.StageNotOpen;
        }

        var release = await retention.ReadOwnStagingAsync(work.Task.TaskId, clock.GetUtcNow(), cancellationToken);
        if (release.FileId is null)
        {
            var cleaned = await FinishCleanupAsync(session, false, null, cancellationToken);
            if (cleaned is BackupExecutionContractCode.Succeeded or BackupExecutionContractCode.AlreadyApplied
                or BackupExecutionContractCode.DatabaseBlocked or BackupExecutionContractCode.LeaseLost
                or BackupExecutionContractCode.CancellationRequested)
            {
                return cleaned;
            }

            return await execution.SuspendAsync(work.Lease, Guid.NewGuid(), "remote.staging_unknown", CancellationToken.None);
        }

        if (!release.Releasable)
        {
            return await FinishCleanupAsync(session, true, release.Reason, cancellationToken);
        }

        var now = clock.GetUtcNow();
        var claimed = await retention.ClaimStagingAsync(
            work.Snapshot.Identity.DatabaseId,
            new ClaimBackupFileDeletionCommand(Guid.NewGuid(), Guid.NewGuid(), "plan-staging", now, now.AddMinutes(2)),
            work.Task.TaskId,
            cancellationToken);
        if (claimed.Code != BackupTaskStoreResultCode.Succeeded || claimed.Value is null)
        {
            var cleaned = await FinishCleanupAsync(session, false, null, CancellationToken.None);
            if (cleaned is BackupExecutionContractCode.Succeeded or BackupExecutionContractCode.AlreadyApplied)
            {
                return cleaned;
            }

            if (claimed.Code == BackupTaskStoreResultCode.DatabaseBlocked)
            {
                if (cleaned == BackupExecutionContractCode.DatabaseBlocked)
                {
                    return cleaned;
                }

                var suspended = await SuspendBlockedAsync(session);
                return suspended == BackupExecutionContractCode.Succeeded
                    ? BackupExecutionContractCode.DatabaseBlocked
                    : suspended;
            }

            if (cleaned is BackupExecutionContractCode.LeaseLost or BackupExecutionContractCode.CancellationRequested
                or BackupExecutionContractCode.DatabaseBlocked)
            {
                return cleaned;
            }

            return await execution.SuspendAsync(work.Lease, Guid.NewGuid(), PlanCopyDeletionReasons.Busy, CancellationToken.None);
        }

        var refreshed = await retention.RefreshAsync(claimed.Value.FileId, claimed.Value.LeaseToken, clock.GetUtcNow(), cancellationToken);
        if (refreshed.Code != BackupTaskStoreResultCode.Succeeded || refreshed.Value is null)
        {
            return await execution.SuspendAsync(work.Lease, Guid.NewGuid(), PlanCopyDeletionReasons.Busy, CancellationToken.None);
        }

        var deleted = await DeleteClaimedAsync(refreshed.Value, cancellationToken);
        return deleted switch
        {
            StagingDeleteResult.Deleted => await FinishCleanupAsync(session, false, null, CancellationToken.None),
            StagingDeleteResult.Missing => await execution.SuspendAsync(
                work.Lease, Guid.NewGuid(), PlanCopyDeletionReasons.StagingMissing, CancellationToken.None),
            StagingDeleteResult.Cancelled => BackupExecutionContractCode.CancellationRequested,
            _ => await execution.SuspendAsync(work.Lease, Guid.NewGuid(), "remote.staging_unknown", CancellationToken.None)
        };
    }

    /// <summary>
    /// 续租会改变任务行版本。只在用途、阶段和 Attempt 未变时重放同一次结束命令。
    /// 协调锁忙时先重试，仍忙则暂停，以便之后的显式入口收口。
    /// </summary>
    private async Task<BackupExecutionContractCode> FinishCleanupAsync(
        BackupPlanExecutionSession session,
        bool retained,
        string? outcomeReason,
        CancellationToken cancellationToken)
    {
        var current = session;
        var leaseRetries = 0;
        var blockedRetries = 0;
        while (true)
        {
            BackupExecutionContractCode code;
            try
            {
                code = await execution.CompleteRemoteCleanupAsync(
                    current.Work.Lease, retained, retained ? outcomeReason : null, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return BackupExecutionContractCode.CancellationRequested;
            }

            if (code == BackupExecutionContractCode.DatabaseBlocked && blockedRetries < 2)
            {
                blockedRetries++;
                continue;
            }

            if (code == BackupExecutionContractCode.DatabaseBlocked)
            {
                return await SuspendAfterBlockedCompletionAsync(current, retained);
            }

            if (code != BackupExecutionContractCode.LeaseLost || leaseRetries >= 2)
            {
                return code;
            }

            leaseRetries++;
            var refreshed = await execution.RefreshAsync(current.Work.Task.TaskId, current.Work.Lease.LeaseToken, CancellationToken.None);
            var before = current.Work.Lease;
            if (refreshed is null
                || refreshed.Work.Lease.Purpose != before.Purpose
                || refreshed.Work.Lease.Stage != before.Stage
                || refreshed.Work.Lease.BackupAttemptId != before.BackupAttemptId
                || refreshed.Work.Lease.RowVersion.SequenceEqual(before.RowVersion))
            {
                return code;
            }

            current = refreshed;
        }
    }

    private async Task<BackupExecutionContractCode> SuspendAfterBlockedCompletionAsync(
        BackupPlanExecutionSession session,
        bool retained)
    {
        var suspended = await SuspendBlockedAsync(session);
        if (retained || suspended != BackupExecutionContractCode.Succeeded)
        {
            return suspended == BackupExecutionContractCode.Succeeded
                ? BackupExecutionContractCode.DatabaseBlocked
                : suspended;
        }

        var closed = await execution.CloseDeferredRemoteCleanupAsync(session.Work.Task.TaskId, CancellationToken.None);
        return closed is BackupExecutionContractCode.Succeeded or BackupExecutionContractCode.AlreadyApplied
            ? closed
            : BackupExecutionContractCode.DatabaseBlocked;
    }

    private async Task<BackupExecutionContractCode> SuspendBlockedAsync(BackupPlanExecutionSession session)
    {
        var suspended = await execution.SuspendAsync(
            session.Work.Lease, Guid.NewGuid(), PlanCopyDeletionReasons.Busy, CancellationToken.None);
        if (suspended != BackupExecutionContractCode.LeaseLost)
        {
            return suspended;
        }

        var next = await execution.RefreshAsync(session.Work.Task.TaskId, session.Work.Lease.LeaseToken, CancellationToken.None);
        return next is null
            ? suspended
            : await execution.SuspendAsync(next.Work.Lease, Guid.NewGuid(), PlanCopyDeletionReasons.Busy, CancellationToken.None);
    }

    public async Task<bool> RunOnceAsync(Guid databaseId, CancellationToken cancellationToken = default)
    {
        if (await execution.CloseReadyRemoteCleanupsAsync(databaseId, cancellationToken) == BackupExecutionContractCode.DatabaseBlocked)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        var claimed = await retention.ClaimStagingAsync(
            databaseId,
            new ClaimBackupFileDeletionCommand(Guid.NewGuid(), Guid.NewGuid(), "plan-staging", now, now.AddMinutes(2)),
            null,
            cancellationToken);
        if (claimed.Code != BackupTaskStoreResultCode.Succeeded || claimed.Value is null)
        {
            return false;
        }

        var refreshed = await retention.RefreshAsync(claimed.Value.FileId, claimed.Value.LeaseToken, clock.GetUtcNow(), cancellationToken);
        if (refreshed.Code != BackupTaskStoreResultCode.Succeeded || refreshed.Value is null)
        {
            return false;
        }

        if (await DeleteClaimedAsync(refreshed.Value, cancellationToken) != StagingDeleteResult.Deleted)
        {
            return false;
        }

        var closed = await execution.CloseDeferredRemoteCleanupAsync(refreshed.Value.TaskId, CancellationToken.None);
        return closed is BackupExecutionContractCode.Succeeded or BackupExecutionContractCode.AlreadyApplied;
    }

    private async Task<StagingDeleteResult> DeleteClaimedAsync(BackupFileRetentionWorkItem work, CancellationToken cancellationToken)
    {
        try
        {
            BackupFileStorageResult<BackupFileMetadata> inspect;
            try
            {
                inspect = await probe.InspectAsync(work.Endpoint, work.Path, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return StagingDeleteResult.Cancelled;
            }

            if (!inspect.IsSucceeded || inspect.Value is null)
            {
                await retention.CommitAsync(work, new(Guid.NewGuid(), BackupFileRetentionOutcome.Failed, clock.GetUtcNow(), "file.inspect_failed"), CancellationToken.None);
                return StagingDeleteResult.Failed;
            }

            if (!inspect.Value.Exists)
            {
                await retention.CommitAsync(work, new(Guid.NewGuid(), BackupFileRetentionOutcome.Missing, clock.GetUtcNow()), CancellationToken.None);
                return StagingDeleteResult.Missing;
            }

            if (!inspect.Value.IsRegularFile || inspect.Value.LengthBytes != work.LengthBytes)
            {
                await retention.CommitAsync(
                    work,
                    new(Guid.NewGuid(), BackupFileRetentionOutcome.Failed, clock.GetUtcNow(),
                        inspect.Value.IsRegularFile ? "file.length_mismatch" : "file.not_regular"),
                    CancellationToken.None);
                return StagingDeleteResult.Failed;
            }

            BackupFileStorageResult<BackupFileMutationReceipt> deleted;
            try
            {
                deleted = await deletion.DeleteAsync(
                    new BackupFileDeleteRequest(
                        work.Endpoint,
                        work.Path,
                        work.LengthBytes,
                        inspect.Value.Identity,
                        60,
                        new(work.TaskId, null, work.FileId, BackupArtifactPathRole.RegisteredFile, work.LeaseToken, work.RowVersion)),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return StagingDeleteResult.Cancelled;
            }

            if (deleted.IsSucceeded)
            {
                return StagingDeleteResult.Deleted;
            }

            if (deleted.Outcome == BackupFileStorageOutcome.Indeterminate
                && deleted.Failure?.Code is BackupFileStorageFailureCode.Cancelled or BackupFileStorageFailureCode.TimedOut)
            {
                return StagingDeleteResult.Cancelled;
            }

            if (deleted.Outcome == BackupFileStorageOutcome.ConfirmedFailed
                && deleted.Failure?.Code != BackupFileStorageFailureCode.OperationRejected)
            {
                await retention.CommitAsync(
                    work,
                    new(Guid.NewGuid(), BackupFileRetentionOutcome.Failed, clock.GetUtcNow(), deleted.Failure?.Code.ToString() ?? "file.delete_failed"),
                    CancellationToken.None);
            }

            return StagingDeleteResult.Failed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StagingDeleteResult.Cancelled;
        }
    }

    private enum StagingDeleteResult
    {
        Deleted,
        Missing,
        Failed,
        Cancelled
    }
}
