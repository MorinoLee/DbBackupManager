using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.BackupTasks;

public sealed record PlanLocationAssessment(
    BackupFileLocation Location,
    Guid? StorageTargetId,
    int? WindowDays,
    LocationRecoveryConclusion Conclusion,
    IReadOnlyList<PlanCopyRetentionDecision> Copies);

public interface IPlanLocationRetentionStore
{
    Task<IReadOnlyList<PlanLocationAssessment>> AssessAsync(
        Guid databaseId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ClaimNextAsync(
        Guid databaseId,
        ClaimBackupFileDeletionCommand command,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> RefreshAsync(
        Guid fileId,
        Guid leaseToken,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> CommitAsync(
        BackupFileRetentionWorkItem work,
        BackupFileRetentionCommitCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>读取一条已到期、且当前不再可删的计划副本租约，不修改状态。</summary>
    Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ReadExpiredProtectedAsync(
        Guid databaseId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>在探测之后收口过期租约：文件还在则回到可用，已经不在则记为缺失。</summary>
    Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ResolveExpiredAsync(
        Guid fileId,
        Guid leaseToken,
        bool stillPresent,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}

public static class PlanCopyDeletionReasons
{
    public const string Busy = "plan.retention.busy";
}

/// <summary>
/// 计划副本的物理删除许可。许可在解开文件期间保持协调锁，调用方提交删除结果后释放。
/// </summary>
public interface IPlanCopyDeletionPermit : IAsyncDisposable
{
    bool Applies { get; }

    bool Allowed { get; }

    string? ReasonCode { get; }

    Task<bool> CommitDeletedAsync(CancellationToken cancellationToken = default);
}

public interface IPlanCopyDeletionCoordinator
{
    Task<IPlanCopyDeletionPermit> OpenAsync(
        BackupFileDeleteRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 显式执行一次计划副本清理。不注册后台循环；RemoteOnly 暂存不在此入口删除。
/// </summary>
public sealed class PlanLocationRetentionRunner(
    IPlanLocationRetentionStore store,
    IBackupFileStorageProbe probe,
    IBackupFileDeletionExecutor deletion,
    TimeProvider clock)
{
    public async Task<bool> RunOnceAsync(Guid databaseId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var expired = await store.ReadExpiredProtectedAsync(databaseId, now, cancellationToken);
        if (expired.Code == BackupTaskStoreResultCode.Succeeded && expired.Value is not null)
        {
            return await ResolveExpiredCopyAsync(expired.Value, cancellationToken);
        }

        if (expired.Code != BackupTaskStoreResultCode.NotFound)
        {
            return false;
        }

        var claimed = await store.ClaimNextAsync(
            databaseId,
            new ClaimBackupFileDeletionCommand(Guid.NewGuid(), Guid.NewGuid(), "plan-retention", now, now.AddMinutes(2)),
            cancellationToken);
        if (claimed.Code != BackupTaskStoreResultCode.Succeeded || claimed.Value is null)
        {
            return false;
        }

        var refreshed = await store.RefreshAsync(claimed.Value.FileId, claimed.Value.LeaseToken, clock.GetUtcNow(), cancellationToken);
        if (refreshed.Code != BackupTaskStoreResultCode.Succeeded || refreshed.Value is null)
        {
            return false;
        }

        var work = refreshed.Value;
        BackupFileStorageResult<BackupFileMetadata> inspect;
        try
        {
            inspect = await probe.InspectAsync(work.Endpoint, work.Path, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (!inspect.IsSucceeded)
        {
            await store.CommitAsync(work, new(Guid.NewGuid(), BackupFileRetentionOutcome.Failed, clock.GetUtcNow(), "file.inspect_failed"), cancellationToken);
            return false;
        }

        if (!inspect.Value!.Exists)
        {
            await store.CommitAsync(work, new(Guid.NewGuid(), BackupFileRetentionOutcome.Missing, clock.GetUtcNow()), cancellationToken);
            return true;
        }

        if (!inspect.Value.IsRegularFile || inspect.Value.LengthBytes != work.LengthBytes)
        {
            await store.CommitAsync(
                work,
                new(Guid.NewGuid(), BackupFileRetentionOutcome.Failed, clock.GetUtcNow(),
                    inspect.Value.IsRegularFile ? "file.length_mismatch" : "file.not_regular"),
                cancellationToken);
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
            await store.CommitAsync(
                work,
                new(Guid.NewGuid(), BackupFileRetentionOutcome.Failed, clock.GetUtcNow(), deleted.Failure?.Code.ToString() ?? "file.delete_failed"),
                cancellationToken);
        }

        return false;
    }

    private async Task<bool> ResolveExpiredCopyAsync(BackupFileRetentionWorkItem work, CancellationToken cancellationToken)
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
            return false;
        }

        var resolved = await store.ResolveExpiredAsync(
            work.FileId,
            work.LeaseToken,
            inspect.Value.Exists,
            clock.GetUtcNow(),
            cancellationToken);
        return resolved.Code == BackupTaskStoreResultCode.Succeeded;
    }
}
