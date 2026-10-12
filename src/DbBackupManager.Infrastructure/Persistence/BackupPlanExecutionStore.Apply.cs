using System.Data;
using DbBackupManager.Application.BackupSets;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;
using static DbBackupManager.Infrastructure.Persistence.BackupTaskPersistence;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed partial class BackupPlanExecutionStore
{
    public Task<BackupExecutionContractCode> ApplyAsync(LeaseHandle lease, ReserveBackupExecutionOperation identity,
        IVerifiedBackupFileHandle? protectedFile = null, IVerifiedBackupFileHandle? protectedDependency = null,
        CancellationToken cancellationToken = default) =>
        _persistence.ExecuteWithStrategyAsync(async db =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var snapshot = await db.BackupTaskSnapshots.SingleOrDefaultAsync(x => x.TaskId == identity.TaskId, cancellationToken);
            if (snapshot is null) return BackupExecutionContractCode.NotFound;
            // 先取保留协调锁，再锁数据库和任务，避免与删除许可交叉等待。
            if (!await PlanRetentionLock.TryAcquireAsync(db, snapshot.DatabaseId, cancellationToken))
                return BackupExecutionContractCode.DatabaseBlocked;
            await BackupInvocationAuthorizationStore.LockDatabaseAsync(db, snapshot.DatabaseId, cancellationToken);
            var task = await LockAsync(db, identity.TaskId, cancellationToken);
            if (task is null || !IsOpen(task, snapshot)) return BackupExecutionContractCode.StageNotOpen;
            var operation = await db.BackupPlanExecutionOperations.AsTracking().SingleOrDefaultAsync(x => x.Id == identity.MutationId, cancellationToken);
            if (operation is null) return BackupExecutionContractCode.NotFound;
            if (BackupExecutionOperationStore.Identity(operation) != identity || lease.TaskId != identity.TaskId || lease.BackupAttemptId != identity.AttemptId)
                return BackupExecutionContractCode.Conflict;
            if (operation.State == BackupExecutionOperationState.Applied) return BackupExecutionContractCode.AlreadyApplied;
            if (operation.State != BackupExecutionOperationState.Frozen) return BackupExecutionContractCode.Conflict;
            if (ValidateLease(task, lease, clock.GetUtcNow()) is not null) return BackupExecutionContractCode.LeaseLost;
            var attempt = await db.BackupAttempts.AsTracking().SingleAsync(x => x.Id == identity.AttemptId && x.TaskId == task.Id, cancellationToken);
            var sqlOperation = await db.BackupPlanExecutionOperations.SingleAsync(x => x.AttemptId == attempt.Id && x.Kind == BackupExecutionOperationKind.SqlResult, cancellationToken);
            if (identity.IntendedBackupSetId != sqlOperation.IntendedBackupSetId) return BackupExecutionContractCode.Conflict;
            var sqlFacts = sqlOperation.Facts.SqlSuccessObserved ? sqlOperation.Facts
                : await db.BackupPlanExecutionOperations.Where(x => x.AttemptId == attempt.Id && x.Kind == BackupExecutionOperationKind.Recovery
                    && x.State == BackupExecutionOperationState.Applied && x.Facts.SqlSuccessObserved && x.Facts.SqlOutcomeSource == BackupSqlOutcomeSource.RecoveredEvidence)
                    .OrderByDescending(x => x.Sequence).Select(x => x.Facts).FirstOrDefaultAsync(cancellationToken) ?? sqlOperation.Facts;
            var facts = operation.Facts;
            var now = clock.GetUtcNow();
            var evidenceTime = facts.EvidenceAtUtc ?? identity.ObservedAtUtc;
            var before = task.Status;
            var stage = task.CurrentStage;
            var reason = $"plan.{identity.Kind.ToString().ToLowerInvariant()}.applied";
            var successful = facts.Outcome == BackupExecutionOutcome.Succeeded;
            if (identity.Kind == BackupExecutionOperationKind.Admission)
            {
                if (successful || facts.SqlOutcomeSource != BackupSqlOutcomeSource.NotInvoked
                    || attempt.BackupInvocationStatus != BackupInvocationStatus.Prepared
                    || await db.BackupInvocationAuthorizations.AnyAsync(x => x.AttemptId == attempt.Id, cancellationToken))
                    return BackupExecutionContractCode.Conflict;
                task.RecordConfirmedFailure(snapshot.StorageMode, lease.LeaseToken, now, facts.ReasonCode!, "调用前检查未通过，BACKUP 未发出。");
            }
            else if (identity.Kind == BackupExecutionOperationKind.SqlResult || identity.Kind == BackupExecutionOperationKind.Recovery && stage == BackupTaskStage.Backup)
            {
                if (stage != BackupTaskStage.Backup) return BackupExecutionContractCode.Conflict;
                if (facts.SqlOutcomeSource == BackupSqlOutcomeSource.NotInvoked)
                {
                    if (identity.Kind != BackupExecutionOperationKind.Recovery || lease.Purpose != BackupLeasePurpose.Reconciliation
                        || attempt.BackupInvocationStatus != BackupInvocationStatus.Prepared
                        || await db.BackupInvocationAuthorizations.AnyAsync(x => x.AttemptId == attempt.Id, cancellationToken))
                        return BackupExecutionContractCode.Conflict;
                    task.CompleteUninvokedPlanPreparationReconciliation(lease.LeaseToken, now);
                }
                else if (successful)
                {
                    if (!facts.SqlSuccessObserved) return BackupExecutionContractCode.Conflict;
                    if (attempt.BackupInvocationStatus == BackupInvocationStatus.Running) attempt.RecordBackupSucceeded(evidenceTime);
                    else if (attempt.BackupInvocationStatus == BackupInvocationStatus.Indeterminate) attempt.ReconcileBackupSucceeded(evidenceTime);
                    else if (identity.Kind == BackupExecutionOperationKind.Recovery && lease.Purpose == BackupLeasePurpose.Reconciliation
                        && facts.SqlOutcomeSource == BackupSqlOutcomeSource.RecoveredEvidence
                        && attempt.BackupInvocationStatus == BackupInvocationStatus.Succeeded && sqlFacts.SqlSuccessObserved)
                    {
                        // 已应用的成功事实不重写完成时间；本次只补查原授权的终止。
                    }
                    else return BackupExecutionContractCode.Conflict;
                    if (identity.Kind == BackupExecutionOperationKind.Recovery && facts.SqlOutcomeSource == BackupSqlOutcomeSource.RecoveredEvidence)
                    {
                        var authorization = await db.BackupInvocationAuthorizations.SingleOrDefaultAsync(x => x.TaskId == task.Id && x.AttemptId == attempt.Id, cancellationToken);
                        if (authorization is null) return BackupExecutionContractCode.Conflict;
                        if (authorization.TerminalObservedAtUtc is null)
                            Unknown(task, snapshot, lease, now, "sql.original_call_unterminated");
                        else Advance(task, snapshot, lease, now);
                    }
                    else Advance(task, snapshot, lease, now);
                }
                else if (facts.Outcome == BackupExecutionOutcome.ConfirmedFailed)
                {
                    if (attempt.BackupInvocationStatus != BackupInvocationStatus.Running || facts.SqlOutcomeSource != BackupSqlOutcomeSource.PlatformResponse)
                        return BackupExecutionContractCode.Conflict;
                    attempt.RecordBackupConfirmedFailed(evidenceTime, facts.ReasonCode!);
                    task.RecordConfirmedFailure(snapshot.StorageMode, lease.LeaseToken, now, facts.ReasonCode!, "SQL 明确返回失败。");
                }
                else
                {
                    if (attempt.BackupInvocationStatus == BackupInvocationStatus.Running) attempt.RecordBackupIndeterminate(evidenceTime, facts.ReasonCode!);
                    Unknown(task, snapshot, lease, now, facts.ReasonCode ?? "sql.result_unknown");
                }
                if (facts.SqlOutcomeSource == BackupSqlOutcomeSource.PlatformResponse && facts.OriginalCallTerminated && facts.OriginalCallerCannotInvoke)
                {
                    var authorization = await db.BackupInvocationAuthorizations.AsTracking().SingleAsync(x => x.AttemptId == attempt.Id, cancellationToken);
                    if (authorization.SqlOperationId != operation.Id || facts.Outcome is not (BackupExecutionOutcome.Succeeded or BackupExecutionOutcome.ConfirmedFailed))
                        return BackupExecutionContractCode.Conflict;
                    authorization.RecordTermination(operation.Id, operation.Id, successful ? BackupInvocationTerminationKind.PlatformCompleted
                        : BackupInvocationTerminationKind.PlatformConfirmedFailed, evidenceTime, true, true);
                }
            }
            else if (identity.Kind == BackupExecutionOperationKind.Metadata)
            {
                if (stage != BackupTaskStage.VerifyLocal || attempt.BackupInvocationStatus != BackupInvocationStatus.Succeeded)
                    return BackupExecutionContractCode.Conflict;
                if (successful && (protectedFile is null || !protectedFile.GuardsExactPathUntilCommit
                    || protectedFile.Path != attempt.WorkerSourceFilePath
                    || !SameEndpoint(Endpoint(snapshot.ToSnapshotModel().WorkerSourceEndpoint), protectedFile.Endpoint)
                    || !facts.Content.MatchesContent(protectedFile.Content) || !facts.Content.PreservesKnownObjectId(protectedFile.Content)
                    || protectedFile.Content.Protection != BackupObjectProtection.GuardedUntilCommit))
                    return BackupExecutionContractCode.Conflict;
                var observations = await db.BackupPlanExecutionObservations.Where(x => x.OperationId == operation.Id).OrderBy(x => x.EntryNumber).ToArrayAsync(cancellationToken);
                var command = new RegisterBackupSetCommand(lease, identity.IntendedBackupSetId, snapshot.DatabaseId, operation.Id,
                    identity.ObservedAtUtc, facts.Metadata, facts.Completion, facts.Assessment, facts.ActualBaseBackupSetId, true,
                    observations.Select(x => new BackupSetObservation(Enum.Parse<BackupSetEvidenceSource>(x.Source.ToString()), x.Kind,
                        x.Facts.Metadata, x.Facts.Completion, x.Facts.Assessment)).ToArray());
                var registered = await BackupSetRegistrationStore.RegisterInTransactionAsync(db, command, now, cancellationToken);
                if (!registered.IsSucceeded) return BackupExecutionContractCode.Conflict;
                var set = await db.BackupSets.SingleAsync(x => x.AttemptId == attempt.Id, cancellationToken);
                var rows = await db.BackupSetEvidence.Where(x => x.AttemptId == attempt.Id).ToArrayAsync(cancellationToken);
                var fullQualified = await DependencyQualifiedAsync(db, snapshot, set, protectedDependency, cancellationToken);
                if (!successful || !MetadataMatches(snapshot, attempt, set, rows) || !fullQualified || !SqlOptionsMatch(sqlFacts, set.Metadata))
                    Unknown(task, snapshot, lease, now, facts.ReasonCode ?? "metadata.not_verified");
            }
            else if (identity.Kind == BackupExecutionOperationKind.Recovery && facts.ReasonCode == "file.write_not_reserved")
            {
                var pendingKind = stage == BackupTaskStage.Transfer ? BackupExecutionOperationKind.Transfer : BackupExecutionOperationKind.ValidateCopy;
                if (!successful || lease.Purpose != BackupLeasePurpose.Reconciliation
                    || stage is not (BackupTaskStage.Transfer or BackupTaskStage.ValidateCopy)
                    || await db.BackupPlanExecutionOperations.AnyAsync(x => x.AttemptId == attempt.Id && x.Kind == pendingKind, cancellationToken)
                    || attempt.BackupInvocationStatus != BackupInvocationStatus.Succeeded || attempt.LocalVerifiedAtUtc is null)
                    return BackupExecutionContractCode.Conflict;
                task.CompleteUnreservedPlanStageReconciliation(lease.LeaseToken, now);
            }
            else if (identity.Kind is BackupExecutionOperationKind.VerifyLocal or BackupExecutionOperationKind.Transfer or BackupExecutionOperationKind.ValidateCopy)
            {
                var expectedStage = identity.Kind switch
                {
                    BackupExecutionOperationKind.VerifyLocal => BackupTaskStage.VerifyLocal,
                    BackupExecutionOperationKind.Transfer => BackupTaskStage.Transfer,
                    _ => BackupTaskStage.ValidateCopy
                };
                if (stage != expectedStage) return BackupExecutionContractCode.Conflict;
                var set = await db.BackupSets.SingleOrDefaultAsync(x => x.AttemptId == attempt.Id, cancellationToken);
                var rows = await db.BackupSetEvidence.Where(x => x.AttemptId == attempt.Id).ToArrayAsync(cancellationToken);
                var fullQualified = set is not null && await DependencyQualifiedAsync(db, snapshot, set, protectedDependency, cancellationToken);
                if (!successful || set is null || !MetadataMatches(snapshot, attempt, set, rows) || !fullQualified || !SqlOptionsMatch(sqlFacts, set.Metadata))
                    Unknown(task, snapshot, lease, now, facts.ReasonCode ?? "file.not_verified");
                else
                {
                    var endpoint = Endpoint(identity.Kind == BackupExecutionOperationKind.VerifyLocal
                        ? snapshot.ToSnapshotModel().WorkerSourceEndpoint : snapshot.ToSnapshotModel().Policy.RemoteEndpoint!);
                    var path = identity.Kind switch
                    {
                        BackupExecutionOperationKind.VerifyLocal => attempt.WorkerSourceFilePath,
                        BackupExecutionOperationKind.Transfer => attempt.RemotePartialFilePath!,
                        _ => attempt.RemoteFinalFilePath!
                    };
                    if (protectedFile is null || !protectedFile.GuardsExactPathUntilCommit || protectedFile.Path != path
                        || !SameEndpoint(endpoint, protectedFile.Endpoint) || !facts.Content.MatchesContent(protectedFile.Content)
                        || !facts.Content.PreservesKnownObjectId(protectedFile.Content)
                        || protectedFile.Content.Protection != BackupObjectProtection.GuardedUntilCommit)
                        return BackupExecutionContractCode.Conflict;
                    if (identity.Kind == BackupExecutionOperationKind.VerifyLocal)
                    {
                        var metadata = await db.BackupPlanExecutionOperations.Where(x => x.AttemptId == attempt.Id
                            && x.Kind == BackupExecutionOperationKind.Metadata && x.State == BackupExecutionOperationState.Applied)
                            .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(cancellationToken);
                        if (metadata is null || !metadata.Facts.Content.MatchesContent(facts.Content)
                            || !metadata.Facts.Content.PreservesKnownObjectId(facts.Content))
                            return BackupExecutionContractCode.Conflict;
                    }
                    if (identity.Kind != BackupExecutionOperationKind.VerifyLocal)
                    {
                        var local = await db.BackupPlanExecutionOperations.SingleAsync(x => x.AttemptId == attempt.Id
                            && x.Kind == BackupExecutionOperationKind.VerifyLocal && x.State == BackupExecutionOperationState.Applied, cancellationToken);
                        if (!local.Facts.Content.MatchesContent(facts.Content)) return BackupExecutionContractCode.Conflict;
                        if (identity.Kind == BackupExecutionOperationKind.ValidateCopy)
                        {
                            var transfer = await db.BackupPlanExecutionOperations.SingleAsync(x => x.AttemptId == attempt.Id
                                && x.Kind == BackupExecutionOperationKind.Transfer && x.State == BackupExecutionOperationState.Applied, cancellationToken);
                            if (!transfer.Facts.Content.PreservesKnownObjectId(facts.Content)) return BackupExecutionContractCode.Conflict;
                        }
                    }
                    if (identity.Kind == BackupExecutionOperationKind.VerifyLocal)
                        attempt.RecordLocalVerification(facts.Content.LengthBytes!.Value, evidenceTime);
                    else if (identity.Kind == BackupExecutionOperationKind.ValidateCopy)
                        attempt.RecordRemoteValidated(evidenceTime);
                    if (identity.Kind != BackupExecutionOperationKind.Transfer)
                    {
                        // RemoteOnly 没有本地恢复窗口。这里写入远端窗口天数，只为满足 RetentionDays 的 1–36500 约束，不授权删除。
                        var retentionDays = identity.Kind == BackupExecutionOperationKind.VerifyLocal
                            ? snapshot.StorageMode == BackupStorageMode.RemoteOnly
                                ? snapshot.RemoteRetentionDays
                                : snapshot.LocalRetentionDays
                            : snapshot.RemoteRetentionDays;
                        if (retentionDays is not > 0)
                            return BackupExecutionContractCode.Conflict;
                        var file = identity.Kind == BackupExecutionOperationKind.VerifyLocal
                            ? BackupFile.CreateLocal(operation.Id, task.Id, attempt.Id, snapshot.DatabaseId, snapshot.ServerId,
                                endpoint.Protocol, path, facts.Content.LengthBytes!.Value, evidenceTime, retentionDays.Value)
                            : BackupFile.CreateRemote(operation.Id, task.Id, attempt.Id, snapshot.DatabaseId, snapshot.StorageTargetId!.Value,
                                endpoint.Protocol, path, facts.Content.LengthBytes!.Value, evidenceTime, retentionDays.Value);
                        file.AssociateBackupSet(set.Id);
                        db.BackupFiles.Add(file);
                        db.BackupFileStateChanges.Add(new(operation.Id, file.Id, task.Id, null, BackupFileStatus.Available, "file.registered", now));
                    }
                    Advance(task, snapshot, lease, now);
                }
            }
            else return BackupExecutionContractCode.Conflict;
            operation.MarkApplied();
            db.AddRange(CreateStateChange(operation.Id, task, before, stage, reason, null, now),
                CreateAudit(null, task.Id, "plan.stage.apply", successful ? "succeeded" : "unknown", facts.ReasonCode));
            var addedCopy = db.ChangeTracker.Entries<BackupFile>().Any(entry => entry.State == EntityState.Added);
            await db.SaveChangesAsync(cancellationToken);
            if (addedCopy)
                await PlanLocationRetentionWork.ReleaseSupersededAsync(db, snapshot.DatabaseId, now, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return BackupExecutionContractCode.Succeeded;
        }, cancellationToken);

    private static void Advance(BackupTask task, BackupTaskSnapshot snapshot, LeaseHandle lease, DateTimeOffset now)
    {
        if (lease.Purpose == BackupLeasePurpose.Execution) task.CompleteRunningStage(snapshot.StorageMode, lease.LeaseToken, now);
        else task.CompleteReconciliation(snapshot.StorageMode, lease.LeaseToken, now, BackupReconciliationOutcome.Succeeded);
    }
    private static void Unknown(BackupTask task, BackupTaskSnapshot snapshot, LeaseHandle lease, DateTimeOffset now, string reason)
    {
        if (lease.Purpose == BackupLeasePurpose.Execution) task.RecordIndeterminateResult(snapshot.StorageMode, lease.LeaseToken, now, reason, "保留原事实与文件，等待显式核对。");
        else task.CompleteReconciliation(snapshot.StorageMode, lease.LeaseToken, now, BackupReconciliationOutcome.Inconclusive, reason, "证据不足，不重新执行备份。");
    }
    private static bool MetadataMatches(BackupTaskSnapshot snapshot, BackupAttempt attempt, BackupSet set, IReadOnlyList<BackupSetEvidence> rows) =>
        set.Id != Guid.Empty && set.TaskId == attempt.TaskId && set.DatabaseId == snapshot.DatabaseId
        && BackupPlanExecutionFactRules.MetadataPassed(set, rows, snapshot.BackupType)
        && set.Metadata.DatabaseGuid.Value == attempt.ExpectedDatabaseGuid && set.Metadata.FamilyGuid.Value == attempt.ExpectedFamilyGuid
        && set.Metadata.IsCopyOnly.Value == snapshot.UseCopyOnly && set.Metadata.HasBackupChecksums.Value == snapshot.UseChecksum
        && set.Metadata.IsCompressed.Value == snapshot.UseCompression;
    private static bool SameEndpoint(BackupFileEndpointInput a, BackupFileEndpointInput b) => a.Protocol == b.Protocol
        && a.Host == b.Host && a.Port == b.Port && a.RootPath == b.RootPath && a.CredentialReferenceId == b.CredentialReferenceId
        && a.SftpHostKeyFingerprint == b.SftpHostKeyFingerprint;
    private static bool SqlOptionsMatch(BackupExecutionFacts facts, BackupSetMetadata metadata) =>
        facts.SqlSuccessObserved && facts.UsedCopyOnly == metadata.IsCopyOnly.Value
        && facts.UsedChecksum == metadata.HasBackupChecksums.Value && facts.UsedCompression == metadata.IsCompressed.Value;
    private static async Task<bool> DependencyQualifiedAsync(PlatformDbContext db, BackupTaskSnapshot snapshot, BackupSet set,
        IVerifiedBackupFileHandle? protectedDependency, CancellationToken token)
    {
        if (snapshot.BackupType != BackupType.Differential) return true;
        if (set.Metadata.DifferentialBaseGuid.Value is not { } guid
            || await FindQualifiedFullInContextAsync(db, snapshot.DatabaseId, guid, token) is not { } full
            || full.BackupSetId != set.BaseBackupSetId || protectedDependency is null || !protectedDependency.GuardsExactPathUntilCommit
            || !SameEndpoint(full.Endpoint, protectedDependency.Endpoint)
            || !string.Equals(full.Path, protectedDependency.Path, full.Endpoint.Protocol == FileTransferProtocol.Smb ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !full.Content.MatchesContent(protectedDependency.Content) || !full.Content.PreservesKnownObjectId(protectedDependency.Content)
            || protectedDependency.Content.Protection != BackupObjectProtection.GuardedUntilCommit) return false;
        return true;
    }
}
