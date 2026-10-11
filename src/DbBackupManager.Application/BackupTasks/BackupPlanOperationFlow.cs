using DbBackupManager.Application.FileStorage;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.BackupTasks;

internal sealed class BackupPlanOperationFlow(IBackupPlanExecutionStore store, IBackupExecutionOperationStore operations, TimeProvider clock)
{
    public async Task<BackupPlanExecutionSession> FreshAsync(BackupPlanExecutionSession session, CancellationToken token) =>
        await store.RefreshAsync(session.Work.Task.TaskId, session.Work.Lease.LeaseToken, token)
        ?? throw new PlanExecutionLeaseLostException();

    public async Task<FrozenBackupExecutionOperation> ReserveAsync(BackupPlanExecutionSession session, BackupExecutionOperationKind kind,
        bool newObservation, CancellationToken token)
    {
        session = await FreshAsync(session, token);
        var previous = session.Operations.Where(x => x.Identity.Kind == kind).OrderByDescending(x => x.Identity.Sequence).FirstOrDefault();
        if (previous is not null && (!newObservation || previous.State == BackupExecutionOperationState.Frozen && kind != BackupExecutionOperationKind.Admission)) return previous;
        if (previous?.State == BackupExecutionOperationState.Reserved && kind is not (BackupExecutionOperationKind.SqlResult or BackupExecutionOperationKind.Transfer or BackupExecutionOperationKind.ValidateCopy))
        {
            var abandoned = await operations.AbandonAsync(session.Work.Lease, previous.Identity, token);
            Require(abandoned.Code);
        }
        else if (previous?.State == BackupExecutionOperationState.Reserved) return previous;
        var sql = session.Operations.Single(x => x.Identity.Kind == BackupExecutionOperationKind.SqlResult);
        var identity = new ReserveBackupExecutionOperation(Guid.NewGuid(), session.Work.Task.TaskId, session.Work.Attempt.Id,
            kind, (previous?.Identity.Sequence ?? 0) + 1, sql.Identity.IntendedBackupSetId, clock.GetUtcNow());
        var result = await operations.ReserveAsync(session.Work.Lease, identity, token);
        Require(result.Code);
        return result.Value!;
    }
    public async Task<FrozenBackupExecutionOperation> FreezeAsync(BackupPlanExecutionSession session, FrozenBackupExecutionOperation operation,
        BackupExecutionFacts facts, IReadOnlyList<BackupExecutionObservationInput> observations, CancellationToken token)
    {
        session = await FreshAsync(session, token);
        var result = await operations.FreezeAsync(session.Work.Lease, new(operation.Identity, facts, observations), token);
        Require(result.Code);
        return result.Value!;
    }
    public Task<BackupExecutionContractCode> ApplyAsync(BackupPlanExecutionSession session, FrozenBackupExecutionOperation operation,
        IVerifiedBackupFileHandle? handle, CancellationToken token) => ApplyAsync(session, operation, handle, null, token);
    public async Task<BackupExecutionContractCode> ApplyAsync(BackupPlanExecutionSession session, FrozenBackupExecutionOperation operation,
        IVerifiedBackupFileHandle? handle, IVerifiedBackupFileHandle? dependency, CancellationToken token)
    {
        session = await FreshAsync(session, token);
        return await store.ApplyAsync(session.Work.Lease, operation.Identity, handle, dependency, token);
    }
    public static void Require(BackupExecutionContractCode code)
    {
        if (code is BackupExecutionContractCode.Succeeded or BackupExecutionContractCode.AlreadyApplied) return;
        if (code == BackupExecutionContractCode.LeaseLost) throw new PlanExecutionLeaseLostException();
        throw new PlanExecutionConflictException(code);
    }
    public static TargetSqlConnectionInput Connection(BackupExecutionWorkItem work)
    {
        var sql = work.Snapshot.SqlTarget;
        return new(sql.ConnectionAddress, sql.SqlCredentialReferenceId, sql.EncryptConnection, sql.TrustServerCertificate,
            sql.CertificateTrustReason, sql.ConnectionTimeoutSeconds, sql.AllowLegacyTls, sql.LegacyTlsReason);
    }
    public static BackupFileEndpointInput Endpoint(BackupFileEndpointModel model) =>
        new(model.Protocol, model.Host, model.Port, model.BasePath, model.CredentialReferenceId, model.SftpHostKeyFingerprint);
    public static BackupDatabaseIdentity ExpectedIdentity(BackupExecutionWorkItem work) =>
        new(work.Attempt.ExpectedDatabaseGuid, work.Attempt.ExpectedFamilyGuid);
    public static BackupContentVerificationRequest File(BackupExecutionWorkItem work, bool remote, string path, BackupContentEvidence? expected = null) =>
        new(work.Task.TaskId, work.Attempt.Id, Endpoint(remote ? work.Snapshot.Policy.RemoteEndpoint! : work.Snapshot.WorkerSourceEndpoint),
            path, work.Snapshot.Policy.TransferTimeoutMinutes * 60, expected);
    public static BackupContentEvidence CopyContent(BackupContentEvidence source) => source with { StableObjectId = null };
}

internal sealed class PlanExecutionLeaseLostException : Exception;
internal sealed class PlanExecutionConflictException(BackupExecutionContractCode code) : Exception
{
    public BackupExecutionContractCode Code { get; } = code;
}

internal sealed class BackupPlanHeartbeat : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _io;
    private readonly Task _loop;
    public BackupPlanHeartbeat(IBackupPlanExecutionStore store, TimeProvider clock, Guid taskId, Guid leaseToken, CancellationToken caller)
    {
        _io = CancellationTokenSource.CreateLinkedTokenSource(caller);
        _loop = LoopAsync(store, clock, taskId, leaseToken);
    }
    public CancellationToken Token => _io.Token;
    private async Task LoopAsync(IBackupPlanExecutionStore store, TimeProvider clock, Guid task, Guid lease)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                var code = await store.RenewAsync(task, lease, clock.GetUtcNow().AddMinutes(2), _stop.Token);
                if (code is not (BackupExecutionContractCode.Succeeded or BackupExecutionContractCode.AlreadyApplied))
                { await _io.CancelAsync(); break; }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch { await _io.CancelAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _loop; }
        finally { _stop.Dispose(); _io.Dispose(); }
    }
}
