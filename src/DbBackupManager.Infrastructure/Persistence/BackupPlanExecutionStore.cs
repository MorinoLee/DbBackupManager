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
                            BackupPlanWorkerStore.CloseWithoutBaseline(db, task, snapshot.StorageMode, now);
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
    /// 或其他数据库仍未终止的调用授权、仍为 Running/Indeterminate 的 Attempt。
    /// 同库的未终止调用不在这里拒绝，交给授权阶段按数据库拒绝并记成 sql.database_blocked。
    /// </summary>
    private static async Task<bool> AnotherExecutionMayStillBeRunningAsync(
        PlatformDbContext db, Guid databaseId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.BackupTasks.AsNoTracking().AnyAsync(
            task => task.Status == BackupTaskStatus.Running && task.LeaseExpiresAtUtc > now, cancellationToken)
        || await db.BackupInvocationAuthorizations.AsNoTracking().AnyAsync(
            item => item.TerminalObservedAtUtc == null && item.DatabaseId != databaseId, cancellationToken)
        || await (from attempt in db.BackupAttempts.AsNoTracking()
                  join snapshot in db.BackupTaskSnapshots.AsNoTracking() on attempt.TaskId equals snapshot.TaskId
                  where (attempt.BackupInvocationStatus == BackupInvocationStatus.Running
                         || attempt.BackupInvocationStatus == BackupInvocationStatus.Indeterminate)
                        && snapshot.DatabaseId != databaseId
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
