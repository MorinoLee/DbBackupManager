using System.Data;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupExecutionOperationStore(IDbContextFactory<PlatformDbContext> factory, TimeProvider clock)
    : IBackupExecutionOperationStore
{
    public Task<BackupExecutionContractResult<FrozenBackupExecutionOperation>> ReserveAsync(
        LeaseHandle lease, ReserveBackupExecutionOperation command, CancellationToken cancellationToken = default) =>
        MutateAsync(lease, command, null, false, cancellationToken);

    public Task<BackupExecutionContractResult<FrozenBackupExecutionOperation>> FreezeAsync(
        LeaseHandle lease, FreezeBackupExecutionOperation command, CancellationToken cancellationToken = default) =>
        MutateAsync(lease, command.Identity, command, false, cancellationToken);

    public Task<BackupExecutionContractResult<FrozenBackupExecutionOperation>> AbandonAsync(
        LeaseHandle lease, ReserveBackupExecutionOperation identity, CancellationToken cancellationToken = default) =>
        MutateAsync(lease, identity, null, true, cancellationToken);

    public async Task<BackupExecutionContractResult<FrozenBackupExecutionOperation>> ApplyFrozenAsync(
        LeaseHandle lease, ReserveBackupExecutionOperation identity, CancellationToken cancellationToken = default)
    {
        if (lease.TaskId != identity.TaskId || lease.BackupAttemptId != identity.AttemptId)
            return new(BackupExecutionContractCode.Conflict);
        var saved = await ReadAsync(identity, cancellationToken);
        return saved.Value is null ? saved : new(BackupExecutionContractCode.StageNotOpen);
    }

    public async Task<BackupExecutionContractResult<FrozenBackupExecutionOperation>> ReadAsync(
        ReserveBackupExecutionOperation identity, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var operation = await context.BackupPlanExecutionOperations.SingleOrDefaultAsync(x => x.Id == identity.MutationId, cancellationToken);
        if (operation is null) return new(BackupExecutionContractCode.NotFound);
        if (Identity(operation) != identity) return new(BackupExecutionContractCode.Conflict);
        return new(BackupExecutionContractCode.AlreadyApplied, await DetachAsync(context, operation, cancellationToken));
    }

    private async Task<BackupExecutionContractResult<FrozenBackupExecutionOperation>> MutateAsync(
        LeaseHandle lease, ReserveBackupExecutionOperation identity, FreezeBackupExecutionOperation? frozen,
        bool abandon, CancellationToken token)
    {
        // 构造领域身份与载荷，先拒绝无效输入，不创建半份回执。
        var candidate = new BackupPlanExecutionOperation(identity.MutationId, identity.TaskId, identity.AttemptId,
            identity.Kind, identity.Sequence, identity.IntendedBackupSetId, identity.ObservedAtUtc);
        frozen?.Facts.Validate();
        if (frozen is not null && (frozen.Observations.Select(x => x.EntryNumber).Distinct().Count() != frozen.Observations.Count))
            return new(BackupExecutionContractCode.Conflict);
        try
        {
            await using var strategyContext = await factory.CreateDbContextAsync(token);
            return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var context = await factory.CreateDbContextAsync(token);
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
                var task = await context.BackupTasks.FromSqlInterpolated(
                    $"SELECT * FROM [BackupTasks] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {identity.TaskId}").AsTracking().SingleOrDefaultAsync(token);
                var operation = await context.BackupPlanExecutionOperations.AsTracking().SingleOrDefaultAsync(x => x.Id == identity.MutationId, token);
                if (operation is not null)
                {
                    if (Identity(operation) != identity) return new BackupExecutionContractResult<FrozenBackupExecutionOperation>(BackupExecutionContractCode.Conflict);
                    var saved = await DetachAsync(context, operation, token);
                    if (frozen is not null && operation.State is BackupExecutionOperationState.Frozen or BackupExecutionOperationState.Applied)
                        return Matches(saved, frozen)
                            ? new(BackupExecutionContractCode.AlreadyApplied, saved) : new(BackupExecutionContractCode.Conflict);
                    if (frozen is null && (!abandon || operation.State == BackupExecutionOperationState.Abandoned))
                        return new(BackupExecutionContractCode.AlreadyApplied, saved);
                }
                if (task is null) return new BackupExecutionContractResult<FrozenBackupExecutionOperation>(BackupExecutionContractCode.NotFound);
                if (lease.TaskId != identity.TaskId || lease.BackupAttemptId != identity.AttemptId
                    || BackupTaskPersistence.ValidateLease(task, lease, clock.GetUtcNow()) is not null)
                    return new BackupExecutionContractResult<FrozenBackupExecutionOperation>(BackupExecutionContractCode.LeaseLost);
                if (!await context.BackupAttempts.AnyAsync(x => x.Id == identity.AttemptId && x.TaskId == identity.TaskId, token))
                    return new BackupExecutionContractResult<FrozenBackupExecutionOperation>(BackupExecutionContractCode.Conflict);
                if (operation is null)
                {
                    if (frozen is not null || abandon) return new BackupExecutionContractResult<FrozenBackupExecutionOperation>(BackupExecutionContractCode.NotFound);
                    operation = candidate;
                    context.BackupPlanExecutionOperations.Add(operation);
                }
                else if (abandon)
                {
                    if (operation.State != BackupExecutionOperationState.Reserved
                        || operation.Kind is BackupExecutionOperationKind.SqlResult or BackupExecutionOperationKind.Transfer or BackupExecutionOperationKind.ValidateCopy)
                        return new BackupExecutionContractResult<FrozenBackupExecutionOperation>(BackupExecutionContractCode.Conflict);
                    operation.Abandon();
                }
                else if (frozen is not null)
                {
                    if (operation.State != BackupExecutionOperationState.Reserved)
                        return new BackupExecutionContractResult<FrozenBackupExecutionOperation>(BackupExecutionContractCode.Conflict);
                    if (operation.Kind == BackupExecutionOperationKind.Admission)
                    {
                        var facts = frozen.Facts;
                        if (facts.Metadata.DatabaseGuid.Value is not { } databaseGuid
                            || facts.Metadata.FamilyGuid.Value is not { } familyGuid)
                            return new BackupExecutionContractResult<FrozenBackupExecutionOperation>(BackupExecutionContractCode.Conflict);
                        var attempt = await context.BackupAttempts.AsTracking().SingleAsync(x => x.Id == identity.AttemptId, token);
                        if (attempt.BackupInvocationStatus != BackupInvocationStatus.Prepared
                            || attempt.ExpectedDatabaseGuid is { } expectedDatabase && expectedDatabase != databaseGuid
                            || attempt.ExpectedFamilyGuid is { } expectedFamily && expectedFamily != familyGuid)
                            return new BackupExecutionContractResult<FrozenBackupExecutionOperation>(BackupExecutionContractCode.Conflict);
                        attempt.BindSqlIdentity(databaseGuid, familyGuid);
                        if (facts.ActualBaseBackupSetId is { } baseId)
                        {
                            var snapshot = await context.BackupTaskSnapshots.SingleAsync(x => x.TaskId == identity.TaskId, token);
                            if (snapshot.Purpose != Domain.BackupPlans.BackupRunPurpose.PlanDifferential
                                || facts.Metadata.RecoveryForkId.Value is not { } fork || facts.EvidenceAtUtc is not { } observed
                                || !await context.BackupSets.AnyAsync(x => x.Id == baseId && x.DatabaseId == snapshot.DatabaseId, token)
                                || attempt.AdmittedFullBackupSetId is { } previous && (previous != baseId
                                    || attempt.AdmissionRecoveryForkId != fork || attempt.AdmissionObservedAtUtc != observed))
                                return new BackupExecutionContractResult<FrozenBackupExecutionOperation>(BackupExecutionContractCode.Conflict);
                            attempt.AdmitDifferential(baseId, fork, observed);
                        }
                    }
                    operation.Freeze(frozen.Facts);
                    foreach (var observation in frozen.Observations)
                        context.BackupPlanExecutionObservations.Add(new(Guid.NewGuid(), operation.Id, observation.EntryNumber,
                            observation.Source, observation.Kind, observation.Facts));
                }
                await context.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return new BackupExecutionContractResult<FrozenBackupExecutionOperation>(BackupExecutionContractCode.Succeeded,
                    await DetachAsync(context, operation, token));
            });
        }
        catch (DbUpdateException)
        {
            // 唯一键竞争后的补查执行与正常路径完全相同的身份/载荷比较。
            var replay = await ReadAsync(identity, token);
            if (replay.Value is { } saved
                && (frozen is null ? !abandon || saved.State == BackupExecutionOperationState.Abandoned
                    : saved.State is BackupExecutionOperationState.Frozen or BackupExecutionOperationState.Applied && Matches(saved, frozen))) return replay;
            return new(BackupExecutionContractCode.Conflict);
        }
    }

    internal static ReserveBackupExecutionOperation Identity(BackupPlanExecutionOperation operation) =>
        new(operation.Id, operation.TaskId, operation.AttemptId, operation.Kind, operation.Sequence,
            operation.IntendedBackupSetId, operation.ObservedAtUtc);
    internal static bool Matches(FrozenBackupExecutionOperation saved, FreezeBackupExecutionOperation incoming) =>
        saved.Identity == incoming.Identity && saved.Facts == incoming.Facts
        && saved.Observations.OrderBy(x => x.EntryNumber).SequenceEqual(incoming.Observations.OrderBy(x => x.EntryNumber));
    internal static async Task<FrozenBackupExecutionOperation> DetachAsync(PlatformDbContext context,
        BackupPlanExecutionOperation operation, CancellationToken token) =>
        new(Identity(operation), operation.State, operation.Facts,
            await context.BackupPlanExecutionObservations.Where(x => x.OperationId == operation.Id).OrderBy(x => x.EntryNumber)
                .Select(x => new BackupExecutionObservationInput(x.EntryNumber, x.Source, x.Kind, x.Facts)).ToArrayAsync(token));
}
