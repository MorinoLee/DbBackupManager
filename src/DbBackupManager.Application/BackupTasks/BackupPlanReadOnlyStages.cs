using DbBackupManager.Application.BackupSets;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using static DbBackupManager.Application.BackupTasks.BackupPlanOperationFlow;

namespace DbBackupManager.Application.BackupTasks;

/// <summary>不依赖 BACKUP、传输、重命名或删除端口，恢复只能查证原 Attempt。</summary>
public sealed class BackupPlanReadOnlyStages(IBackupPlanExecutionStore store, IBackupExecutionOperationStore operations,
    BackupMetadataReconciliationService metadata, ITargetSqlReadOnlyProbe sql, IBackupFileContentVerifier files,
    IBackupFileStorageProbe probe, ITargetSqlInvocationTerminationProbe termination,
    IBackupInvocationAuthorizationStore authorizations, TimeProvider clock)
{
    private readonly BackupPlanOperationFlow _flow = new(store, operations, clock);

    public async Task<BackupExecutionContractCode> RunAsync(BackupPlanExecutionSession session, bool recovery, CancellationToken token)
    {
        session = await _flow.FreshAsync(session, token);
        if (session.Work.Lease.Stage == BackupTaskStage.Backup) return await RecoverSqlAsync(session, token);
        if (session.Work.Lease.Stage == BackupTaskStage.VerifyLocal)
        {
            var firstSource = session.Operations.Where(x => x.Identity.Kind == BackupExecutionOperationKind.Metadata
                && x.Facts.Content.State == BackupContentState.Verified).OrderBy(x => x.Identity.Sequence).FirstOrDefault()?.Facts.Content;
            var source = await files.OpenVerifiedAsync(File(session.Work, false, session.Work.Attempt.WorkerSourceFilePath, firstSource), token);
            if (!source.IsSucceeded) return await SuspendAsync(session, "file.source_unknown", token);
            await using var sourceHandle = source.Value!;
            var known = session.Operations.Where(x => x.Identity.Kind == BackupExecutionOperationKind.Metadata).OrderByDescending(x => x.Identity.Sequence).FirstOrDefault();
            var operation = await _flow.ReserveAsync(session, BackupExecutionOperationKind.Metadata,
                known is null || known.State != BackupExecutionOperationState.Frozen && known.Facts.Outcome != BackupExecutionOutcome.Succeeded, token);
            if (operation.State == BackupExecutionOperationState.Reserved)
            {
                var work = session.Work;
                var platform = session.Operations.Where(x => x.Identity.Kind == BackupExecutionOperationKind.SqlResult)
                    .Select(x => x.Facts.PlatformCompletedAtUtc).SingleOrDefault();
                var prepared = await metadata.PrepareAsync(new(work.Lease, operation.Identity.IntendedBackupSetId, work.Snapshot.Identity.DatabaseId,
                    operation.Identity.MutationId, operation.Identity.ObservedAtUtc, ExpectedIdentity(work), Connection(work),
                    new(work.Attempt.Id, work.Snapshot.Identity.DatabaseName, work.Attempt.LocalSqlFilePath,
                        Math.Min(work.Snapshot.Policy.VerifyTimeoutMinutes * 60, 3600)), true, platform), token);
                var command = prepared.Command;
                var verified = MetadataMatches(work, command.Metadata, command.Assessment, command.BaseBackupSetId, prepared.Evidence);
                if (verified && work.Snapshot.BackupType == BackupType.Differential)
                {
                    var full = await store.FindQualifiedFullAsync(work.Snapshot.Identity.DatabaseId, command.Metadata.DifferentialBaseGuid.Value!.Value, token);
                    verified = full?.BackupSetId == command.BaseBackupSetId;
                    if (verified)
                    {
                        var check = await files.VerifyExistingAsync(new(work.Task.TaskId, work.Attempt.Id, full!.Endpoint, full.Path,
                            work.Snapshot.Policy.VerifyTimeoutMinutes * 60, full.Content), token);
                        verified = check.IsSucceeded;
                        if (check.Value is { } handle) await handle.DisposeAsync();
                    }
                }
                var facts = new BackupExecutionFacts
                {
                    Outcome = verified ? BackupExecutionOutcome.Succeeded : BackupExecutionOutcome.Unknown,
                    SqlSuccessObserved = true,
                    Metadata = command.Metadata,
                    Completion = command.Completion,
                    Assessment = command.Assessment,
                    ActiveAssessment = prepared.ActiveBaseline,
                    ActualBaseBackupSetId = command.BaseBackupSetId,
                    Content = sourceHandle.Content,
                    ReasonCode = verified ? null : "metadata.not_verified"
                };
                operation = await _flow.FreezeAsync(session, operation, facts, command.Observations.Select((x, i) => new BackupExecutionObservationInput(i,
                    Enum.Parse<BackupExecutionObservationSource>(x.Source.ToString()), x.Kind,
                    new()
                    {
                        Metadata = x.Metadata,
                        Completion = x.Completion,
                        Assessment = x.Assessment,
                        SqlSuccessObserved = true,
                        EvidenceAtUtc = x.SourceObservedAtUtc,
                    })).ToArray(), token);
            }
            if (operation.State != BackupExecutionOperationState.Applied)
            {
                await using var dependency = operation.Facts.Outcome == BackupExecutionOutcome.Succeeded
                    ? await HoldDependencyAsync(session, operation.Facts, token) : null;
                if (session.Work.Snapshot.BackupType == BackupType.Differential && operation.Facts.Outcome == BackupExecutionOutcome.Succeeded && dependency is null)
                    return await SuspendAsync(session, "baseline.local_full_unknown", token);
                var applied = await _flow.ApplyAsync(session, operation, sourceHandle, dependency, token);
                Require(applied);
                if (operation.Facts.Outcome != BackupExecutionOutcome.Succeeded) return applied;
            }
            var next = await store.RefreshAsync(session.Work.Task.TaskId, session.Work.Lease.LeaseToken, token);
            if (next is null) return BackupExecutionContractCode.LeaseLost;
            session = next;
            return await VerifyLocalAsync(session, token);
        }
        if (recovery && session.Work.Lease.Stage is BackupTaskStage.Transfer or BackupTaskStage.ValidateCopy)
            return await RecoverCopyAsync(session, token);
        // RemoteOnly 的 Cleanup 也要有只读收口：暂存已经解开就补齐终态，否则释放租约交给执行路径，
        // 不能让任务带着核对租约停在 Cleanup，也不能在核对里删除文件。
        if (recovery && session.Work.Lease.Stage == BackupTaskStage.Cleanup
            && session.Work.Snapshot.Policy.StorageMode == BackupStorageMode.RemoteOnly)
            return await RecoverCleanupAsync(session, token);
        return BackupExecutionContractCode.Succeeded;
    }

    private async Task<BackupExecutionContractCode> RecoverCleanupAsync(BackupPlanExecutionSession session, CancellationToken token)
    {
        // 只读核对只拿核对租约：暂存记录已经解开时补齐终态，仍在时释放租约交给执行路径，
        // 由执行路径按原 Attempt 继续 Cleanup，核对不代删文件。
        return await _flow.CommitAsync(session,
            next => store.ReleaseDeferredCleanupForExecutionAsync(next.Work.Lease, token), x => x, token);
    }

    private async Task<BackupExecutionContractCode> RecoverSqlAsync(BackupPlanExecutionSession session, CancellationToken token)
    {
        var original = session.Operations.Single(x => x.Identity.Kind == BackupExecutionOperationKind.SqlResult);
        if (original.State == BackupExecutionOperationState.Frozen)
            return await _flow.ApplyAsync(session, original, null, token);
        if (session.Authorization is null && session.Work.Attempt.InvocationStatus == BackupInvocationStatus.Prepared)
        {
            var uninvoked = await _flow.ReserveAsync(session, BackupExecutionOperationKind.Recovery, true, token);
            if (uninvoked.State == BackupExecutionOperationState.Reserved)
                uninvoked = await _flow.FreezeAsync(session, uninvoked, new()
                {
                    Outcome = BackupExecutionOutcome.Succeeded,
                    SqlOutcomeSource = BackupSqlOutcomeSource.NotInvoked,
                    ReasonCode = "sql.prepared_never_authorized"
                }, [], token);
            return await _flow.ApplyAsync(session, uninvoked, null, token);
        }
        // 没有授权或没有调用前固定的 SQL 身份：没有可核对的原绑定，继续阻塞。
        if (session.Authorization is null || session.Work.Attempt.ExpectedDatabaseGuid is null
            || session.Work.Attempt.ExpectedFamilyGuid is null)
            return await SuspendAsync(session, "sql.recovery_original_binding_missing", token);
        // 终止身份必须完整；缺少原连接或会话时间时未知不能冒充终止。
        if (session.Authorization.Binding.State != BackupInvocationBindingState.Known
            || session.Authorization.Binding.ConnectionId is null
            || session.Authorization.Binding.SessionId is not > 0
            || session.Authorization.Binding.SessionEstablishedLocal is null)
            return await SuspendAsync(session, "sql.recovery_original_binding_unknown", token);

        var authorization = session.Authorization;
        // 终止证据已经独立提交：只按原冻结观察继续或收口，不重新探测、不覆盖已确认的成功时间。
        if (authorization.TerminalObservedAtUtc is { } terminatedAt)
        {
            if (authorization.TerminationKind != BackupInvocationTerminationKind.RecoveredTerminated)
                return await SuspendAsync(session, "sql.recovery_termination_evidence_missing", token);
            var saved = session.Operations
                .Where(x => x.Identity.Kind == BackupExecutionOperationKind.Recovery
                    && x.State is BackupExecutionOperationState.Frozen or BackupExecutionOperationState.Applied
                    && x.Identity.MutationId == authorization.TerminationEvidenceId
                    && x.Facts.EvidenceAtUtc == terminatedAt
                    && x.Facts.OriginalCallTerminated && x.Facts.OriginalCallerCannotInvoke)
                .OrderByDescending(x => x.Identity.Sequence)
                .FirstOrDefault();
            if (saved is null) return await SuspendAsync(session, "sql.recovery_termination_evidence_missing", token);
            if (saved.Facts.SqlSuccessObserved) return await _flow.ApplyAsync(session, saved, null, token);
            return await CloseUnknownResultAsync(session, terminatedAt, token);
        }

        var operation = await _flow.ReserveAsync(session, BackupExecutionOperationKind.Recovery, true, token);
        if (operation.State == BackupExecutionOperationState.Reserved)
        {
            var work = session.Work;
            var previous = session.Operations.Where(x => x.Identity.Kind == BackupExecutionOperationKind.Recovery
                && x.State == BackupExecutionOperationState.Applied && x.Facts.SqlSuccessObserved
                && x.Facts.SqlOutcomeSource == BackupSqlOutcomeSource.RecoveredEvidence).OrderByDescending(x => x.Identity.Sequence).FirstOrDefault();
            BackupExecutionFacts facts;
            string? metadataReason = null;
            var observations = new List<BackupExecutionObservationInput>();
            if (previous is not null)
            {
                facts = previous.Facts; // 已确认的 SQL 身份、字段和完成来源保持原值，只追加终止观察。
                observations.AddRange(previous.Observations.Where(x => x.Source != BackupExecutionObservationSource.Termination)
                    .Select((x, i) => x with { EntryNumber = i }));
            }
            else
            {
                // 成功元数据只是成功证据，不再是终止探测的前提；无论是否完整都必须独立探测终止。
                var prepared = await metadata.PrepareAsync(new(work.Lease, original.Identity.IntendedBackupSetId, work.Snapshot.Identity.DatabaseId,
                    operation.Identity.MutationId, operation.Identity.ObservedAtUtc, ExpectedIdentity(work), Connection(work),
                    new(work.Attempt.Id, work.Snapshot.Identity.DatabaseName, work.Attempt.LocalSqlFilePath,
                        Math.Min(work.Snapshot.Policy.VerifyTimeoutMinutes * 60, 3600)), false, null), token);
                var command = prepared.Command;
                var complete = MetadataMatches(work, command.Metadata, command.Assessment, command.BaseBackupSetId, prepared.Evidence, false);
                if (complete)
                {
                    facts = new()
                    {
                        Outcome = BackupExecutionOutcome.Succeeded,
                        SqlOutcomeSource = BackupSqlOutcomeSource.RecoveredEvidence,
                        SqlSuccessObserved = true,
                        Metadata = command.Metadata,
                        Completion = command.Completion,
                        Assessment = command.Assessment,
                        ActualBaseBackupSetId = command.BaseBackupSetId,
                        ActiveAssessment = prepared.ActiveBaseline,
                        UsedCopyOnly = command.Metadata.IsCopyOnly.Value,
                        UsedChecksum = command.Metadata.HasBackupChecksums.Value,
                        UsedCompression = command.Metadata.IsCompressed.Value
                    };
                }
                else
                {
                    // 证据不足、缺失或矛盾时只记录未知结果，不得反推 SQL 明确失败或未调用，也不制造成功。
                    metadataReason = MetadataFailureReason(prepared.Evidence);
                    facts = new()
                    {
                        Outcome = BackupExecutionOutcome.Unknown,
                        SqlOutcomeSource = BackupSqlOutcomeSource.RecoveredEvidence,
                        Metadata = command.Metadata,
                        Completion = command.Completion,
                        Assessment = command.Assessment,
                        ActiveAssessment = prepared.ActiveBaseline,
                        ActualBaseBackupSetId = command.BaseBackupSetId,
                        ReasonCode = metadataReason
                    };
                }
                observations.AddRange(command.Observations.Select((x, i) => new BackupExecutionObservationInput(i,
                    Enum.Parse<BackupExecutionObservationSource>(x.Source.ToString()), x.Kind,
                    new()
                    {
                        Metadata = x.Metadata,
                        Completion = x.Completion,
                        Assessment = x.Assessment,
                        SqlSuccessObserved = complete,
                        EvidenceAtUtc = x.SourceObservedAtUtc,
                    })));
            }
            var ended = await termination.ReadAsync(Connection(work), authorization.PermitId, authorization.Binding,
                Math.Min(work.Snapshot.Policy.VerifyTimeoutMinutes * 60, 3600), token);
            var terminal = ended.State == TargetSqlInvocationTerminationState.Terminated
                && BackupInvocationTerminationEvidenceRules.IsPositiveTerminationReason(ended.ReasonCode);
            if (!terminal && !facts.SqlSuccessObserved)
                return await SuspendAsync(session, TerminationBlockReason(ended), token);
            facts = facts with
            {
                EvidenceAtUtc = clock.GetUtcNow(),
                OriginalCallTerminated = terminal,
                OriginalCallerCannotInvoke = terminal,
                ReasonCode = facts.SqlSuccessObserved ? terminal ? null : TerminationBlockReason(ended) : metadataReason
            };
            observations.Add(new(observations.Count, BackupExecutionObservationSource.Termination, BackupSetEvidenceKind.Backup,
                new() { OriginalCallTerminated = terminal, OriginalCallerCannotInvoke = terminal, ReasonCode = ended.ReasonCode }));
            operation = await _flow.FreezeAsync(session, operation, facts, observations, token);
        }
        if (operation.Facts.OriginalCallTerminated && operation.Facts.OriginalCallerCannotInvoke)
        {
            if (session.Authorization.TerminalObservedAtUtc is null)
            {
                session = await _flow.FreshAsync(session, token);
                var command = new TerminateBackupInvocation(session.Authorization!.PermitId,
                    operation.Identity.MutationId, operation.Identity.MutationId, BackupInvocationTerminationKind.RecoveredTerminated,
                    operation.Facts.EvidenceAtUtc!.Value, true, true);
                var recorded = await _flow.CommitAsync(session, next => authorizations.RecordTerminationAsync(next.Work.Lease, command, token), x => x.Code, token);
                Require(recorded.Code);
            }
            // 原调用已终止，但没有可证明的成功证据：独立登记终止后以未知结果收口，不再走成功阶段。
            if (!operation.Facts.SqlSuccessObserved)
                return await CloseUnknownResultAsync(session, operation.Facts.EvidenceAtUtc!.Value, token);
        }
        return await _flow.ApplyAsync(session, operation, null, token);
    }

    private async Task<BackupExecutionContractCode> CloseUnknownResultAsync(BackupPlanExecutionSession session,
        DateTimeOffset observedAtUtc, CancellationToken token)
    {
        session = await _flow.FreshAsync(session, token);
        // 关单身份使用已记录的终止 MutationId；同一证据的响应丢失或重复核对不产生第二次历史或通知。
        var mutationId = session.Authorization?.TerminationMutationId
            ?? throw new PlanExecutionConflictException(BackupExecutionContractCode.Conflict);
        var code = await _flow.CommitAsync(session, next => store.CloseUnknownAsync(next.Work.Lease,
            mutationId, "sql.recovery_result_unknown_closed", observedAtUtc, token), x => x, token);
        Require(code);
        return code;
    }

    private static string TerminationBlockReason(TargetSqlInvocationTerminationEvidence evidence) =>
        evidence.State == TargetSqlInvocationTerminationState.Active
            ? "sql.recovery_original_call_active"
            : evidence.ReasonCode switch
            {
                "termination.permission_denied" => "sql.recovery_termination_permission_denied",
                "termination.binding_unknown" => "sql.recovery_original_binding_unknown",
                _ => "sql.recovery_termination_read_failed"
            };

    private static string MetadataFailureReason(TargetSqlBackupMetadataEvidence? evidence) =>
        evidence is not null && evidence.Header.HasRequiredFields && evidence.History.HasRequiredFields
            ? "sql.recovery_metadata_conflict"
            : "sql.recovery_metadata_insufficient";

    private async Task<BackupExecutionContractCode> VerifyLocalAsync(BackupPlanExecutionSession session, CancellationToken token)
    {
        var work = session.Work;
        await using var dependency = await HoldDependencyAsync(session, null, token);
        if (work.Snapshot.BackupType == BackupType.Differential && dependency is null)
            return await SuspendAsync(session, "baseline.local_full_unknown", token);
        var operation = await _flow.ReserveAsync(session, BackupExecutionOperationKind.VerifyLocal, true, token);
        var expected = operation.State == BackupExecutionOperationState.Frozen ? operation.Facts.Content
            : session.Operations.Where(x => x.Identity.Kind == BackupExecutionOperationKind.Metadata && x.State == BackupExecutionOperationState.Applied)
                .OrderByDescending(x => x.Identity.Sequence).FirstOrDefault()?.Facts.Content;
        var inspected = await files.OpenVerifiedAsync(File(work, false, work.Attempt.WorkerSourceFilePath, expected), token);
        if (!inspected.IsSucceeded) return await SuspendAsync(session, "file.source_unknown", token);
        await using var handle = inspected.Value!;
        if (operation.State == BackupExecutionOperationState.Reserved)
        {
            var verified = await sql.VerifyBackupAsync(Connection(work), new(work.Attempt.LocalSqlFilePath,
                work.Snapshot.Policy.UseChecksum, work.Snapshot.Policy.VerifyTimeoutMinutes * 60), token);
            if (!verified.IsSucceeded) return await SuspendAsync(session, "file.verify_failed", token);
            operation = await _flow.FreezeAsync(session, operation, new()
            {
                Outcome = BackupExecutionOutcome.Succeeded,
                EvidenceAtUtc = clock.GetUtcNow(),
                Content = handle.Content,
                SqlSuccessObserved = true
            }, [new(0, BackupExecutionObservationSource.SourceFile, BackupSetEvidenceKind.Backup, new() { Content = handle.Content })], token);
        }
        return await _flow.ApplyAsync(session, operation, handle, dependency, token);
    }

    internal async Task<BackupExecutionContractCode> RecoverCopyAsync(BackupPlanExecutionSession session, CancellationToken token)
    {
        var work = session.Work;
        await using var dependency = await HoldDependencyAsync(session, null, token);
        if (work.Snapshot.BackupType == BackupType.Differential && dependency is null)
            return await SuspendAsync(session, "baseline.local_full_unknown", token);
        var kind = work.Lease.Stage == BackupTaskStage.Transfer ? BackupExecutionOperationKind.Transfer : BackupExecutionOperationKind.ValidateCopy;
        var local = session.Operations.SingleOrDefault(x => x.Identity.Kind == BackupExecutionOperationKind.VerifyLocal && x.State == BackupExecutionOperationState.Applied);
        if (local is null) return await SuspendAsync(session, "file.source_evidence_missing", token);
        if (work.Lease.Purpose == BackupLeasePurpose.Reconciliation && !session.Operations.Any(x => x.Identity.Kind == kind))
        {
            var unwritten = await _flow.ReserveAsync(session, BackupExecutionOperationKind.Recovery, true, token);
            unwritten = await _flow.FreezeAsync(session, unwritten, new()
            { Outcome = BackupExecutionOutcome.Succeeded, ReasonCode = "file.write_not_reserved" }, [], token);
            return await _flow.ApplyAsync(session, unwritten, null, dependency, token);
        }
        var operation = await _flow.ReserveAsync(session, kind, false, token);
        var part = await probe.InspectAsync(Endpoint(work.Snapshot.Policy.RemoteEndpoint!), work.Attempt.RemotePartialFilePath!, token);
        var final = await probe.InspectAsync(Endpoint(work.Snapshot.Policy.RemoteEndpoint!), work.Attempt.RemoteFinalFilePath!, token);
        if (!part.IsSucceeded || !final.IsSucceeded || part.Value!.Exists && final.Value!.Exists
            || kind == BackupExecutionOperationKind.Transfer && (!part.Value!.Exists || final.Value!.Exists)
            || kind == BackupExecutionOperationKind.ValidateCopy && (part.Value!.Exists || !final.Value!.Exists))
            return await SuspendAsync(session, "file.remote_paths_ambiguous", token);
        var expected = operation.State == BackupExecutionOperationState.Frozen ? operation.Facts.Content
            : kind == BackupExecutionOperationKind.ValidateCopy
                ? session.Operations.Single(x => x.Identity.Kind == BackupExecutionOperationKind.Transfer && x.State == BackupExecutionOperationState.Applied).Facts.Content
                : CopyContent(local.Facts.Content);
        var path = kind == BackupExecutionOperationKind.Transfer ? work.Attempt.RemotePartialFilePath! : work.Attempt.RemoteFinalFilePath!;
        var checkedFile = await files.VerifyExistingAsync(File(work, true, path, expected), token);
        if (!checkedFile.IsSucceeded) return await SuspendAsync(session, "file.remote_content_unknown", token);
        await using var handle = checkedFile.Value!;
        if (operation.State == BackupExecutionOperationState.Reserved)
            operation = await _flow.FreezeAsync(session, operation,
                new() { Outcome = BackupExecutionOutcome.Succeeded, EvidenceAtUtc = clock.GetUtcNow(), Content = handle.Content, SqlSuccessObserved = true },
                [new(0, kind == BackupExecutionOperationKind.Transfer ? BackupExecutionObservationSource.RemotePartial : BackupExecutionObservationSource.RemoteFinal,
                    BackupSetEvidenceKind.Backup, new() { Content = handle.Content })], token);
        return await _flow.ApplyAsync(session, operation, handle, dependency, token);
    }

    internal async Task<BackupExecutionContractCode> SuspendAsync(BackupPlanExecutionSession session, string reason, CancellationToken token)
    {
        session = await _flow.FreshAsync(session, token);
        var mutationId = Guid.NewGuid();
        return await _flow.CommitAsync(session, next => store.SuspendAsync(next.Work.Lease, mutationId, reason, token), x => x, token);
    }
    internal async Task<IVerifiedBackupFileHandle?> HoldDependencyAsync(BackupPlanExecutionSession session, BackupExecutionFacts? facts, CancellationToken token)
    {
        var work = session.Work;
        if (work.Snapshot.BackupType != BackupType.Differential) return null;
        facts ??= session.Operations.Where(x => x.Identity.Kind == BackupExecutionOperationKind.Metadata && x.State == BackupExecutionOperationState.Applied)
            .OrderByDescending(x => x.Identity.Sequence).FirstOrDefault()?.Facts;
        if (facts?.Metadata.DifferentialBaseGuid.Value is not { } guid) return null;
        var full = await store.FindQualifiedFullAsync(work.Snapshot.Identity.DatabaseId, guid, token);
        if (full is null || full.BackupSetId != facts.ActualBaseBackupSetId) return null;
        var checkedFile = await files.VerifyExistingAsync(new(work.Task.TaskId, work.Attempt.Id, full.Endpoint, full.Path,
            work.Snapshot.Policy.VerifyTimeoutMinutes * 60, full.Content), token);
        return checkedFile.Value;
    }
    private static bool MetadataMatches(BackupExecutionWorkItem work, BackupSetMetadata m, BackupSetAssessment assessment,
        Guid? baseId, TargetSqlBackupMetadataEvidence? evidence, bool requireManagedDependency = true) => evidence is not null
        && evidence.Header.HasRequiredFields && evidence.History.HasRequiredFields
        && BackupPlanExecutionFactRules.CompleteBackup(m, work.Snapshot.BackupType,
            BackupPlanExecutionFactRules.ExpectedCopyOnly(work.Snapshot.BackupType, work.Snapshot.Purpose!.Value))
        && m.DatabaseGuid.Value == work.Attempt.ExpectedDatabaseGuid && m.FamilyGuid.Value == work.Attempt.ExpectedFamilyGuid
        && m.HasBackupChecksums.Value == work.Snapshot.Policy.UseChecksum && m.IsCompressed.Value == work.Snapshot.Policy.UseCompression
        && (!requireManagedDependency || (work.Snapshot.BackupType == BackupType.Full ? assessment == BackupSetAssessment.NotApplicable
            : assessment.Conclusion == DifferentialBaselineConclusion.Verified && baseId is not null));
}

public sealed class BackupPlanTaskReconciler(IBackupPlanExecutionStore store, BackupPlanReadOnlyStages stages, TimeProvider clock)
    : IBackupPlanTaskReconciler
{
    public async Task<BackupExecutionContractCode> ReconcilePlanTaskOnceAsync(ReconcileBackupPlanTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lease = Guid.NewGuid();
        var opened = await store.OpenAsync(request.TaskId, request.RequestMutationId, true, lease, Guid.NewGuid().ToString("N"),
            clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(2), cancellationToken);
        if (opened.Value is null) return opened.Code;
        await using var heartbeat = new BackupPlanHeartbeat(store, clock, request.TaskId, lease, opened.Value.Work.Lease.ExpiresAtUtc, cancellationToken);
        try { return await stages.RunAsync(opened.Value, true, heartbeat.Token); }
        catch (PlanExecutionLeaseLostException) { return BackupExecutionContractCode.LeaseLost; }
        catch (PlanExecutionConflictException exception) { return exception.Code; }
        catch (OperationCanceledException)
        {
            var fresh = await store.RefreshAsync(request.TaskId, lease, CancellationToken.None);
            if (fresh is null) return BackupExecutionContractCode.LeaseLost;
            try { await stages.SuspendAsync(fresh, "plan.recovery_interrupted", CancellationToken.None); }
            catch { /* 暂停失败仍把取消交给调用方。 */ }
            throw;
        }
    }
}
