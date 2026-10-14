using DbBackupManager.Application.FileStorage;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using static DbBackupManager.Application.BackupTasks.BackupPlanOperationFlow;

namespace DbBackupManager.Application.BackupTasks;

public sealed class BackupPlanTaskRunner(IBackupPlanExecutionStore store, IBackupExecutionOperationStore operations,
    IBackupInvocationAuthorizationStore authorizations, ITargetSqlInvocationPreparation preparation,
    ITargetSqlActiveBaselineReader active, IBackupDirectoryPreparer directories, IBackupFileStorageProbe probe,
    IBackupFileContentVerifier files, IBackupPlanFileTransferExecutor transfer, BackupPlanReadOnlyStages stages,
    PlanRemoteStagingCleaner staging, TimeProvider clock) : IBackupPlanTaskRunner
{
    private readonly Guid _incarnation = Guid.NewGuid();
    private readonly BackupPlanOperationFlow _flow = new(store, operations, clock);

    public async Task<BackupExecutionContractCode> RunTaskOnceAsync(RunBackupPlanTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lease = Guid.NewGuid();
        var opened = await store.OpenAsync(request.TaskId, request.RequestMutationId, false, lease, _incarnation.ToString("N"),
            clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(2), cancellationToken);
        if (opened.Value is null) return opened.Code;
        var session = opened.Value;
        await using var heartbeat = new BackupPlanHeartbeat(store, clock, request.TaskId, lease, session.Work.Lease.ExpiresAtUtc, cancellationToken);
        try
        {
            if (session.Work.Lease.Stage == BackupTaskStage.Backup)
            {
                var code = await InvokeAsync(session, heartbeat.Token);
                var next = await ContinueAsync(session, code);
                if (next is null) return code;
                session = next;
            }
            if (session.Work.Lease.Stage == BackupTaskStage.VerifyLocal)
            {
                var code = await stages.RunAsync(session, false, heartbeat.Token);
                var next = await ContinueAsync(session, code);
                if (next is null) return code;
                session = next;
            }
            if (session.Work.Task.CancellationRequestedAtUtc is not null)
                return await stages.SuspendAsync(session, "plan.cancelled_after_sql", CancellationToken.None);
            if (session.Work.Lease.Stage == BackupTaskStage.Transfer)
            {
                var code = await TransferAsync(session, heartbeat.Token);
                var next = await ContinueAsync(session, code);
                if (next is null) return code;
                session = next;
            }
            if (session.Work.Lease.Stage == BackupTaskStage.ValidateCopy)
            {
                var confirmed = await ConfirmCopyAsync(session, heartbeat.Token);
                if (confirmed != BackupExecutionContractCode.Succeeded) return confirmed;
                // RemoteOnly 远端确认后任务进入 Running/Cleanup 且租约仍有效，这里必须取到原租约会话；
                // 取不到说明等待期间已经失租，不能拿陈旧的 ValidateCopy 会话返回成功。
                // LocalAndRemote 的 ValidateCopy 已是最后阶段，成功提交会释放租约，取不到属正常。
                var refreshed = await store.RefreshAsync(request.TaskId, session.Work.Lease.LeaseToken, CancellationToken.None);
                if (refreshed is null)
                {
                    if (session.Work.Snapshot.Policy.StorageMode == BackupStorageMode.RemoteOnly)
                        throw new PlanExecutionLeaseLostException();
                }
                else
                {
                    session = refreshed;
                }
            }

            // RemoteOnly 的 Cleanup 由执行路径收口：无论本轮新完成 ValidateCopy，还是核对释放租约后
            // 从 Pending/Cleanup 续跑，都执行同一段暂存清理，不重新发起 BACKUP。
            if (session.Work.Lease.Stage != BackupTaskStage.Cleanup)
            {
                return BackupExecutionContractCode.Succeeded;
            }

            if (session.Work.Snapshot.Policy.StorageMode != BackupStorageMode.RemoteOnly)
            {
                return BackupExecutionContractCode.StageNotOpen;
            }

            return await staging.CleanOwnAsync(session, heartbeat.Token);
        }
        catch (PlanExecutionLeaseLostException) { return BackupExecutionContractCode.LeaseLost; }
        catch (PlanExecutionConflictException exception) when (exception.Code == BackupExecutionContractCode.CancellationRequested)
        {
            var fresh = await store.RefreshAsync(request.TaskId, lease, CancellationToken.None);
            return fresh is null ? BackupExecutionContractCode.LeaseLost
                : await stages.SuspendAsync(fresh, "plan.cancelled_after_sql", CancellationToken.None);
        }
        catch (PlanExecutionConflictException exception) { return exception.Code; }
        catch (OperationCanceledException)
        {
            var fresh = await store.RefreshAsync(request.TaskId, lease, CancellationToken.None);
            if (fresh is null) return BackupExecutionContractCode.LeaseLost;
            try { await stages.SuspendAsync(fresh, "plan.interrupted", CancellationToken.None); }
            catch { /* 暂停失败仍把取消交给调用方。 */ }
            throw;
        }
        catch (Exception exception) when (exception is not PlanExecutionUnobservedCommitException)
        {
            try
            {
                var fresh = await store.RefreshAsync(request.TaskId, lease, CancellationToken.None);
                if (fresh is not null) await stages.SuspendAsync(fresh, "plan.unexpected_error", CancellationToken.None);
            }
            catch { /* 暂停失败不能掩盖原异常，也不能改写已经提交的结果。 */ }
            throw;
        }
    }

    private async Task<BackupPlanExecutionSession?> ContinueAsync(BackupPlanExecutionSession session, BackupExecutionContractCode code)
    {
        Require(code);
        var next = await store.RefreshAsync(session.Work.Task.TaskId, session.Work.Lease.LeaseToken, CancellationToken.None);
        if (next is not null) return next;
        var required = BackupTaskStateMachine.GetRequiredStages(session.Work.Snapshot.Policy.StorageMode);
        if (required.Count > 0 && session.Work.Lease.Stage == required[^1]) return null;
        throw new PlanExecutionLeaseLostException();
    }

    private async Task<BackupExecutionContractCode> InvokeAsync(BackupPlanExecutionSession session, CancellationToken token)
    {
        var work = session.Work;
        var original = session.Operations.Single(x => x.Identity.Kind == BackupExecutionOperationKind.SqlResult);
        if (original.State == BackupExecutionOperationState.Frozen) return await _flow.ApplyAsync(session, original, null, token);
        if (session.Authorization is not null || work.Attempt.InvocationStatus != BackupInvocationStatus.Prepared)
            return await stages.SuspendAsync(session, "sql.call_already_authorized", token);
        var destination = File(work, false, work.Attempt.WorkerSourceFilePath);
        var sourceCapability = await transfer.CheckCapabilitiesAsync(destination, token);
        if (!sourceCapability.IsSucceeded) return await RejectAsync(session, "file.source_protection_unsupported", token);
        if (work.Snapshot.Policy.StorageMode is BackupStorageMode.LocalAndRemote or BackupStorageMode.RemoteOnly)
        {
            var remoteCapability = await transfer.CheckCapabilitiesAsync(File(work, true, work.Attempt.RemotePartialFilePath!), token);
            if (!remoteCapability.IsSucceeded) return await RejectAsync(session, "file.remote_protection_unsupported", token);
        }
        var directory = await directories.PrepareParentAsync(new(destination.Endpoint, destination.Path, destination.TimeoutSeconds), token);
        if (!directory.IsSucceeded) return await RejectAsync(session, "file.directory_unavailable", token);
        var existing = await probe.InspectAsync(destination.Endpoint, destination.Path, token);
        if (!existing.IsSucceeded || existing.Value!.Exists) return await RejectAsync(session, "file.attempt_path_not_empty", token);
        var request = new TargetSqlBackupRequest(work.Snapshot.Identity.DatabaseName, work.Attempt.LocalSqlFilePath,
            work.Snapshot.Purpose!.Value, work.Snapshot.Policy.UseChecksum, work.Snapshot.Policy.UseCompression, work.Snapshot.Policy.BackupTimeoutMinutes * 60);
        var ready = await preparation.PrepareAsync(Connection(work), request, _incarnation, token);
        if (!ready.IsSucceeded) return await RejectAsync(session, $"sql.{ready.Failure!.Code}", token);
        await using var invocation = ready.Value!;
        if (invocation.Database.DatabaseGuid is null || invocation.Database.FamilyGuid is null
            || work.Attempt.ExpectedDatabaseGuid is { } known && known != invocation.Database.DatabaseGuid
            || work.Attempt.ExpectedFamilyGuid is { } family && family != invocation.Database.FamilyGuid)
            return await RejectAsync(session, "sql.identity_changed", token);
        QualifiedManagedFull? full = null;
        IVerifiedBackupFileHandle? fullHandle = null;
        BackupActiveMetadataEvidence? g0 = null, g1 = null;
        try
        {
            if (work.Snapshot.BackupType == BackupType.Differential)
            {
                g0 = await active.ReadAsync(Connection(work), new(work.Snapshot.Identity.DatabaseName,
                    Math.Min(work.Snapshot.Policy.VerifyTimeoutMinutes * 60, 3600)), token);
                var guids = g0.Baseline.DataFileBases.Select(x => x.BaseBackupSetGuid).Distinct().ToArray();
                if (guids.Length == 1 && guids[0] is { } guid)
                    full = await store.FindQualifiedFullAsync(work.Snapshot.Identity.DatabaseId, guid, token);
                var decision = DifferentialBaselineRules.EvaluateActiveBaseline(invocation.Database, g0.Baseline,
                    full is null ? [] : [full.Candidate]);
                if (decision.Conclusion != DifferentialBaselineConclusion.Verified || full is null)
                    return await RejectAsync(session, decision.ReasonCode, token);
                var checkedFull = await files.VerifyExistingAsync(new(work.Task.TaskId, work.Attempt.Id, full.Endpoint, full.Path,
                    work.Snapshot.Policy.VerifyTimeoutMinutes * 60, full.Content), token);
                if (!checkedFull.IsSucceeded) return await RejectAsync(session, "baseline.local_full_unknown", token);
                fullHandle = checkedFull.Value!;
                g1 = await active.ReadAsync(Connection(work), new(work.Snapshot.Identity.DatabaseName,
                    Math.Min(work.Snapshot.Policy.VerifyTimeoutMinutes * 60, 3600)), token);
                var finalDecision = DifferentialBaselineRules.EvaluateActiveBaseline(invocation.Database, g1.Baseline, [full.Candidate]);
                if (!Equivalent(g0, g1) || finalDecision.Conclusion != DifferentialBaselineConclusion.Verified
                    || work.Attempt.AdmittedFullBackupSetId is { } previous && previous != full.BackupSetId)
                    return await RejectAsync(session, "baseline.changed_before_invocation", token);
            }
            var admission = await _flow.ReserveAsync(session, BackupExecutionOperationKind.Admission, true, token);
            if (admission.State == BackupExecutionOperationState.Reserved)
                await _flow.FreezeAsync(session, admission, new()
                {
                    Outcome = BackupExecutionOutcome.Succeeded,
                    EvidenceAtUtc = clock.GetUtcNow(),
                    ActualBaseBackupSetId = full?.BackupSetId,
                    Metadata = new()
                    {
                        DatabaseGuid = BackupMetadata.Known(invocation.Database.DatabaseGuid.Value),
                        FamilyGuid = BackupMetadata.Known(invocation.Database.FamilyGuid.Value),
                        RecoveryForkId = g1?.Baseline.CurrentRecoveryForkId is { } fork ? BackupMetadata.Known(fork) : default,
                        DifferentialBaseGuid = g1?.Baseline.DataFileBases[0].BaseBackupSetGuid is { } baseGuid ? BackupMetadata.Known(baseGuid) : default,
                        DifferentialBaseLsn = g1?.Baseline.DataFileBases[0].BaseLsn is { } baseLsn ? BackupMetadata.Known(baseLsn) : default
                    }
                }, AdmissionObservations(g0, g1), token);
            session = await _flow.FreshAsync(session, token);
            var authorization = new AuthorizeBackupInvocation(original.Identity.MutationId, original.Identity.MutationId,
                original.Identity.MutationId, work.Snapshot.Identity.DatabaseId, invocation.Binding, clock.GetUtcNow(), session.Work.Attempt.RowVersion);
            var grant = await _flow.CommitAsync(session, next => authorizations.AuthorizeAsync(next.Work.Lease, authorization, token), x => x.Code, token);
            if (grant.Code != BackupExecutionContractCode.Succeeded || grant.Value?.Lease is null)
                return grant.Code == BackupExecutionContractCode.DatabaseBlocked
                    ? await stages.SuspendAsync(session, "sql.database_blocked", token) : grant.Code;
            var result = await invocation.InvokeOnceAsync(grant.Value.PermitId, token);
            var completed = clock.GetUtcNow(); // SQL 返回时的平台观察事实，不用连接清理完成时间反填。
            // 先关闭原连接并封住句柄，再持久化实际返回的事实；后续失败不改写这个结果。
            await invocation.DisposeAsync();
            var success = result.IsSucceeded;
            var terminal = result.Outcome != TargetSqlOutcome.Indeterminate;
            original = await _flow.FreezeAsync(session, original, new()
            {
                Outcome = result.Outcome switch
                {
                    TargetSqlOutcome.Succeeded => BackupExecutionOutcome.Succeeded,
                    TargetSqlOutcome.ConfirmedFailed => BackupExecutionOutcome.ConfirmedFailed,
                    _ => BackupExecutionOutcome.Indeterminate
                },
                SqlOutcomeSource = BackupSqlOutcomeSource.PlatformResponse,
                SqlSuccessObserved = success,
                PlatformCompletedAtUtc = success ? completed : null,
                EvidenceAtUtc = completed,
                UsedCopyOnly = result.Value?.UsedCopyOnly,
                UsedChecksum = result.Value?.UsedChecksum,
                UsedCompression = result.Value?.UsedCompression,
                OriginalCallTerminated = terminal,
                OriginalCallerCannotInvoke = terminal,
                ReasonCode = result.Failure is { } failure ? $"sql.{failure.Code}" : null
            }, [], CancellationToken.None);
            return await _flow.ApplyAsync(session, original, null, CancellationToken.None);
        }
        finally { if (fullHandle is not null) await fullHandle.DisposeAsync(); }
    }

    private async Task<BackupExecutionContractCode> RejectAsync(BackupPlanExecutionSession session, string reason, CancellationToken token)
    {
        // 失败观察独立追加，不覆盖已经冻结的成功身份绑定。
        session = await _flow.FreshAsync(session, token);
        var sql = session.Operations.Single(x => x.Identity.Kind == BackupExecutionOperationKind.SqlResult);
        var identity = new ReserveBackupExecutionOperation(Guid.NewGuid(), session.Work.Task.TaskId, session.Work.Attempt.Id,
            BackupExecutionOperationKind.Admission, NextSequence(session.Operations, BackupExecutionOperationKind.Admission),
            sql.Identity.IntendedBackupSetId, clock.GetUtcNow());
        var reserved = await _flow.CommitAsync(session, next => operations.ReserveAsync(next.Work.Lease, identity, token), x => x.Code, token);
        Require(reserved.Code);
        var frozen = await _flow.FreezeAsync(session, reserved.Value!, new()
        { Outcome = BackupExecutionOutcome.ConfirmedFailed, SqlOutcomeSource = BackupSqlOutcomeSource.NotInvoked, ReasonCode = reason }, [], token);
        return await _flow.ApplyAsync(session, frozen, null, token);
    }

    private async Task<BackupExecutionContractCode> TransferAsync(BackupPlanExecutionSession session, CancellationToken token)
    {
        var work = session.Work;
        await using var dependency = await stages.HoldDependencyAsync(session, null, token);
        if (work.Snapshot.BackupType == BackupType.Differential && dependency is null)
            return await stages.SuspendAsync(session, "baseline.local_full_unknown", token);
        var local = session.Operations.Single(x => x.Identity.Kind == BackupExecutionOperationKind.VerifyLocal && x.State == BackupExecutionOperationState.Applied);
        var source = await files.OpenVerifiedAsync(File(work, false, work.Attempt.WorkerSourceFilePath, local.Facts.Content), token);
        if (!source.IsSucceeded) return await stages.SuspendAsync(session, "file.source_changed", token);
        await using var sourceHandle = source.Value!;
        var operation = await _flow.ReserveAsync(session, BackupExecutionOperationKind.Transfer, false, token);
        var part = await probe.InspectAsync(Endpoint(work.Snapshot.Policy.RemoteEndpoint!), work.Attempt.RemotePartialFilePath!, token);
        var final = await probe.InspectAsync(Endpoint(work.Snapshot.Policy.RemoteEndpoint!), work.Attempt.RemoteFinalFilePath!, token);
        if (!part.IsSucceeded || !final.IsSucceeded || final.Value!.Exists)
            return await stages.SuspendAsync(session, "file.remote_paths_ambiguous", token);
        if (part.Value!.Exists) return await stages.RecoverCopyAsync(session, token);
        if (operation.State != BackupExecutionOperationState.Reserved)
            return await stages.SuspendAsync(session, "file.partial_missing", token);
        var transferred = await transfer.TransferVerifiedAsync(operation.Identity.MutationId, sourceHandle,
            File(work, true, work.Attempt.RemotePartialFilePath!, CopyContent(local.Facts.Content)), token);
        if (!transferred.IsSucceeded || !transferred.Value!.MatchesVerifiedSource(local.Facts.Content))
            return await stages.SuspendAsync(session, "file.transfer_unknown", CancellationToken.None);
        operation = await _flow.FreezeAsync(session, operation, new()
        {
            Outcome = BackupExecutionOutcome.Succeeded,
            EvidenceAtUtc = clock.GetUtcNow(),
            Content = transferred.Value.PartialReadBackContent,
            SqlSuccessObserved = true
        }, [new(0, BackupExecutionObservationSource.SourceFile, BackupSetEvidenceKind.Backup, new() { Content = transferred.Value.SourceContent }),
            new(1, BackupExecutionObservationSource.SourceFile, BackupSetEvidenceKind.Backup, new() { Content = transferred.Value.SentContent, ReasonCode = "file.sent_content" }),
            new(2, BackupExecutionObservationSource.RemotePartial, BackupSetEvidenceKind.Backup, new() { Content = transferred.Value.PartialReadBackContent })], token);
        return await stages.RecoverCopyAsync(await _flow.FreshAsync(session, token), token);
    }

    private async Task<BackupExecutionContractCode> ConfirmCopyAsync(BackupPlanExecutionSession session, CancellationToken token)
    {
        var work = session.Work;
        await using var dependency = await stages.HoldDependencyAsync(session, null, token);
        if (work.Snapshot.BackupType == BackupType.Differential && dependency is null)
            return await stages.SuspendAsync(session, "baseline.local_full_unknown", token);
        var transferred = session.Operations.Single(x => x.Identity.Kind == BackupExecutionOperationKind.Transfer && x.State == BackupExecutionOperationState.Applied);
        var operation = await _flow.ReserveAsync(session, BackupExecutionOperationKind.ValidateCopy, false, token);
        var final = await probe.InspectAsync(Endpoint(work.Snapshot.Policy.RemoteEndpoint!), work.Attempt.RemoteFinalFilePath!, token);
        if (!final.IsSucceeded) return await stages.SuspendAsync(session, "file.final_unknown", token);
        if (final.Value!.Exists) return await stages.RecoverCopyAsync(session, token);
        if (operation.State != BackupExecutionOperationState.Reserved) return await stages.SuspendAsync(session, "file.final_missing", token);
        var renamed = await transfer.RenameAndVerifyAsync(operation.Identity.MutationId,
            File(work, true, work.Attempt.RemotePartialFilePath!, transferred.Facts.Content), work.Attempt.RemoteFinalFilePath!, token);
        if (!renamed.IsSucceeded) return await stages.SuspendAsync(session, "file.rename_unknown", CancellationToken.None);
        await using var handle = renamed.Value!.FinalHandle;
        var source = session.Operations.Single(x => x.Identity.Kind == BackupExecutionOperationKind.VerifyLocal && x.State == BackupExecutionOperationState.Applied);
        if (!renamed.Value.CanRegisterAvailable(source.Facts.Content, Endpoint(work.Snapshot.Policy.RemoteEndpoint!), work.Attempt.RemoteFinalFilePath!))
            return await stages.SuspendAsync(session, "file.confirmation_unknown", token);
        operation = await _flow.FreezeAsync(session, operation,
            new() { Outcome = BackupExecutionOutcome.Succeeded, EvidenceAtUtc = clock.GetUtcNow(), Content = handle.Content, SqlSuccessObserved = true },
            [new(0, BackupExecutionObservationSource.RemotePartial, BackupSetEvidenceKind.Backup, new() { Content = renamed.Value.BeforeRename }),
                new(1, BackupExecutionObservationSource.RemoteFinal, BackupSetEvidenceKind.Backup, new() { Content = renamed.Value.FinalReadBack })], token);
        return await _flow.ApplyAsync(session, operation, handle, dependency, token);
    }
    private static BackupExecutionObservationInput[] AdmissionObservations(BackupActiveMetadataEvidence? g0, BackupActiveMetadataEvidence? g1)
    {
        var result = new List<BackupExecutionObservationInput>();
        foreach (var (gate, name) in new[] { (g0, "admission.g0"), (g1, "admission.g1") })
        {
            if (gate is null) continue;
            foreach (var file in gate.Baseline.DataFileBases)
                result.Add(new(result.Count, BackupExecutionObservationSource.Database, BackupSetEvidenceKind.ActiveBaseline, new()
                {
                    ReasonCode = name,
                    Metadata = new()
                    {
                        DatabaseGuid = BackupMetadata.Known(gate.Baseline.Database.DatabaseGuid!.Value),
                        FamilyGuid = BackupMetadata.Known(gate.Baseline.Database.FamilyGuid!.Value),
                        RecoveryForkId = BackupMetadata.Known(gate.Baseline.CurrentRecoveryForkId!.Value),
                        DifferentialBaseGuid = BackupMetadata.Known(file.BaseBackupSetGuid!.Value),
                        DifferentialBaseLsn = BackupMetadata.Known(file.BaseLsn!.Value)
                    }
                }));
        }
        return result.ToArray();
    }
    private static bool Equivalent(BackupActiveMetadataEvidence a, BackupActiveMetadataEvidence b) => a.Baseline.Database == b.Baseline.Database
        && a.Baseline.CurrentRecoveryForkId == b.Baseline.CurrentRecoveryForkId && a.Baseline.Status == b.Baseline.Status
        && a.Baseline.DataFileBases.SequenceEqual(b.Baseline.DataFileBases);
}
