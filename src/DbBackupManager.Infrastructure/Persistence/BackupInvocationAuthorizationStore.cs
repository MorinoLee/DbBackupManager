using System.Data;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupInvocationAuthorizationStore(IDbContextFactory<PlatformDbContext> factory, TimeProvider clock)
    : IBackupInvocationAuthorizationStore
{
    public async Task<BackupExecutionContractResult<BackupInvocationAuthorizationModel>> AuthorizeAsync(
        LeaseHandle lease, AuthorizeBackupInvocation command, CancellationToken cancellationToken = default)
    {
        command.Binding.Validate();
        try
        {
            await using var strategyContext = await factory.CreateDbContextAsync(cancellationToken);
            return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var context = await factory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                var result = await AuthorizeCoreAsync(context, lease, command, clock.GetUtcNow(), cancellationToken);
                if (result.Code == BackupExecutionContractCode.Succeeded)
                {
                    await context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                return result;
            });
        }
        catch (DbUpdateException)
        {
            var saved = await ReadAsync(command.PermitId, cancellationToken);
            return saved.Value is { } value && Matches(value, lease, command)
                ? saved : new(BackupExecutionContractCode.Conflict);
        }
    }

    internal static async Task LockDatabaseAsync(PlatformDbContext context, Guid databaseId, CancellationToken token)
    {
        _ = await context.ManagedDatabases.FromSqlInterpolated(
            $"SELECT * FROM [ManagedDatabases] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {databaseId}").SingleAsync(token);
    }

    // 调用者先持有数据库协调锁；与旧恢复结果原子保存，不能把恢复后的状态当成调用终止证据。
    internal static async Task PreserveLegacyBlockAsync(PlatformDbContext context, Guid databaseId,
        BackupAttempt attempt, CancellationToken token)
    {
        if (attempt.BackupInvocationStatus is not (BackupInvocationStatus.Running or BackupInvocationStatus.Indeterminate)
            || await context.BackupInvocationAuthorizations.AnyAsync(x => x.TaskId == attempt.TaskId && x.AttemptId == attempt.Id, token))
            return;
        var startedAt = attempt.BackupStartedAtUtc ?? throw new InvalidOperationException("遗留调用缺少开始事实。");
        if (!await context.BackupPlanExecutionOperations.AnyAsync(x => x.Id == attempt.Id, token))
            context.BackupPlanExecutionOperations.Add(new(attempt.Id, attempt.TaskId, attempt.Id,
                BackupExecutionOperationKind.SqlResult, 1, attempt.Id, startedAt));
        // Unknown 的标识只绑定此遗留 Attempt；不是原会话身份，也不返回可消费的调用权。
        context.BackupInvocationAuthorizations.Add(new(attempt.Id, databaseId, attempt.TaskId, attempt.Id,
            attempt.Id, attempt.Id, startedAt, new() { CallerIncarnationId = attempt.Id }));
    }

    internal static async Task<BackupExecutionContractResult<BackupInvocationAuthorizationModel>> AuthorizeCoreAsync(
        PlatformDbContext context, LeaseHandle lease, AuthorizeBackupInvocation command, DateTimeOffset now, CancellationToken token)
    {
        await LockDatabaseAsync(context, command.DatabaseId, token);
        var replay = await context.BackupInvocationAuthorizations.SingleOrDefaultAsync(
            x => x.Id == command.PermitId || x.MutationId == command.MutationId, token);
        if (replay is not null)
            return Matches(Model(replay), lease, command)
                ? new(BackupExecutionContractCode.AlreadyApplied, Model(replay)) : new(BackupExecutionContractCode.Conflict);
        var task = await context.BackupTasks.FromSqlInterpolated(
            $"SELECT * FROM [BackupTasks] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {lease.TaskId}").AsTracking().SingleOrDefaultAsync(token);
        if (task is null) return new(BackupExecutionContractCode.NotFound);
        if (lease.Purpose != BackupLeasePurpose.Execution || lease.Stage != BackupTaskStage.Backup
            || BackupTaskPersistence.ValidateLease(task, lease, now) is not null) return new(BackupExecutionContractCode.LeaseLost);
        var snapshot = await context.BackupTaskSnapshots.SingleAsync(x => x.TaskId == task.Id, token);
        if (snapshot.DatabaseId != command.DatabaseId) return new(BackupExecutionContractCode.Conflict);
        var attempt = await context.BackupAttempts.AsTracking().SingleOrDefaultAsync(x => x.TaskId == task.Id && x.Id == lease.BackupAttemptId, token);
        if (attempt is null || attempt.BackupInvocationStatus != BackupInvocationStatus.Prepared
            || !attempt.RowVersion.SequenceEqual(command.AttemptRowVersion)) return new(BackupExecutionContractCode.Conflict);
        var blocked = await context.BackupInvocationAuthorizations.AnyAsync(x => x.DatabaseId == command.DatabaseId && x.TerminalObservedAtUtc == null, token)
            || await (from prior in context.BackupAttempts
                      join priorSnapshot in context.BackupTaskSnapshots on prior.TaskId equals priorSnapshot.TaskId
                      where priorSnapshot.DatabaseId == command.DatabaseId
                          && (prior.BackupInvocationStatus == BackupInvocationStatus.Running || prior.BackupInvocationStatus == BackupInvocationStatus.Indeterminate)
                          && !context.BackupInvocationAuthorizations.Any(x => x.TaskId == prior.TaskId && x.AttemptId == prior.Id)
                      select prior.Id).AnyAsync(token);
        if (blocked) return new(BackupExecutionContractCode.DatabaseBlocked);
        if (task.PlanId is not null && (attempt.ExpectedDatabaseGuid is null || attempt.ExpectedFamilyGuid is null
            || snapshot.BackupType == Domain.Configuration.BackupType.Differential
                && (attempt.AdmittedFullBackupSetId is null || attempt.AdmissionRecoveryForkId is null || attempt.AdmissionObservedAtUtc is null)))
            return new(BackupExecutionContractCode.Conflict);
        var operation = await context.BackupPlanExecutionOperations.SingleOrDefaultAsync(x => x.Id == command.SqlOperationId, token);
        if (operation is null)
        {
            if (task.PlanId is not null) return new(BackupExecutionContractCode.Conflict);
            context.BackupPlanExecutionOperations.Add(new(command.SqlOperationId, task.Id, attempt.Id,
                BackupExecutionOperationKind.SqlResult, 1, command.SqlOperationId, command.GrantedAtUtc));
        }
        else if (operation.TaskId != task.Id || operation.AttemptId != attempt.Id
            || operation.Kind != BackupExecutionOperationKind.SqlResult || operation.State != BackupExecutionOperationState.Reserved)
            return new(BackupExecutionContractCode.Conflict);
        var authorization = new BackupInvocationAuthorization(command.PermitId, command.DatabaseId, task.Id, attempt.Id,
            command.MutationId, command.SqlOperationId, command.GrantedAtUtc, command.Binding);
        context.BackupInvocationAuthorizations.Add(authorization);
        attempt.MarkBackupRunning(command.GrantedAtUtc);
        return new(BackupExecutionContractCode.Succeeded, Model(authorization) with { Lease = task.ToLeaseHandle() });
    }

    public async Task<BackupExecutionContractResult<BackupInvocationAuthorizationModel>> ReadAsync(
        Guid permitId, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var saved = await context.BackupInvocationAuthorizations.SingleOrDefaultAsync(x => x.Id == permitId, cancellationToken);
        return saved is null ? new(BackupExecutionContractCode.NotFound) : new(BackupExecutionContractCode.AlreadyApplied, Model(saved));
    }

    public async Task<BackupExecutionContractResult<BackupInvocationAuthorizationModel>> RecordTerminationAsync(
        LeaseHandle lease, TerminateBackupInvocation command, CancellationToken cancellationToken = default)
    {
        var saved = await ReadAsync(command.PermitId, cancellationToken);
        if (saved.Value is null) return new(BackupExecutionContractCode.NotFound);
        await using var strategyContext = await factory.CreateDbContextAsync(cancellationToken);
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await LockDatabaseAsync(context, saved.Value.DatabaseId, cancellationToken);
            var authorization = await context.BackupInvocationAuthorizations.AsTracking().SingleOrDefaultAsync(x => x.Id == command.PermitId, cancellationToken);
            if (authorization is null) return new BackupExecutionContractResult<BackupInvocationAuthorizationModel>(BackupExecutionContractCode.NotFound);
            if (authorization.TaskId != lease.TaskId || authorization.AttemptId != lease.BackupAttemptId)
                return new BackupExecutionContractResult<BackupInvocationAuthorizationModel>(BackupExecutionContractCode.Conflict);
            if (authorization.TerminalObservedAtUtc is not null)
                return TerminationMatches(authorization, command)
                    ? new(BackupExecutionContractCode.AlreadyApplied, Model(authorization)) : new(BackupExecutionContractCode.Conflict);
            var task = await context.BackupTasks.FromSqlInterpolated(
                $"SELECT * FROM [BackupTasks] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {lease.TaskId}").AsTracking().SingleAsync(cancellationToken);
            if (BackupTaskPersistence.ValidateLease(task, lease, clock.GetUtcNow()) is not null)
                return new BackupExecutionContractResult<BackupInvocationAuthorizationModel>(BackupExecutionContractCode.LeaseLost);
            var evidence = await context.BackupPlanExecutionOperations.SingleOrDefaultAsync(x => x.Id == command.EvidenceId, cancellationToken);
            if (evidence is null || evidence.TaskId != authorization.TaskId || evidence.AttemptId != authorization.AttemptId
                || evidence.State is not (BackupExecutionOperationState.Frozen or BackupExecutionOperationState.Applied)
                || evidence.Facts.EvidenceAtUtc != command.ObservedAtUtc
                || !evidence.Facts.OriginalCallTerminated || !evidence.Facts.OriginalCallerCannotInvoke
                || !command.OriginalCallTerminated || !command.OriginalCallerCannotInvoke
                || command.Kind != BackupInvocationTerminationKind.RecoveredTerminated && evidence.Id != authorization.SqlOperationId
                || command.Kind == BackupInvocationTerminationKind.PlatformCompleted
                    && (evidence.Kind != BackupExecutionOperationKind.SqlResult || evidence.Facts.SqlOutcomeSource != BackupSqlOutcomeSource.PlatformResponse
                        || evidence.Facts.Outcome != BackupExecutionOutcome.Succeeded || !evidence.Facts.SqlSuccessObserved)
                || command.Kind == BackupInvocationTerminationKind.PlatformConfirmedFailed
                    && (evidence.Kind != BackupExecutionOperationKind.SqlResult || evidence.Facts.SqlOutcomeSource is not (BackupSqlOutcomeSource.PlatformResponse or BackupSqlOutcomeSource.NotInvoked)
                        || evidence.Facts.SqlSuccessObserved
                        || evidence.Facts.Outcome != BackupExecutionOutcome.ConfirmedFailed
                            && !(evidence.Facts.Outcome == BackupExecutionOutcome.Cancelled && evidence.Facts.SqlOutcomeSource == BackupSqlOutcomeSource.NotInvoked))
                || command.Kind == BackupInvocationTerminationKind.RecoveredTerminated
                    && (evidence.Kind != BackupExecutionOperationKind.Recovery || !evidence.Facts.SqlSuccessObserved
                        || evidence.Facts.Metadata.BackupSetGuid.State != Domain.BackupSets.BackupMetadataState.Known))
                return new BackupExecutionContractResult<BackupInvocationAuthorizationModel>(BackupExecutionContractCode.Conflict);
            if (command.Kind == BackupInvocationTerminationKind.RecoveredTerminated)
            {
                var original = await context.BackupAttempts.SingleAsync(x => x.Id == authorization.AttemptId, cancellationToken);
                var observations = await context.BackupPlanExecutionObservations.Where(x => x.OperationId == evidence.Id).ToArrayAsync(cancellationToken);
                var header = observations.Where(x => x.Source == BackupExecutionObservationSource.BackupHeader).ToArray();
                var history = observations.Where(x => x.Source == BackupExecutionObservationSource.Msdb).ToArray();
                if (evidence.Facts.SqlOutcomeSource != BackupSqlOutcomeSource.RecoveredEvidence
                    || original.ExpectedDatabaseGuid is null || original.ExpectedFamilyGuid is null
                    || evidence.Facts.Metadata.DatabaseGuid.Value != original.ExpectedDatabaseGuid
                    || evidence.Facts.Metadata.FamilyGuid.Value != original.ExpectedFamilyGuid
                    || header.Length != 1 || history.Length != 1
                    || header[0].Facts.Metadata.BackupSetGuid != evidence.Facts.Metadata.BackupSetGuid
                    || history[0].Facts.Metadata.BackupSetGuid != evidence.Facts.Metadata.BackupSetGuid
                    || header[0].Facts.Metadata.DatabaseGuid != evidence.Facts.Metadata.DatabaseGuid
                    || history[0].Facts.Metadata.DatabaseGuid != evidence.Facts.Metadata.DatabaseGuid
                    || header[0].Facts.Metadata.FamilyGuid != evidence.Facts.Metadata.FamilyGuid
                    || history[0].Facts.Metadata.FamilyGuid != evidence.Facts.Metadata.FamilyGuid
                    || !observations.Any(x => x.Source == BackupExecutionObservationSource.Termination
                        && x.Facts.OriginalCallTerminated && x.Facts.OriginalCallerCannotInvoke))
                    return new BackupExecutionContractResult<BackupInvocationAuthorizationModel>(BackupExecutionContractCode.Conflict);
            }
            try
            {
                authorization.RecordTermination(command.MutationId, command.EvidenceId, command.Kind,
                    command.ObservedAtUtc, command.OriginalCallTerminated, command.OriginalCallerCannotInvoke);
            }
            catch (ArgumentException) { return new BackupExecutionContractResult<BackupInvocationAuthorizationModel>(BackupExecutionContractCode.Conflict); }
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new BackupExecutionContractResult<BackupInvocationAuthorizationModel>(BackupExecutionContractCode.Succeeded, Model(authorization));
        });
    }

    internal static bool Matches(BackupInvocationAuthorizationModel saved, LeaseHandle lease, AuthorizeBackupInvocation command) =>
        saved.PermitId == command.PermitId && saved.TaskId == lease.TaskId && saved.AttemptId == lease.BackupAttemptId
        && saved.DatabaseId == command.DatabaseId && saved.MutationId == command.MutationId && saved.SqlOperationId == command.SqlOperationId
        && saved.GrantedAtUtc == command.GrantedAtUtc && saved.Binding == command.Binding;
    private static bool TerminationMatches(BackupInvocationAuthorization saved, TerminateBackupInvocation command) =>
        saved.TerminationMutationId == command.MutationId && saved.TerminationEvidenceId == command.EvidenceId
        && saved.TerminationKind == command.Kind && saved.TerminalObservedAtUtc == command.ObservedAtUtc
        && command.OriginalCallTerminated && command.OriginalCallerCannotInvoke;
    internal static BackupInvocationAuthorizationModel Model(BackupInvocationAuthorization saved) =>
        new(saved.Id, saved.TaskId, saved.AttemptId, saved.DatabaseId, saved.MutationId, saved.SqlOperationId,
            saved.GrantedAtUtc, saved.Binding, saved.TerminalObservedAtUtc, saved.TerminationKind, saved.TerminationEvidenceId, saved.TerminationMutationId);
}
