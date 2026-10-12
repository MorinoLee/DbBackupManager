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

    // 只重放同一持久化命令；续租改变 RowVersion 时刷新，失租或阶段变化立即停止。
    public async Task<T> CommitAsync<T>(BackupPlanExecutionSession session, Func<BackupPlanExecutionSession, Task<T>> command,
        Func<T, BackupExecutionContractCode> code, CancellationToken token)
    {
        var current = await FreshAsync(session, token);
        for (var retry = 0; ; retry++)
        {
            var result = await command(current);
            if (code(result) != BackupExecutionContractCode.LeaseLost || retry >= 2) return result;
            var next = await store.RefreshAsync(current.Work.Task.TaskId, current.Work.Lease.LeaseToken, token);
            var before = current.Work.Lease;
            if (next is null || next.Work.Lease.Purpose != before.Purpose || next.Work.Lease.Stage != before.Stage
                || next.Work.Lease.BackupAttemptId != before.BackupAttemptId
                || next.Work.Lease.RowVersion.SequenceEqual(before.RowVersion)) return result;
            current = next;
        }
    }

    public async Task<FrozenBackupExecutionOperation> ReserveAsync(BackupPlanExecutionSession session, BackupExecutionOperationKind kind,
        bool newObservation, CancellationToken token)
    {
        session = await FreshAsync(session, token);
        var previous = session.Operations.Where(x => x.Identity.Kind == kind).OrderByDescending(x => x.Identity.Sequence).FirstOrDefault();
        if (previous is not null && (!newObservation || previous.State == BackupExecutionOperationState.Frozen && kind != BackupExecutionOperationKind.Admission)) return previous;
        if (previous?.State == BackupExecutionOperationState.Reserved && kind is not (BackupExecutionOperationKind.SqlResult or BackupExecutionOperationKind.Transfer or BackupExecutionOperationKind.ValidateCopy))
        {
            var abandoned = await CommitAsync(session, next => operations.AbandonAsync(next.Work.Lease, previous.Identity, token), x => x.Code, token);
            Require(abandoned.Code);
        }
        else if (previous?.State == BackupExecutionOperationState.Reserved) return previous;
        var sql = session.Operations.Single(x => x.Identity.Kind == BackupExecutionOperationKind.SqlResult);
        var identity = new ReserveBackupExecutionOperation(Guid.NewGuid(), session.Work.Task.TaskId, session.Work.Attempt.Id,
            kind, NextSequence(session.Operations, kind), sql.Identity.IntendedBackupSetId, clock.GetUtcNow());
        var result = await CommitAsync(session, next => operations.ReserveAsync(next.Work.Lease, identity, token), x => x.Code, token);
        Require(result.Code);
        return result.Value!;
    }
    public async Task<FrozenBackupExecutionOperation> FreezeAsync(BackupPlanExecutionSession session, FrozenBackupExecutionOperation operation,
        BackupExecutionFacts facts, IReadOnlyList<BackupExecutionObservationInput> observations, CancellationToken token)
    {
        var command = new FreezeBackupExecutionOperation(operation.Identity, facts, observations);
        var result = await CommitAsync(session, next => operations.FreezeAsync(next.Work.Lease, command, token), x => x.Code, token);
        Require(result.Code);
        return result.Value!;
    }
    public Task<BackupExecutionContractCode> ApplyAsync(BackupPlanExecutionSession session, FrozenBackupExecutionOperation operation,
        IVerifiedBackupFileHandle? handle, CancellationToken token) => ApplyAsync(session, operation, handle, null, token);
    public Task<BackupExecutionContractCode> ApplyAsync(BackupPlanExecutionSession session, FrozenBackupExecutionOperation operation,
        IVerifiedBackupFileHandle? handle, IVerifiedBackupFileHandle? dependency, CancellationToken token)
    {
        return CommitAsync(session, next => store.ApplyAsync(next.Work.Lease, operation.Identity, handle, dependency, token), x => x, token);
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
    internal static int NextSequence(IReadOnlyList<FrozenBackupExecutionOperation> operations, BackupExecutionOperationKind kind)
    {
        var max = 0;
        foreach (var operation in operations)
            if (operation.Identity.Kind == kind && operation.Identity.Sequence > max) max = operation.Identity.Sequence;
        return max + 1;
    }
}

internal sealed class PlanExecutionLeaseLostException : Exception;
internal sealed class PlanExecutionConflictException(BackupExecutionContractCode code) : Exception
{
    public BackupExecutionContractCode Code { get; } = code;
}

internal sealed class BackupPlanHeartbeat : IAsyncDisposable
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    // 续租间隔 20 秒、租约 2 分钟；留出多次重试后，只在剩余时间不够再等一轮时取消外部 I/O。
    internal static readonly TimeSpan NearExpiry = TimeSpan.FromSeconds(30);
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _io;
    private readonly Task _loop;
    private readonly TimeProvider _clock;
    private DateTimeOffset _expiresAt;
    internal Exception? LastFault { get; private set; }
    public BackupPlanHeartbeat(IBackupPlanExecutionStore store, TimeProvider clock, Guid taskId, Guid leaseToken,
        DateTimeOffset leaseExpiresAt, CancellationToken caller, Func<CancellationToken, Task>? wait = null)
    {
        _clock = clock;
        _expiresAt = leaseExpiresAt;
        _io = CancellationTokenSource.CreateLinkedTokenSource(caller);
        _loop = LoopAsync(store, taskId, leaseToken, wait ?? (static token => Task.Delay(Interval, token)));
    }
    public CancellationToken Token => _io.Token;
    private async Task LoopAsync(IBackupPlanExecutionStore store, Guid task, Guid lease, Func<CancellationToken, Task> wait)
    {
        try
        {
            while (true)
            {
                await wait(_stop.Token);
                try
                {
                    var requested = _clock.GetUtcNow().Add(LeaseDuration);
                    var code = await store.RenewAsync(task, lease, requested, _stop.Token);
                    if (code is BackupExecutionContractCode.Succeeded or BackupExecutionContractCode.AlreadyApplied)
                    {
                        _expiresAt = requested;
                        LastFault = null;
                        continue;
                    }
                    if (CancelIo(code)) break;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    LastFault = exception;
                    if (CancelIo(null)) break;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private bool CancelIo(BackupExecutionContractCode? code)
    {
        if (code is BackupExecutionContractCode.Succeeded or BackupExecutionContractCode.AlreadyApplied) return false;
        if (code != BackupExecutionContractCode.LeaseLost && _clock.GetUtcNow() < _expiresAt - NearExpiry) return false;
        _io.Cancel();
        return true;
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stop.CancelAsync();
            await _loop;
        }
        catch (Exception exception)
        {
            // 已提交的执行结果必须保留；续租失败只留在心跳上，不从 Dispose 再抛出。
            LastFault = exception;
        }
        finally { _stop.Dispose(); _io.Dispose(); }
    }
}
