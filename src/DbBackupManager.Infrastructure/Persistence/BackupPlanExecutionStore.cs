using System.Data;
using DbBackupManager.Application.BackupSets;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static DbBackupManager.Infrastructure.Persistence.BackupTaskPersistence;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed partial class BackupPlanExecutionStore(
    IDbContextFactory<PlatformDbContext> factory,
    TimeProvider clock,
    ILogger<BackupPlanExecutionStore>? logger = null)
    : IBackupPlanExecutionStore
{
    private readonly BackupTaskPersistence _persistence = new(factory);

    private static readonly Action<ILogger, Guid, Guid, string, Exception?> UnknownResultClosed =
        LoggerMessage.Define<Guid, Guid, string>(LogLevel.Information, new EventId(4201, "PlanUnknownResultClosed"),
            "计划任务以结果未知收口；任务={TaskId}，Attempt={AttemptId}，原因={ReasonCode}。");

    public Task<BackupExecutionContractResult<BackupPlanExecutionSession>> OpenAsync(Guid taskId, Guid requestMutationId,
        bool reconciliationOnly, Guid leaseToken, string owner, DateTimeOffset now, DateTimeOffset expires, CancellationToken cancellationToken = default)
    {
        if (taskId == Guid.Empty || requestMutationId == Guid.Empty || leaseToken == Guid.Empty)
            throw new ArgumentException("任务、请求及租约标识不能为空。");
        return _persistence.ExecuteWithStrategyAsync(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            if (!reconciliationOnly && !await PlanExecutionLock.TryAcquireAsync(db, cancellationToken))
            {
                return new BackupExecutionContractResult<BackupPlanExecutionSession>(BackupExecutionContractCode.Conflict);
            }

            var snapshot = await db.BackupTaskSnapshots.SingleOrDefaultAsync(x => x.TaskId == taskId, cancellationToken);
            if (snapshot is null) return new BackupExecutionContractResult<BackupPlanExecutionSession>(BackupExecutionContractCode.NotFound);
            await BackupInvocationAuthorizationStore.LockDatabaseAsync(db, snapshot.DatabaseId, cancellationToken);
            var task = await LockAsync(db, taskId, cancellationToken);
            if (task is null) return new BackupExecutionContractResult<BackupPlanExecutionSession>(BackupExecutionContractCode.NotFound);
            if (!IsOpen(task, snapshot)) return new BackupExecutionContractResult<BackupPlanExecutionSession>(BackupExecutionContractCode.StageNotOpen);
            var reason = reconciliationOnly ? "plan.recovery.claimed" : "plan.execution.claimed";
            var replay = await db.BackupTaskStateChanges.SingleOrDefaultAsync(x => x.MutationId == requestMutationId, cancellationToken);
            if (replay is not null)
                return new BackupExecutionContractResult<BackupPlanExecutionSession>(replay.TaskId == taskId && replay.ReasonCode == reason
                    ? BackupExecutionContractCode.AlreadyApplied : BackupExecutionContractCode.Conflict);
            var fromStatus = task.Status;
            var fromStage = task.CurrentStage;
            if (task.Status == BackupTaskStatus.Running && task.LeaseExpiresAtUtc <= now)
                task.RecordExpiredExecutionLease(snapshot.StorageMode, now, "plan.lease_expired", "执行租约已到期，保留原 Attempt 待核对。");
            BackupAttempt attempt;
            if (reconciliationOnly)
            {
                if (task.Status != BackupTaskStatus.NeedsAttention || task.CurrentBackupAttemptId is null)
                    return new BackupExecutionContractResult<BackupPlanExecutionSession>(BackupExecutionContractCode.Conflict);
                if (task.LeaseToken is not null && task.LeaseExpiresAtUtc > now)
                    return new BackupExecutionContractResult<BackupPlanExecutionSession>(BackupExecutionContractCode.Conflict);
                task.RequestReconciliation(now);
                task.AcquireReconciliationLease(leaseToken, owner, now, expires);
                attempt = await db.BackupAttempts.AsTracking().SingleAsync(x => x.Id == task.CurrentBackupAttemptId, cancellationToken);
            }
            else
            {
                if (task.Status != BackupTaskStatus.Pending)
                    return new BackupExecutionContractResult<BackupPlanExecutionSession>(BackupExecutionContractCode.Conflict);
                if (snapshot.Purpose == BackupRunPurpose.PlanDifferential && task.CurrentStage == BackupTaskStage.Backup)
                {
                    var facts = await BackupPlanBaselineFacts.ReadAsync(db, snapshot.DatabaseId, cancellationToken);
                    if (!BackupPlanBaselineRules.AllowsDifferentialExecution(
                            facts.HasVerifiedManagedOrdinaryFull, facts.ActiveBaselineConclusion))
                    {
                        if (task.TriggerType == BackupTaskTriggerType.Scheduled)
                        {
                            BackupPlanWorkerStore.CloseWithoutBaseline(db, task, snapshot.StorageMode, now, requestMutationId);
                            await db.SaveChangesAsync(cancellationToken);
                            await tx.CommitAsync(cancellationToken);
                        }

                        return new BackupExecutionContractResult<BackupPlanExecutionSession>(BackupExecutionContractCode.Conflict);
                    }
                }

                if (await AnotherExecutionMayStillBeRunningAsync(db, snapshot.DatabaseId, now, cancellationToken))
                    return new BackupExecutionContractResult<BackupPlanExecutionSession>(BackupExecutionContractCode.Conflict);
                if (!await ConfigurationEnabledAsync(db, snapshot, cancellationToken))
                    return new BackupExecutionContractResult<BackupPlanExecutionSession>(BackupExecutionContractCode.Conflict);
                if (task.CurrentBackupAttemptId is null)
                {
                    var id = Guid.NewGuid();
                    var paths = BackupTaskPathFactory.Create(new BackupPlanPathInput(
                        new(snapshot.ServerId, snapshot.ServerName, snapshot.InstanceId, snapshot.InstanceName, snapshot.DatabaseId, snapshot.DatabaseName),
                        new(snapshot.LocalSqlBackupRootPath, snapshot.FileNameRuleVersion,
                            new(snapshot.SourceAccessProtocol, snapshot.SourceAccessHost, snapshot.SourceAccessPort, snapshot.SourceAccessBasePath,
                                snapshot.SourceCredentialReferenceId, snapshot.SourceSftpHostKeyFingerprint)),
                        snapshot.StorageTargetId is { } target ? new(target, new(snapshot.RemoteProtocol!.Value, snapshot.RemoteHost!, snapshot.RemotePort,
                            snapshot.RemoteBasePath!, snapshot.RemoteCredentialReferenceId!.Value, snapshot.RemoteSftpHostKeyFingerprint)) : null,
                        snapshot.Purpose!.Value), id, now);
                    attempt = new(id, taskId, 1, now, paths);
                    db.BackupAttempts.Add(attempt);
                    db.BackupPlanExecutionOperations.Add(new(Guid.NewGuid(), taskId, id, BackupExecutionOperationKind.SqlResult, 1, Guid.NewGuid(), now));
                }
                else
                {
                    attempt = await db.BackupAttempts.AsTracking().SingleAsync(x => x.Id == task.CurrentBackupAttemptId, cancellationToken);
                    if (task.CurrentStage == BackupTaskStage.Backup && (attempt.BackupInvocationStatus != BackupInvocationStatus.Prepared
                        || await db.BackupInvocationAuthorizations.AnyAsync(x => x.AttemptId == attempt.Id, cancellationToken)))
                        return new BackupExecutionContractResult<BackupPlanExecutionSession>(BackupExecutionContractCode.Conflict);
                }
                if (task.CurrentStage == BackupTaskStage.Backup && task.CurrentBackupAttemptId is not null)
                    task.ClaimOriginalPreparedPlanAttempt(attempt.Id, leaseToken, owner, now, expires);
                else task.ClaimExecution(snapshot.StorageMode, attempt.Id, leaseToken, owner, now, expires);
            }
            db.AddRange(CreateStateChange(requestMutationId, task, fromStatus, fromStage, reason, null, now),
                CreateAudit(null, taskId, reason, "succeeded", null));
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new BackupExecutionContractResult<BackupPlanExecutionSession>(BackupExecutionContractCode.Succeeded,
                await SessionAsync(db, task, snapshot, attempt, cancellationToken));
        }, cancellationToken);
    }

    public async Task<BackupPlanExecutionSession?> RefreshAsync(Guid taskId, Guid leaseToken, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var task = await db.BackupTasks.SingleOrDefaultAsync(x => x.Id == taskId && x.LeaseToken == leaseToken, cancellationToken);
        if (task?.CurrentBackupAttemptId is null || task.LeaseExpiresAtUtc <= clock.GetUtcNow()) return null;
        var snapshot = await db.BackupTaskSnapshots.SingleAsync(x => x.TaskId == taskId, cancellationToken);
        if (!IsOpen(task, snapshot)) return null;
        var attempt = await db.BackupAttempts.AsTracking().SingleAsync(x => x.Id == task.CurrentBackupAttemptId, cancellationToken);
        return await SessionAsync(db, task, snapshot, attempt, cancellationToken);
    }

    public Task<BackupExecutionContractCode> RenewAsync(Guid taskId, Guid leaseToken, DateTimeOffset expires, CancellationToken cancellationToken = default) =>
        _persistence.ExecuteWithStrategyAsync(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var task = await LockAsync(db, taskId, cancellationToken);
            if (task?.LeaseToken != leaseToken || task.LeasePurpose is null || task.LeaseExpiresAtUtc <= clock.GetUtcNow())
                return BackupExecutionContractCode.LeaseLost;
            if (expires <= task.LeaseExpiresAtUtc) return BackupExecutionContractCode.AlreadyApplied;
            task.RenewLease(leaseToken, task.LeasePurpose.Value, clock.GetUtcNow(), expires);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return BackupExecutionContractCode.Succeeded;
        }, cancellationToken);

    public async Task<QualifiedManagedFull?> FindQualifiedFullAsync(Guid databaseId, Guid backupSetGuid, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await FindQualifiedFullInContextAsync(db, databaseId, backupSetGuid, cancellationToken);
    }

    internal static async Task<QualifiedManagedFull?> FindQualifiedFullInContextAsync(PlatformDbContext db,
        Guid databaseId, Guid backupSetGuid, CancellationToken cancellationToken, bool asNoTracking = false)
    {
        var setsQuery = db.BackupSets.Where(x => x.DatabaseId == databaseId && x.Metadata.BackupSetGuid.Value == backupSetGuid);
        var sets = await (asNoTracking ? setsQuery.AsNoTracking() : setsQuery).ToArrayAsync(cancellationToken);
        if (sets.Length != 1) return null;
        var set = sets[0];
        var attempts = db.BackupAttempts.Where(x => x.Id == set.AttemptId);
        var attempt = await (asNoTracking ? attempts.AsNoTracking() : attempts.AsTracking()).SingleAsync(cancellationToken);
        var evidenceQuery = db.BackupSetEvidence.Where(x => x.AttemptId == set.AttemptId);
        var evidence = await (asNoTracking ? evidenceQuery.AsNoTracking() : evidenceQuery).ToArrayAsync(cancellationToken);
        var files = db.BackupFiles.Where(x => x.BackupSetId == set.Id && x.Location == BackupFileLocation.Local);
        var local = await (asNoTracking ? files.AsNoTracking() : files).SingleOrDefaultAsync(cancellationToken);
        var operations = db.BackupPlanExecutionOperations.Where(x => x.AttemptId == set.AttemptId && x.Kind == BackupExecutionOperationKind.VerifyLocal
            && x.State == BackupExecutionOperationState.Applied);
        var content = await (asNoTracking ? operations.AsNoTracking() : operations).Select(x => x.Facts.Content).SingleOrDefaultAsync(cancellationToken);
        if (attempt.BackupInvocationStatus != BackupInvocationStatus.Succeeded || attempt.LocalVerifiedAtUtc is null || attempt.SourceLengthBytes is not > 0
            || !BackupPlanExecutionFactRules.MetadataPassed(set, evidence) || local?.Status != BackupFileStatus.Available
            || local.TaskId != set.TaskId || local.AttemptId != set.AttemptId || !string.Equals(local.Path, attempt.WorkerSourceFilePath, local.Protocol == FileTransferProtocol.Smb ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || local.LengthBytes != attempt.SourceLengthBytes || content?.LengthBytes != local.LengthBytes) return null;
        var snapshots = db.BackupTaskSnapshots.Where(x => x.TaskId == set.TaskId);
        var snapshot = await (asNoTracking ? snapshots.AsNoTracking() : snapshots).SingleAsync(cancellationToken);
        return new(set.Id, new(set.Id, BackupMetadataReconciliationService.Full(set.Metadata, BaselineEvidenceStatus.Complete)),
            Endpoint(snapshot.ToSnapshotModel().WorkerSourceEndpoint), local.Path, content);
    }

    public Task<BackupExecutionContractCode> SuspendAsync(LeaseHandle lease, Guid mutationId, string reason, CancellationToken cancellationToken = default) =>
        _persistence.ExecuteWithStrategyAsync(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var task = await LockAsync(db, lease.TaskId, cancellationToken);
            if (task is null) return BackupExecutionContractCode.NotFound;
            var replay = await db.BackupTaskStateChanges.SingleOrDefaultAsync(x => x.MutationId == mutationId, cancellationToken);
            if (replay is not null) return replay.TaskId == lease.TaskId && replay.ReasonCode == reason
                ? BackupExecutionContractCode.AlreadyApplied : BackupExecutionContractCode.Conflict;
            if (ValidateLease(task, lease, clock.GetUtcNow()) is not null) return BackupExecutionContractCode.LeaseLost;
            var snapshot = await db.BackupTaskSnapshots.SingleAsync(x => x.TaskId == task.Id, cancellationToken);
            var before = task.Status;
            var now = clock.GetUtcNow();
            if (lease.Purpose == BackupLeasePurpose.Execution)
                task.RecordIndeterminateResult(snapshot.StorageMode, lease.LeaseToken, now, reason, "原 Attempt 与文件保持不变，等待显式只读核对。");
            else task.CompleteReconciliation(snapshot.StorageMode, lease.LeaseToken, now, BackupReconciliationOutcome.Inconclusive,
                reason, "证据尚未完整，保留原事实。");
            db.AddRange(CreateStateChange(mutationId, task, before, lease.Stage, reason, null, now),
                CreateAudit(null, task.Id, "plan.execution.suspend", "unknown", reason));
            NotificationOutboxWriter.EnqueuePlanOutcome(db, mutationId, task.Id, lease.Stage, task.Status, now, task.ErrorCode ?? reason);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return BackupExecutionContractCode.Succeeded;
        }, cancellationToken);

    /// <summary>
    /// 原调用已独立证明终止，但成功证据不足、缺失或矛盾时的收口：以 ClosedUnknown 结束任务，
    /// 保留原 Attempt 与全部证据/文件。拒绝无终止授权、身份不完整、仍有任何成功证据的状态。
    /// </summary>
    public async Task<BackupExecutionContractCode> CloseUnknownAsync(LeaseHandle lease, Guid terminationMutationId,
        string reasonCode, DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default)
    {
        if (terminationMutationId == Guid.Empty || observedAtUtc.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(reasonCode))
            throw new ArgumentException("结果未知收口请求无效。");
        return await _persistence.ExecuteWithStrategyAsync(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var snapshot = await db.BackupTaskSnapshots.SingleOrDefaultAsync(x => x.TaskId == lease.TaskId, cancellationToken);
            if (snapshot is null) return BackupExecutionContractCode.NotFound;
            await BackupInvocationAuthorizationStore.LockDatabaseAsync(db, snapshot.DatabaseId, cancellationToken);
            var task = await LockAsync(db, lease.TaskId, cancellationToken);
            if (task is null) return BackupExecutionContractCode.NotFound;
            if (!IsOpen(task, snapshot)) return BackupExecutionContractCode.StageNotOpen;
            var replay = await db.BackupTaskStateChanges.SingleOrDefaultAsync(x => x.MutationId == terminationMutationId, cancellationToken);
            if (replay is not null)
                return replay.TaskId == task.Id && replay.ReasonCode == "plan.recovery.close_unknown"
                    && replay.ToStatus == BackupTaskStatus.ClosedUnknown
                    ? BackupExecutionContractCode.AlreadyApplied : BackupExecutionContractCode.Conflict;
            if (lease.Purpose != BackupLeasePurpose.Reconciliation || task.Status != BackupTaskStatus.NeedsAttention
                || task.CurrentStage != BackupTaskStage.Backup || task.CurrentBackupAttemptId is null)
                return BackupExecutionContractCode.Conflict;
            var now = clock.GetUtcNow();
            if (ValidateLease(task, lease, now) is not null) return BackupExecutionContractCode.LeaseLost;
            var attempt = await db.BackupAttempts.AsTracking().SingleOrDefaultAsync(
                x => x.Id == lease.BackupAttemptId && x.TaskId == task.Id, cancellationToken);
            if (attempt is null || attempt.BackupInvocationStatus is not (BackupInvocationStatus.Running or BackupInvocationStatus.Indeterminate)
                || attempt.ExpectedDatabaseGuid is null || attempt.ExpectedFamilyGuid is null)
                return BackupExecutionContractCode.Conflict;
            var authorization = await db.BackupInvocationAuthorizations.AsTracking().SingleOrDefaultAsync(
                x => x.TaskId == task.Id && x.AttemptId == attempt.Id, cancellationToken);
            if (authorization is null || authorization.TerminalObservedAtUtc != observedAtUtc
                || authorization.TerminationMutationId != terminationMutationId
                || authorization.TerminationEvidenceId != terminationMutationId
                || authorization.TerminationKind != BackupInvocationTerminationKind.RecoveredTerminated
                || authorization.Binding.State != BackupInvocationBindingState.Known
                || authorization.Binding.ConnectionId is null || authorization.Binding.SessionId is not > 0
                || authorization.Binding.SessionEstablishedLocal is null)
                return BackupExecutionContractCode.Conflict;
            var evidence = await db.BackupPlanExecutionOperations.SingleOrDefaultAsync(x => x.Id == terminationMutationId, cancellationToken);
            if (evidence is null || evidence.TaskId != task.Id || evidence.AttemptId != attempt.Id
                || evidence.Kind != BackupExecutionOperationKind.Recovery
                || evidence.State is not (BackupExecutionOperationState.Frozen or BackupExecutionOperationState.Applied)
                || evidence.Facts.EvidenceAtUtc != observedAtUtc
                || !evidence.Facts.OriginalCallTerminated || !evidence.Facts.OriginalCallerCannotInvoke
                || evidence.Facts.SqlSuccessObserved)
                return BackupExecutionContractCode.Conflict;
            // 任何成功证据存在时都不能以未知收口，避免把可恢复的成功覆盖成未知终态。
            if (await db.BackupPlanExecutionOperations.AnyAsync(x => x.AttemptId == attempt.Id
                    && x.Kind == BackupExecutionOperationKind.Recovery && x.Facts.SqlSuccessObserved, cancellationToken)
                || await db.BackupSets.AnyAsync(x => x.AttemptId == attempt.Id, cancellationToken))
                return BackupExecutionContractCode.Conflict;
            var before = task.Status;
            var stage = task.CurrentStage;
            var evidenceReason = evidence.Facts.ReasonCode ?? reasonCode;
            task.CloseUnknownResult(snapshot.StorageMode, lease.LeaseToken, now, reasonCode,
                $"执行已结束，但无法证明备份成功（{evidenceReason}）；保留原 Attempt、证据与文件，不回退为明确失败或未调用。");
            db.AddRange(CreateStateChange(terminationMutationId, task, before, stage, "plan.recovery.close_unknown", evidenceReason, now),
                CreateAudit(null, task.Id, "plan.recovery.close_unknown", "unknown", evidenceReason));
            NotificationOutboxWriter.EnqueuePlanOutcome(db, terminationMutationId, task.Id, stage, task.Status, now, reasonCode);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            if (logger is not null) UnknownResultClosed(logger, task.Id, attempt.Id, reasonCode, null);
            return BackupExecutionContractCode.Succeeded;
        }, cancellationToken);
    }

    public async Task<BackupExecutionContractCode> CompleteRemoteCleanupAsync(LeaseHandle lease, bool retained,
        string? outcomeReason = null, CancellationToken cancellationToken = default)
    {
        Guid? remoteGap = null;
        var code = await _persistence.ExecuteWithStrategyAsync(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var snapshot = await db.BackupTaskSnapshots.SingleOrDefaultAsync(x => x.TaskId == lease.TaskId, cancellationToken);
            if (snapshot is null) return BackupExecutionContractCode.NotFound;
            if (!await PlanRetentionLock.TryAcquireAsync(db, snapshot.DatabaseId, cancellationToken))
                return BackupExecutionContractCode.DatabaseBlocked;
            await BackupInvocationAuthorizationStore.LockDatabaseAsync(db, snapshot.DatabaseId, cancellationToken);
            var task = await LockAsync(db, lease.TaskId, cancellationToken);
            if (task is null) return BackupExecutionContractCode.NotFound;
            if (snapshot.StorageMode != BackupStorageMode.RemoteOnly || task.CurrentStage != BackupTaskStage.Cleanup)
                return BackupExecutionContractCode.StageNotOpen;
            if (ValidateLease(task, lease, clock.GetUtcNow()) is not null) return BackupExecutionContractCode.LeaseLost;
            var attempt = await db.BackupAttempts.AsTracking().SingleAsync(x => x.Id == lease.BackupAttemptId && x.TaskId == task.Id, cancellationToken);
            var local = await db.BackupFiles.AsNoTracking().SingleOrDefaultAsync(x => x.TaskId == task.Id && x.AttemptId == attempt.Id
                && x.Location == BackupFileLocation.Local && x.Status != BackupFileStatus.Deleted, cancellationToken);
            var now = clock.GetUtcNow();
            var before = task.Status;
            if (retained)
            {
                if (local is null || attempt.LocalCleanupCompletedAtUtc is not null)
                    return BackupExecutionContractCode.Conflict;
                var reason = string.IsNullOrWhiteSpace(outcomeReason) || outcomeReason.Length > 100
                    ? PlanLocationRetentionWork.StagingRetainedReason
                    : outcomeReason;
                task.CompleteRunningStage(snapshot.StorageMode, lease.LeaseToken, now);
                db.AddRange(CreateStateChange(Guid.NewGuid(), task, before, BackupTaskStage.Cleanup, reason, null, now),
                    CreateAudit(null, task.Id, "plan.staging.retain", "succeeded", reason));
            }
            else
            {
                if (attempt.LocalCleanupCompletedAtUtc is null || local is not null)
                    return BackupExecutionContractCode.Conflict;
                task.CompleteRunningStage(snapshot.StorageMode, lease.LeaseToken, now);
                db.AddRange(CreateStateChange(Guid.NewGuid(), task, before, BackupTaskStage.Cleanup, "plan.staging.cleaned", null, now),
                    CreateAudit(null, task.Id, "plan.staging.clean", "succeeded", null));
            }

            await db.SaveChangesAsync(cancellationToken);
            if (snapshot.StorageTargetId is Guid target)
                await RemoteGapNotices.MarkPendingAsync(db, new RemoteGapNotices.Observation(
                    snapshot.DatabaseId, task.Id, BackupTaskStage.Cleanup, now, [target]), cancellationToken);
            await tx.CommitAsync(cancellationToken);
            remoteGap = snapshot.DatabaseId;
            return BackupExecutionContractCode.Succeeded;
        }, cancellationToken);
        if (code == BackupExecutionContractCode.Succeeded && remoteGap is Guid databaseId)
            await RemoteGapNotices.TryDrainPendingAsync(factory, databaseId, cancellationToken, logger);
        return code;
    }

    /// <summary>
    /// 只读核对 RemoteOnly 暂存清理。已确认物理删除（本地行已解开且 Attempt 记下清理完成）时按既有
    /// 删除证据收口，不需要重新执行，也不受已停用配置影响；暂存仍在时只释放核对租约、保留原 Attempt
    /// 和文件，由执行路径按原阶段重试清理。本方法不删除任何文件，也不把缺失或归属未知当成已清理。
    /// </summary>
    public async Task<BackupExecutionContractCode> ReleaseDeferredCleanupForExecutionAsync(LeaseHandle lease,
        CancellationToken cancellationToken = default)
    {
        Guid? settledDatabase = null;
        var code = await _persistence.ExecuteWithStrategyAsync(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var snapshot = await db.BackupTaskSnapshots.SingleOrDefaultAsync(x => x.TaskId == lease.TaskId, cancellationToken);
            if (snapshot is null) return BackupExecutionContractCode.NotFound;
            if (!await PlanRetentionLock.TryAcquireAsync(db, snapshot.DatabaseId, cancellationToken))
                return BackupExecutionContractCode.DatabaseBlocked;
            await BackupInvocationAuthorizationStore.LockDatabaseAsync(db, snapshot.DatabaseId, cancellationToken);
            var task = await LockAsync(db, lease.TaskId, cancellationToken);
            if (task is null) return BackupExecutionContractCode.NotFound;
            if (snapshot.StorageMode != BackupStorageMode.RemoteOnly || task.CurrentStage != BackupTaskStage.Cleanup)
                return BackupExecutionContractCode.StageNotOpen;
            // 协调锁、数据库锁和任务行锁都已经取得后再读当前时间：等待期间租约可能已经到期，
            // 不能沿用取锁之前缓存的时刻。
            var now = clock.GetUtcNow();
            if (ValidateLease(task, lease, now) is not null) return BackupExecutionContractCode.LeaseLost;
            var attempt = await db.BackupAttempts.AsTracking().SingleAsync(x => x.Id == lease.BackupAttemptId && x.TaskId == task.Id, cancellationToken);
            var localRemains = await db.BackupFiles.AnyAsync(x => x.TaskId == task.Id && x.AttemptId == attempt.Id
                && x.Location == BackupFileLocation.Local && x.Status != BackupFileStatus.Deleted, cancellationToken);
            var before = task.Status;
            string reason;
            if (attempt.LocalCleanupCompletedAtUtc is not null && !localRemains)
            {
                // 物理删除已经提交，只差任务终态：按既有删除证据收口，不依赖已停用配置重新执行。
                task.CompleteReconciliation(snapshot.StorageMode, lease.LeaseToken, now, BackupReconciliationOutcome.Succeeded);
                reason = "plan.staging.clean";
                settledDatabase = snapshot.DatabaseId;
            }
            else
            {
                // 暂存仍在（或归属/依赖仍无法证明）：核对不做删除，释放租约交回执行路径按原 Attempt 继续。
                task.CompleteReconciliation(snapshot.StorageMode, lease.LeaseToken, now, BackupReconciliationOutcome.SafeToRetry,
                    null, "暂存清理证据仍不完整，已释放核对租约交回执行路径。");
                reason = "plan.staging.deferred";
            }

            db.AddRange(CreateStateChange(Guid.NewGuid(), task, before, BackupTaskStage.Cleanup,
                    "plan.reconciliation.cleanup", reason, now),
                CreateAudit(null, task.Id, "plan.staging.cleanup_review", "succeeded", reason));
            await db.SaveChangesAsync(cancellationToken);
            if (settledDatabase is not null && snapshot.StorageTargetId is Guid target)
                await RemoteGapNotices.MarkPendingAsync(db, new RemoteGapNotices.Observation(
                    snapshot.DatabaseId, task.Id, BackupTaskStage.Cleanup, now, [target]), cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return BackupExecutionContractCode.Succeeded;
        }, cancellationToken);
        if (code == BackupExecutionContractCode.Succeeded && settledDatabase is Guid databaseId)
            await RemoteGapNotices.TryDrainPendingAsync(factory, databaseId, cancellationToken, logger);
        return code;
    }

    public async Task<BackupExecutionContractCode> CloseDeferredRemoteCleanupAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        Guid? drainedDatabase = null;
        var code = await _persistence.ExecuteWithStrategyAsync(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var snapshot = await db.BackupTaskSnapshots.SingleOrDefaultAsync(x => x.TaskId == taskId, cancellationToken);
            if (snapshot is null) return BackupExecutionContractCode.NotFound;
            if (!await PlanRetentionLock.TryAcquireAsync(db, snapshot.DatabaseId, cancellationToken))
                return BackupExecutionContractCode.DatabaseBlocked;
            await BackupInvocationAuthorizationStore.LockDatabaseAsync(db, snapshot.DatabaseId, cancellationToken);
            var task = await LockAsync(db, taskId, cancellationToken);
            if (task is null) return BackupExecutionContractCode.NotFound;
            if (task.Status == BackupTaskStatus.Succeeded && task.CurrentStage == BackupTaskStage.Cleanup)
                return BackupExecutionContractCode.AlreadyApplied;
            if (snapshot.StorageMode != BackupStorageMode.RemoteOnly || task.Status != BackupTaskStatus.NeedsAttention
                || task.CurrentStage != BackupTaskStage.Cleanup || task.CurrentBackupAttemptId is null)
                return BackupExecutionContractCode.Conflict;
            if (task.LeaseToken is not null && task.LeaseExpiresAtUtc > clock.GetUtcNow())
                return BackupExecutionContractCode.Conflict;
            var attempt = await db.BackupAttempts.AsNoTracking().SingleAsync(x => x.Id == task.CurrentBackupAttemptId, cancellationToken);
            var localRemains = await db.BackupFiles.AnyAsync(x => x.TaskId == task.Id && x.AttemptId == attempt.Id
                && x.Location == BackupFileLocation.Local && x.Status != BackupFileStatus.Deleted, cancellationToken);
            if (attempt.LocalCleanupCompletedAtUtc is null || localRemains)
                return BackupExecutionContractCode.Conflict;
            var before = task.Status;
            var now = clock.GetUtcNow();
            task.CompleteDeferredRemoteCleanup(snapshot.StorageMode, now);
            db.AddRange(CreateStateChange(Guid.NewGuid(), task, before, BackupTaskStage.Cleanup, "plan.staging.cleaned", null, now),
                CreateAudit(null, task.Id, "plan.staging.clean", "succeeded", null));
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            drainedDatabase = snapshot.DatabaseId;
            return BackupExecutionContractCode.Succeeded;
        }, cancellationToken);
        if (code == BackupExecutionContractCode.Succeeded && drainedDatabase is Guid databaseId)
            await RemoteGapNotices.TryDrainPendingAsync(factory, databaseId, cancellationToken, logger);
        return code;
    }

    public async Task<BackupExecutionContractCode> CloseReadyRemoteCleanupsAsync(Guid databaseId, CancellationToken cancellationToken = default)
    {
        if (databaseId == Guid.Empty) throw new ArgumentException("数据库标识不能为空。", nameof(databaseId));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var ids = await (
            from task in db.BackupTasks.AsNoTracking()
            join snapshot in db.BackupTaskSnapshots.AsNoTracking() on task.Id equals snapshot.TaskId
            join attempt in db.BackupAttempts.AsNoTracking() on task.CurrentBackupAttemptId equals attempt.Id
            where snapshot.DatabaseId == databaseId
                && snapshot.StorageMode == BackupStorageMode.RemoteOnly
                && task.Status == BackupTaskStatus.NeedsAttention
                && task.CurrentStage == BackupTaskStage.Cleanup
                && attempt.LocalCleanupCompletedAtUtc != null
                && (task.LeaseToken == null || task.LeaseExpiresAtUtc <= now)
            select task.Id).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            var code = await CloseDeferredRemoteCleanupAsync(id, cancellationToken);
            if (code == BackupExecutionContractCode.DatabaseBlocked)
                return code;
        }

        return BackupExecutionContractCode.Succeeded;
    }

    /// <summary>
    /// 在计划执行锁下判断是否还有别的执行可能在跑：任意未到期的 Running 租约，
    /// 或其他数据库仍未终止的调用授权、仍可能运行的 Attempt。已记录终止的调用即使 Attempt
    /// 仍保留 Running/Indeterminate，也不再永久阻塞其他数据库；无授权的遗留调用继续阻塞。
    /// 同库的未终止调用不在这里拒绝，交给授权阶段按数据库拒绝并记成 sql.database_blocked。
    /// </summary>
    private static async Task<bool> AnotherExecutionMayStillBeRunningAsync(
        PlatformDbContext db, Guid databaseId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.BackupTasks.AsNoTracking().AnyAsync(
            task => task.Status == BackupTaskStatus.Running && task.LeaseExpiresAtUtc > now, cancellationToken)
        || await db.BackupInvocationAuthorizations.AsNoTracking().AnyAsync(
            item => item.TerminalObservedAtUtc == null && item.DatabaseId != databaseId, cancellationToken)
        || await (from attempt in BackupInvocationLiveness.StillPossiblyRunning(db)
                  join snapshot in db.BackupTaskSnapshots.AsNoTracking() on attempt.TaskId equals snapshot.TaskId
                  where snapshot.DatabaseId != databaseId
                  select attempt.Id).AnyAsync(cancellationToken);

    private static bool IsOpen(BackupTask task, BackupTaskSnapshot snapshot) => task.PlanId is not null
        && snapshot.FileNameRuleVersion == "v3"
        && snapshot.Purpose is BackupRunPurpose.PlanFull or BackupRunPurpose.PlanDifferential or BackupRunPurpose.AdHocCopyOnlyFull
        && snapshot.StorageMode is BackupStorageMode.LocalOnly or BackupStorageMode.LocalAndRemote or BackupStorageMode.RemoteOnly;
    internal static async Task<bool> ConfigurationEnabledAsync(PlatformDbContext db, BackupTaskSnapshot snapshot, CancellationToken token)
    {
        if (!await db.ManagedDatabases.AnyAsync(x => x.Id == snapshot.DatabaseId && x.IsManaged && x.IsAvailable && !x.IsSystemDatabase, token)
            || !await db.DatabaseInstances.AnyAsync(x => x.Id == snapshot.InstanceId && x.IsEnabled, token)
            || !await db.DatabaseServers.AnyAsync(x => x.Id == snapshot.ServerId && x.IsEnabled, token)) return false;
        foreach (var id in new[] { snapshot.SqlCredentialReferenceId, snapshot.SourceCredentialReferenceId, snapshot.RemoteCredentialReferenceId ?? snapshot.SourceCredentialReferenceId }.Distinct())
            if (!await db.CredentialReferences.AnyAsync(x => x.Id == id && x.IsEnabled, token)) return false;
        return snapshot.StorageTargetId is not { } target || await db.StorageTargets.AnyAsync(x => x.Id == target && x.IsEnabled, token);
    }
    internal static BackupFileEndpointInput Endpoint(BackupFileEndpointModel model) =>
        new(model.Protocol, model.Host, model.Port, model.BasePath, model.CredentialReferenceId, model.SftpHostKeyFingerprint);
    private static Task<BackupTask?> LockAsync(PlatformDbContext db, Guid id, CancellationToken token) => db.BackupTasks.FromSqlInterpolated(
        $"SELECT * FROM [BackupTasks] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {id}").AsTracking().SingleOrDefaultAsync(token);
    private static async Task<BackupPlanExecutionSession> SessionAsync(PlatformDbContext db, BackupTask task,
        BackupTaskSnapshot snapshot, BackupAttempt attempt, CancellationToken token)
    {
        var operations = await db.BackupPlanExecutionOperations.Where(x => x.AttemptId == attempt.Id).OrderBy(x => x.Kind).ThenBy(x => x.Sequence).ToArrayAsync(token);
        var detached = new List<FrozenBackupExecutionOperation>();
        foreach (var operation in operations) detached.Add(await BackupExecutionOperationStore.DetachAsync(db, operation, token));
        var authorization = await db.BackupInvocationAuthorizations.SingleOrDefaultAsync(x => x.AttemptId == attempt.Id, token);
        return new(CreateWorkItem(task, snapshot, attempt), detached,
            authorization is null ? null : BackupInvocationAuthorizationStore.Model(authorization));
    }
}
