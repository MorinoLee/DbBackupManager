using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.BackupTasks;

/// <summary>
/// 显式清理 RemoteOnly 本地暂存。当前文件和远端恢复依赖未确认时不删除。
/// 不注册后台循环。
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
            return await execution.SuspendAsync(work.Lease, Guid.NewGuid(), "remote.staging_unknown", cancellationToken);
        }

        if (!release.Releasable)
        {
            return await FinishCleanupAsync(session, true, cancellationToken);
        }

        var now = clock.GetUtcNow();
        var claimed = await retention.ClaimStagingAsync(
            work.Snapshot.Identity.DatabaseId,
            new ClaimBackupFileDeletionCommand(Guid.NewGuid(), Guid.NewGuid(), "plan-staging", now, now.AddMinutes(2)),
            work.Task.TaskId,
            cancellationToken);
        if (claimed.Code != BackupTaskStoreResultCode.Succeeded || claimed.Value is null)
        {
            return claimed.Code == BackupTaskStoreResultCode.DatabaseBlocked
                ? BackupExecutionContractCode.DatabaseBlocked
                : await execution.SuspendAsync(work.Lease, Guid.NewGuid(), PlanCopyDeletionReasons.Busy, CancellationToken.None);
        }

        var refreshed = await retention.RefreshAsync(claimed.Value.FileId, claimed.Value.LeaseToken, clock.GetUtcNow(), cancellationToken);
        if (refreshed.Code != BackupTaskStoreResultCode.Succeeded || refreshed.Value is null)
        {
            return await execution.SuspendAsync(work.Lease, Guid.NewGuid(), PlanCopyDeletionReasons.Busy, CancellationToken.None);
        }

        var deleted = await DeleteClaimedAsync(refreshed.Value, cancellationToken);
        if (!deleted)
        {
            return await execution.SuspendAsync(work.Lease, Guid.NewGuid(), "remote.staging_unknown", CancellationToken.None);
        }

        return await FinishCleanupAsync(session, false, CancellationToken.None);
    }

    /// <summary>
    /// 续租会改变任务行版本。只在用途、阶段和 Attempt 未变时重放同一次结束命令。
    /// </summary>
    private async Task<BackupExecutionContractCode> FinishCleanupAsync(
        BackupPlanExecutionSession session,
        bool retained,
        CancellationToken cancellationToken)
    {
        var current = session;
        for (var retry = 0; ; retry++)
        {
            var code = await execution.CompleteRemoteCleanupAsync(current.Work.Lease, retained, cancellationToken);
            if (code != BackupExecutionContractCode.LeaseLost || retry >= 2)
            {
                return code;
            }

            var next = await execution.RefreshAsync(current.Work.Task.TaskId, current.Work.Lease.LeaseToken, CancellationToken.None);
            var before = current.Work.Lease;
            if (next is null
                || next.Work.Lease.Purpose != before.Purpose
                || next.Work.Lease.Stage != before.Stage
                || next.Work.Lease.BackupAttemptId != before.BackupAttemptId
                || next.Work.Lease.RowVersion.SequenceEqual(before.RowVersion))
            {
                return code;
            }

            current = next;
        }
    }

    public async Task<bool> RunOnceAsync(Guid databaseId, CancellationToken cancellationToken = default)
    {
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

        if (!await DeleteClaimedAsync(refreshed.Value, cancellationToken))
        {
            return false;
        }

        await execution.CloseDeferredRemoteCleanupAsync(refreshed.Value.TaskId, CancellationToken.None);
        return true;
    }

    private async Task<bool> DeleteClaimedAsync(BackupFileRetentionWorkItem work, CancellationToken cancellationToken)
    {
        BackupFileStorageResult<BackupFileMetadata> inspect;
        try
        {
            inspect = await probe.InspectAsync(work.Endpoint, work.Path, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (!inspect.IsSucceeded || inspect.Value is null)
        {
            await retention.CommitAsync(work, new(Guid.NewGuid(), BackupFileRetentionOutcome.Failed, clock.GetUtcNow(), "file.inspect_failed"), CancellationToken.None);
            return false;
        }

        if (!inspect.Value.Exists)
        {
            await retention.CommitAsync(work, new(Guid.NewGuid(), BackupFileRetentionOutcome.Missing, clock.GetUtcNow()), CancellationToken.None);
            return false;
        }

        if (!inspect.Value.IsRegularFile || inspect.Value.LengthBytes != work.LengthBytes)
        {
            await retention.CommitAsync(
                work,
                new(Guid.NewGuid(), BackupFileRetentionOutcome.Failed, clock.GetUtcNow(),
                    inspect.Value.IsRegularFile ? "file.length_mismatch" : "file.not_regular"),
                CancellationToken.None);
            return false;
        }

        var deleted = await deletion.DeleteAsync(
            new BackupFileDeleteRequest(
                work.Endpoint,
                work.Path,
                work.LengthBytes,
                inspect.Value.Identity,
                60,
                new(work.TaskId, null, work.FileId, BackupArtifactPathRole.RegisteredFile, work.LeaseToken, work.RowVersion)),
            cancellationToken);
        if (deleted.IsSucceeded)
        {
            return true;
        }

        if (deleted.Outcome == BackupFileStorageOutcome.ConfirmedFailed
            && deleted.Failure?.Code != BackupFileStorageFailureCode.OperationRejected)
        {
            await retention.CommitAsync(
                work,
                new(Guid.NewGuid(), BackupFileRetentionOutcome.Failed, clock.GetUtcNow(), deleted.Failure?.Code.ToString() ?? "file.delete_failed"),
                CancellationToken.None);
        }

        return false;
    }
}
