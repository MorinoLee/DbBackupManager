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
                    new() { Metadata = x.Metadata, Completion = x.Completion, Assessment = x.Assessment, SqlSuccessObserved = true })).ToArray(), token);
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
            if (next is null) return BackupExecutionContractCode.Succeeded;
            session = next;
            return await VerifyLocalAsync(session, token);
        }
        if (recovery && session.Work.Lease.Stage is BackupTaskStage.Transfer or BackupTaskStage.ValidateCopy)
            return await RecoverCopyAsync(session, token);
        return BackupExecutionContractCode.Succeeded;
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
        if (session.Authorization is null || session.Work.Attempt.ExpectedDatabaseGuid is null || session.Work.Attempt.ExpectedFamilyGuid is null)
            return await SuspendAsync(session, "sql.original_evidence_missing", token);
        var operation = await _flow.ReserveAsync(session, BackupExecutionOperationKind.Recovery, true, token);
        if (operation.State == BackupExecutionOperationState.Reserved)
        {
            var work = session.Work;
            var prepared = await metadata.PrepareAsync(new(work.Lease, original.Identity.IntendedBackupSetId, work.Snapshot.Identity.DatabaseId,
                operation.Identity.MutationId, operation.Identity.ObservedAtUtc, ExpectedIdentity(work), Connection(work),
                new(work.Attempt.Id, work.Snapshot.Identity.DatabaseName, work.Attempt.LocalSqlFilePath,
                    Math.Min(work.Snapshot.Policy.VerifyTimeoutMinutes * 60, 3600)), false, null), token);
            var command = prepared.Command;
            var complete = MetadataMatches(work, command.Metadata, command.Assessment, command.BaseBackupSetId, prepared.Evidence, false);
            if (!complete) return await SuspendAsync(session, "sql.recovery_unknown", token);
            var ended = await termination.ReadAsync(Connection(work), session.Authorization.PermitId, session.Authorization.Binding,
                Math.Min(work.Snapshot.Policy.VerifyTimeoutMinutes * 60, 3600), token);
            var terminal = ended.State == TargetSqlInvocationTerminationState.Terminated;
            var facts = new BackupExecutionFacts
            {
                Outcome = BackupExecutionOutcome.Succeeded,
                SqlOutcomeSource = BackupSqlOutcomeSource.RecoveredEvidence,
                SqlSuccessObserved = true,
                EvidenceAtUtc = clock.GetUtcNow(),
                Metadata = command.Metadata,
                Completion = command.Completion,
                Assessment = command.Assessment,
                ActualBaseBackupSetId = command.BaseBackupSetId,
                ActiveAssessment = prepared.ActiveBaseline,
                OriginalCallTerminated = terminal,
                OriginalCallerCannotInvoke = terminal,
                UsedCopyOnly = command.Metadata.IsCopyOnly.Value,
                UsedChecksum = command.Metadata.HasBackupChecksums.Value,
                UsedCompression = command.Metadata.IsCompressed.Value
            };
            var observations = command.Observations.Select((x, i) => new BackupExecutionObservationInput(i,
                Enum.Parse<BackupExecutionObservationSource>(x.Source.ToString()), x.Kind,
                new() { Metadata = x.Metadata, Completion = x.Completion, Assessment = x.Assessment, SqlSuccessObserved = true })).ToList();
            observations.Add(new(observations.Count, BackupExecutionObservationSource.Termination, BackupSetEvidenceKind.Backup,
                new() { OriginalCallTerminated = terminal, OriginalCallerCannotInvoke = terminal, ReasonCode = ended.ReasonCode }));
            operation = await _flow.FreezeAsync(session, operation, facts, observations, token);
        }
        if (operation.Facts.OriginalCallTerminated && operation.Facts.OriginalCallerCannotInvoke && session.Authorization.TerminalObservedAtUtc is null)
        {
            session = await _flow.FreshAsync(session, token);
            var command = new TerminateBackupInvocation(session.Authorization!.PermitId,
                operation.Identity.MutationId, operation.Identity.MutationId, BackupInvocationTerminationKind.RecoveredTerminated,
                operation.Facts.EvidenceAtUtc!.Value, true, true);
            var ended = await _flow.CommitAsync(session, next => authorizations.RecordTerminationAsync(next.Work.Lease, command, token), x => x.Code, token);
            Require(ended.Code);
        }
        return await _flow.ApplyAsync(session, operation, null, token);
    }

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
        && evidence.Header.Status == BaselineEvidenceStatus.Complete && evidence.History.Status == BaselineEvidenceStatus.Complete
        && evidence.Header.Issues.Count == 0 && evidence.History.Issues.Count == 0
        && BackupPlanExecutionFactRules.CompleteBackup(m, work.Snapshot.BackupType)
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
        await using var heartbeat = new BackupPlanHeartbeat(store, clock, request.TaskId, lease, cancellationToken);
        try { return await stages.RunAsync(opened.Value, true, heartbeat.Token); }
        catch (PlanExecutionLeaseLostException) { return BackupExecutionContractCode.LeaseLost; }
        catch (PlanExecutionConflictException exception) { return exception.Code; }
        catch (OperationCanceledException)
        {
            var fresh = await store.RefreshAsync(request.TaskId, lease, CancellationToken.None);
            if (fresh is not null) await stages.SuspendAsync(fresh, "plan.recovery_interrupted", CancellationToken.None);
            throw;
        }
    }
}
