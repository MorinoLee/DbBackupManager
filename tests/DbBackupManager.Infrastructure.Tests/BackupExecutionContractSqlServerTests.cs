using System.Data.Common;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupExecutionContractSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ClearBackupTaskDataAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentCoordinatorsGrantOnlyOneDatabaseCall(bool includePlan)
    {
        var first = await BackupSetTestData.WorkAsync(database);
        await using var db = database.CreateContext();
        var policy = (await db.BackupTasks.SingleAsync(x => x.Id == first.Task.TaskId)).PolicyId!.Value;
        var second = includePlan ? await ExecutionContractTestData.PlanAsync(database, first)
            : await BackupSetTestData.WorkAsync(database, policy);
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var left = new BackupInvocationAuthorizationStore(factory, TimeProvider.System);
        var right = new BackupInvocationAuthorizationStore(factory, TimeProvider.System);
        var a = await ExecutionContractTestData.AuthorizationAsync(database, first);
        var b = await ExecutionContractTestData.AuthorizationAsync(database, second);
        var results = await Task.WhenAll(left.AuthorizeAsync(first.Lease, a), right.AuthorizeAsync(second.Lease, b));
        Assert.Single(results, x => x.Code == BackupExecutionContractCode.Succeeded);
        Assert.Single(results, x => x.Code == BackupExecutionContractCode.DatabaseBlocked);
        Assert.Equal(1, await db.BackupInvocationAuthorizations.CountAsync(x => x.TerminalObservedAtUtc == null));
        Assert.Equal(1, await db.BackupAttempts.CountAsync(x => x.BackupInvocationStatus == BackupInvocationStatus.Running));
    }

    [Fact]
    public async Task LostGrantResponseAndRestartNeverReturnAnotherInvocationCapability()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var command = await ExecutionContractTestData.AuthorizationAsync(database, work);
        var failing = new BackupInvocationAuthorizationStore(new Factory(database, new CommitFailure()), TimeProvider.System);
        await Assert.ThrowsAsync<IOException>(() => failing.AuthorizeAsync(work.Lease, command));
        var restarted = new BackupInvocationAuthorizationStore(new Factory(database), TimeProvider.System);
        var replay = await restarted.AuthorizeAsync(work.Lease, command);
        Assert.Equal(BackupExecutionContractCode.AlreadyApplied, replay.Code);
        Assert.Null(replay.Value!.Lease);
        Assert.Equal(BackupExecutionContractCode.Conflict,
            (await restarted.AuthorizeAsync(work.Lease, command with { Binding = command.Binding with { CallerIncarnationId = Guid.NewGuid() } })).Code);
        await using var db = database.CreateContext();
        var policy = (await db.BackupTasks.SingleAsync(x => x.Id == work.Task.TaskId)).PolicyId!.Value;
        var next = await BackupSetTestData.WorkAsync(database, policy);
        Assert.Equal(BackupExecutionContractCode.DatabaseBlocked,
            (await restarted.AuthorizeAsync(next.Lease, await ExecutionContractTestData.AuthorizationAsync(database, next))).Code);
        Assert.Equal(1, await db.BackupInvocationAuthorizations.CountAsync());
    }

    [Fact]
    public async Task LegacyAuthorizationReplayRejectsChangedIdentityAndNeverGrantsAnotherCall()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var store = new BackupTaskExecutionStore(new Factory(database));
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(BackupTaskStoreResultCode.Succeeded,
            (await store.MarkBackupInvocationStartedAsync(work.Lease, work.Attempt.RowVersion, now)).Code);
        var replay = await store.MarkBackupInvocationStartedAsync(work.Lease, work.Attempt.RowVersion, now);
        Assert.Equal(BackupTaskStoreResultCode.AlreadyApplied, replay.Code);
        Assert.Null(replay.Value);
        Assert.Equal(BackupTaskStoreResultCode.ConcurrencyConflict,
            (await store.MarkBackupInvocationStartedAsync(work.Lease, work.Attempt.RowVersion, now.AddSeconds(1))).Code);
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.BackupInvocationAuthorizations.CountAsync());
    }

    [Fact]
    public async Task ContradictoryFailureAndSuccessFactsCannotTerminateAuthorization()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var factory = new Factory(database);
        var coordinator = new BackupInvocationAuthorizationStore(factory, TimeProvider.System);
        var command = await ExecutionContractTestData.AuthorizationAsync(database, work);
        var grant = await coordinator.AuthorizeAsync(work.Lease, command);
        var operations = new BackupExecutionOperationStore(factory, TimeProvider.System);
        var identity = new ReserveBackupExecutionOperation(command.SqlOperationId, work.Task.TaskId, work.Attempt.Id,
            BackupExecutionOperationKind.SqlResult, 1, command.SqlOperationId, command.GrantedAtUtc);
        var now = DateTimeOffset.UtcNow;
        await operations.FreezeAsync(grant.Value!.Lease!, new(identity, new()
        {
            Outcome = BackupExecutionOutcome.ConfirmedFailed,
            SqlOutcomeSource = BackupSqlOutcomeSource.PlatformResponse,
            SqlSuccessObserved = true,
            OriginalCallTerminated = true,
            OriginalCallerCannotInvoke = true,
            EvidenceAtUtc = now
        }, []));
        Assert.Equal(BackupExecutionContractCode.Conflict, (await coordinator.RecordTerminationAsync(grant.Value.Lease!,
            new(command.PermitId, Guid.NewGuid(), identity.MutationId, BackupInvocationTerminationKind.PlatformConfirmedFailed,
                now, true, true))).Code);
        Assert.Null((await coordinator.ReadAsync(command.PermitId)).Value!.TerminalObservedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnresolvedHistoricalCallBlocksAfterLeaseExpiryAndFinalTaskFailure(bool hasAuthorization)
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var coordinator = new BackupInvocationAuthorizationStore(new Factory(database), TimeProvider.System);
        if (hasAuthorization)
            Assert.Equal(BackupExecutionContractCode.Succeeded,
                (await coordinator.AuthorizeAsync(work.Lease, await ExecutionContractTestData.AuthorizationAsync(database, work))).Code);
        await using var db = database.CreateContext();
        if (!hasAuthorization)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [BackupAttempts] SET [BackupInvocationStatus]='Running', [BackupStartedAtUtc]={DateTimeOffset.UtcNow} WHERE [Id]={work.Attempt.Id}");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [BackupTasks] SET [LeaseAcquiredAtUtc]={DateTimeOffset.UtcNow.AddMinutes(-20)},[LeaseExpiresAtUtc]={DateTimeOffset.UtcNow.AddMinutes(-1)} WHERE [Id]={work.Task.TaskId}");
        // 旧调用已失去租约，任务即使明确终结也不能证明原 SQL 会话已终止。
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [BackupTasks] SET [Status]='Failed',[CompletedAtUtc]={DateTimeOffset.UtcNow},[ErrorCode]='synthetic_failure',[ErrorMessage]=N'合成失败',[LeasePurpose]=NULL,[LeaseToken]=NULL,[LeaseOwner]=NULL,[LeaseAcquiredAtUtc]=NULL,[LeaseExpiresAtUtc]=NULL WHERE [Id]={work.Task.TaskId}");
        var policy = (await db.BackupTasks.SingleAsync(x => x.Id == work.Task.TaskId)).PolicyId!.Value;
        var next = await BackupSetTestData.WorkAsync(database, policy);
        Assert.Equal(BackupExecutionContractCode.DatabaseBlocked,
            (await coordinator.AuthorizeAsync(next.Lease, await ExecutionContractTestData.AuthorizationAsync(database, next))).Code);
    }

    [Fact]
    public async Task FrozenReceiptPreservesFullContentAndLsnAndRejectsChangedReplay()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var store = new BackupExecutionOperationStore(new Factory(database), TimeProvider.System);
        var identity = ExecutionContractTestData.Identity(work);
        Assert.Equal(BackupExecutionContractCode.Succeeded, (await store.ReserveAsync(work.Lease, identity)).Code);
        var metadata = BackupSetTestData.Full();
        var facts = new BackupExecutionFacts
        {
            Metadata = metadata,
            Content = new() { State = BackupContentState.Verified, DigestAlgorithm = "SHA256", DigestHex = new string('a', 62) + "01", LengthBytes = 8192, Protection = BackupObjectProtection.GuardedUntilCommit }
        };
        var command = new FreezeBackupExecutionOperation(identity, facts,
            [new(0, BackupExecutionObservationSource.BackupHeader, BackupSetEvidenceKind.Backup, facts),
             new(1, BackupExecutionObservationSource.Msdb, BackupSetEvidenceKind.Backup, facts),
             new(2, BackupExecutionObservationSource.RemoteFinal, BackupSetEvidenceKind.Backup,
                 facts with { Content = facts.Content with { DigestHex = new string('a', 62) + "02" } })]);
        var frozen = await store.FreezeAsync(work.Lease, command);
        Assert.Equal(BackupExecutionContractCode.Succeeded, frozen.Code);
        Assert.Equal(command.Facts, frozen.Value!.Facts);
        Assert.Equal(BackupExecutionContractCode.StageNotOpen, (await store.ApplyFrozenAsync(work.Lease, identity)).Code);
        Assert.Equal(BackupExecutionContractCode.AlreadyApplied, (await store.FreezeAsync(work.Lease, command)).Code);
        Assert.Equal(BackupExecutionContractCode.Conflict,
            (await store.FreezeAsync(work.Lease, command with { Facts = facts with { Content = facts.Content with { DigestHex = new string('a', 62) + "02" } } })).Code);
        Assert.Equal(BackupExecutionContractCode.Conflict,
            (await store.FreezeAsync(work.Lease, command with { Observations = [command.Observations[0]] })).Code);
        Assert.Equal(BackupExecutionContractCode.Conflict, (await store.ReserveAsync(work.Lease, identity with { IntendedBackupSetId = Guid.NewGuid() })).Code);
        await using var db = database.CreateContext();
        Assert.Equal(3, await db.BackupPlanExecutionObservations.CountAsync());
        var distinctContent = await db.BackupPlanExecutionObservations.OrderBy(x => x.EntryNumber).Select(x => x.Facts.Content).ToArrayAsync();
        Assert.Equal(new string('a', 62) + "01", distinctContent[0].DigestHex);
        Assert.Equal(new string('a', 62) + "02", distinctContent[2].DigestHex);
        Assert.Equal("SHA256", distinctContent[2].DigestAlgorithm);
        var saved = await db.BackupPlanExecutionOperations.SingleAsync();
        Assert.Equal(new BackupLsn(1234567890123456789012345m), saved.Facts.Metadata.FirstLsn.Value);
        Assert.Equal(new BackupLsn(1234567890123456789012346m), saved.Facts.Metadata.LastLsn.Value);
        Assert.Equal(DateTimeKind.Unspecified, saved.Facts.Metadata.SqlFinishedLocal.Value!.Value.Kind);
        var columns = await db.Database.SqlQueryRaw<string>("SELECT TYPE_NAME(user_type_id) AS [Value] FROM sys.columns WHERE object_id=OBJECT_ID('BackupPlanExecutionOperations') AND name='ContentDigest'").SingleAsync();
        Assert.Equal("binary", columns);
        db.Entry(saved).ComplexProperty(x => x.Facts).ComplexProperty(x => x.Content).Property(x => x.LengthBytes).CurrentValue = 8193;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task FreezeRollbackAndLostResponseHaveNoPartialOrDuplicateObservations()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var normal = new BackupExecutionOperationStore(new Factory(database), TimeProvider.System);
        var identity = ExecutionContractTestData.Identity(work);
        await normal.ReserveAsync(work.Lease, identity);
        var facts = new BackupExecutionFacts();
        var command = new FreezeBackupExecutionOperation(identity, facts,
            [new(0, BackupExecutionObservationSource.Comparison, BackupSetEvidenceKind.Backup, facts)]);
        var failure = new BackupExecutionOperationStore(new Factory(database, new SavingFailure()), TimeProvider.System);
        await Assert.ThrowsAsync<IOException>(() => failure.FreezeAsync(work.Lease, command));
        await using var db = database.CreateContext();
        Assert.Equal(BackupExecutionOperationState.Reserved, (await db.BackupPlanExecutionOperations.SingleAsync()).State);
        Assert.Equal(0, await db.BackupPlanExecutionObservations.CountAsync());
        var lostResponse = new BackupExecutionOperationStore(new Factory(database, new CommitFailure()), TimeProvider.System);
        await Assert.ThrowsAsync<IOException>(() => lostResponse.FreezeAsync(work.Lease, command));
        Assert.Equal(BackupExecutionContractCode.AlreadyApplied, (await normal.FreezeAsync(work.Lease, command)).Code);
        Assert.Equal(1, await db.BackupPlanExecutionObservations.CountAsync());
        Assert.Equal(BackupExecutionContractCode.LeaseLost,
            (await normal.ReserveAsync(new(work.Lease.TaskId, Guid.NewGuid(), work.Lease.Purpose, work.Lease.Stage, work.Lease.BackupAttemptId, work.Lease.ExpiresAtUtc, work.Lease.RowVersion), identity with { MutationId = Guid.NewGuid(), Sequence = 2 })).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrozenTerminalProofReleasesAuthorizationWithStrictReplay(bool succeeded)
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var factory = new Factory(database);
        var coordinator = new BackupInvocationAuthorizationStore(factory, TimeProvider.System);
        var command = await ExecutionContractTestData.AuthorizationAsync(database, work);
        var grant = await coordinator.AuthorizeAsync(work.Lease, command);
        var store = new BackupExecutionOperationStore(factory, TimeProvider.System);
        var identity = (await store.ReadAsync(new(command.SqlOperationId, work.Task.TaskId, work.Attempt.Id,
            BackupExecutionOperationKind.SqlResult, 1, command.SqlOperationId, command.GrantedAtUtc))).Value!.Identity;
        var facts = new BackupExecutionFacts
        {
            Outcome = succeeded ? BackupExecutionOutcome.Succeeded : BackupExecutionOutcome.ConfirmedFailed,
            SqlOutcomeSource = BackupSqlOutcomeSource.PlatformResponse,
            SqlSuccessObserved = succeeded,
            EvidenceAtUtc = DateTimeOffset.UtcNow,
            OriginalCallTerminated = true,
            OriginalCallerCannotInvoke = true
        };
        await store.FreezeAsync(grant.Value!.Lease!, new(identity, facts, []));
        var terminal = new TerminateBackupInvocation(command.PermitId, Guid.NewGuid(), command.SqlOperationId,
            succeeded ? BackupInvocationTerminationKind.PlatformCompleted : BackupInvocationTerminationKind.PlatformConfirmedFailed,
            facts.EvidenceAtUtc!.Value, true, true);
        Assert.Equal(BackupExecutionContractCode.Conflict,
            (await coordinator.RecordTerminationAsync(grant.Value.Lease!, terminal with { OriginalCallerCannotInvoke = false })).Code);
        Assert.Equal(BackupExecutionContractCode.Succeeded, (await coordinator.RecordTerminationAsync(grant.Value.Lease!, terminal)).Code);
        Assert.Equal(BackupExecutionContractCode.AlreadyApplied, (await coordinator.RecordTerminationAsync(grant.Value.Lease!, terminal)).Code);
        Assert.Equal(BackupExecutionContractCode.Conflict, (await coordinator.RecordTerminationAsync(grant.Value.Lease!, terminal with { MutationId = Guid.NewGuid() })).Code);
        await using var db = database.CreateContext();
        var policy = (await db.BackupTasks.SingleAsync(x => x.Id == work.Task.TaskId)).PolicyId!.Value;
        var next = await BackupSetTestData.WorkAsync(database, policy);
        Assert.Equal(BackupExecutionContractCode.Succeeded,
            (await coordinator.AuthorizeAsync(next.Lease, await ExecutionContractTestData.AuthorizationAsync(database, next))).Code);
    }

    [Fact]
    public async Task AdmissionIdentityIsAtomicAndCannotBeReplacedOrFilledAfterInvocation()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var factory = new Factory(database);
        var store = new BackupExecutionOperationStore(factory, TimeProvider.System);
        var identity = ExecutionContractTestData.Identity(work) with { Kind = BackupExecutionOperationKind.Admission };
        await store.ReserveAsync(work.Lease, identity);
        var metadata = new BackupSetMetadata { DatabaseGuid = BackupMetadata.Known(Guid.NewGuid()), FamilyGuid = BackupMetadata.Known(Guid.NewGuid()) };
        var command = new FreezeBackupExecutionOperation(identity, new() { Metadata = metadata }, []);
        Assert.Equal(BackupExecutionContractCode.Succeeded, (await store.FreezeAsync(work.Lease, command)).Code);
        await using var db = database.CreateContext();
        var attempt = await db.BackupAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(metadata.DatabaseGuid.Value, attempt.ToAttemptModel().ExpectedDatabaseGuid);
        Assert.Equal(metadata.FamilyGuid.Value, attempt.ToAttemptModel().ExpectedFamilyGuid);
        var later = identity with { MutationId = Guid.NewGuid(), Sequence = 2 };
        await store.ReserveAsync(work.Lease, later);
        Assert.Equal(BackupExecutionContractCode.Conflict, (await store.FreezeAsync(work.Lease,
            command with { Identity = later, Facts = command.Facts with { Metadata = metadata with { DatabaseGuid = BackupMetadata.Known(Guid.NewGuid()) } } })).Code);
        var coordinator = new BackupInvocationAuthorizationStore(factory, TimeProvider.System);
        await coordinator.AuthorizeAsync(work.Lease, await ExecutionContractTestData.AuthorizationAsync(database, work));
        Assert.Equal(BackupExecutionContractCode.Conflict, (await store.FreezeAsync(work.Lease,
            command with { Identity = later })).Code);
        Assert.Equal(BackupExecutionContractCode.AlreadyApplied, (await store.FreezeAsync(work.Lease, command)).Code);
        Assert.Equal(metadata.DatabaseGuid.Value, (await db.BackupAttempts.AsNoTracking().SingleAsync()).ExpectedDatabaseGuid);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RecoveryTerminationNeedsOriginalBindingMatchingSourcesAndPositiveTerminalProof(bool knownSession, bool terminalObservation)
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var metadata = BackupSetTestData.Full();
        await using (var db = database.CreateContext())
        {
            var attempt = await db.BackupAttempts.AsTracking().SingleAsync();
            attempt.BindSqlIdentity(metadata.DatabaseGuid.Value!.Value, metadata.FamilyGuid.Value!.Value);
            await db.SaveChangesAsync();
        }
        var factory = new Factory(database);
        var coordinator = new BackupInvocationAuthorizationStore(factory, TimeProvider.System);
        var command = await ExecutionContractTestData.AuthorizationAsync(database, work);
        if (knownSession) command = command with
        {
            Binding = command.Binding with
            {
                State = BackupInvocationBindingState.Known,
                SessionId = 51,
                SessionEstablishedLocal = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Unspecified)
            }
        };
        var grant = await coordinator.AuthorizeAsync(work.Lease, command);
        var identity = ExecutionContractTestData.Identity(work) with { Kind = BackupExecutionOperationKind.Recovery };
        var store = new BackupExecutionOperationStore(factory, TimeProvider.System);
        await store.ReserveAsync(grant.Value!.Lease!, identity);
        var facts = new BackupExecutionFacts
        {
            Metadata = metadata,
            SqlSuccessObserved = true,
            Outcome = BackupExecutionOutcome.Succeeded,
            SqlOutcomeSource = BackupSqlOutcomeSource.RecoveredEvidence,
            EvidenceAtUtc = DateTimeOffset.UtcNow,
            OriginalCallTerminated = true,
            OriginalCallerCannotInvoke = true
        };
        List<BackupExecutionObservationInput> observations = [new(0, BackupExecutionObservationSource.BackupHeader, BackupSetEvidenceKind.Backup, facts),
            new(1, BackupExecutionObservationSource.Msdb, BackupSetEvidenceKind.Backup, facts)];
        if (terminalObservation) observations.Add(new(2, BackupExecutionObservationSource.Termination, BackupSetEvidenceKind.Backup, facts));
        await store.FreezeAsync(grant.Value.Lease!, new(identity, facts, observations));
        var result = await coordinator.RecordTerminationAsync(grant.Value.Lease!, new(command.PermitId, Guid.NewGuid(), identity.MutationId,
            BackupInvocationTerminationKind.RecoveredTerminated, facts.EvidenceAtUtc!.Value, true, true));
        Assert.Equal(knownSession && terminalObservation ? BackupExecutionContractCode.Succeeded : BackupExecutionContractCode.Conflict, result.Code);
        var saved = await coordinator.ReadAsync(command.PermitId);
        Assert.Equal(knownSession && terminalObservation, saved.Value!.TerminalObservedAtUtc is not null);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("receipt")]
    [InlineData("authorization")]
    [InlineData("binding")]
    public async Task ExecutionMigrationActuallyRollsDownAndUpAndRejectsDurableEvidence(string dataKind)
    {
        var temporary = new PlatformDatabaseSqlServerFixture();
        try
        {
            await temporary.InitializeAsync();
            await using var db = temporary.CreateContext();
            var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            var previous = migrations[^2];
            var migrator = db.GetService<IMigrator>();
            if (dataKind != "empty")
            {
                var work = await BackupSetTestData.WorkAsync(temporary);
                var store = new BackupExecutionOperationStore(new Factory(temporary), TimeProvider.System);
                if (dataKind == "receipt") await store.ReserveAsync(work.Lease, ExecutionContractTestData.Identity(work));
                if (dataKind == "authorization") await new BackupInvocationAuthorizationStore(new Factory(temporary), TimeProvider.System)
                    .AuthorizeAsync(work.Lease, await ExecutionContractTestData.AuthorizationAsync(temporary, work));
                if (dataKind == "binding")
                {
                    var attempt = await db.BackupAttempts.AsTracking().SingleAsync();
                    attempt.BindSqlIdentity(Guid.NewGuid(), Guid.NewGuid());
                    await db.SaveChangesAsync();
                }
                var error = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(previous));
                Assert.Equal(51004, error.Number);
                Assert.Equal(migrations, await db.Database.GetAppliedMigrationsAsync());
                Assert.Equal(3, await ContractTableCountAsync(db));
            }
            else
            {
                await migrator.MigrateAsync(previous);
                Assert.Equal(0, await ContractTableCountAsync(db));
                await migrator.MigrateAsync();
                Assert.Equal(3, await ContractTableCountAsync(db));
                var work = await BackupSetTestData.WorkAsync(temporary);
                Assert.Equal(BackupExecutionContractCode.Succeeded,
                    (await new BackupInvocationAuthorizationStore(new Factory(temporary), TimeProvider.System)
                        .AuthorizeAsync(work.Lease, await ExecutionContractTestData.AuthorizationAsync(temporary, work))).Code);
            }
        }
        finally { await temporary.DisposeAsync(); }
    }

    private static Task<int> ContractTableCountAsync(PlatformDbContext db) => db.Database.SqlQueryRaw<int>(
        "SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name IN ('BackupPlanExecutionOperations','BackupPlanExecutionObservations','BackupInvocationAuthorizations')").SingleAsync();
    internal sealed class Factory(PlatformDatabaseSqlServerFixture database, params IInterceptor[] interceptors)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => database.CreateContext(interceptors);
        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class CommitFailure : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) => throw new IOException("合成提交响应丢失");
    }
    private sealed class SavingFailure : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) => throw new IOException("合成保存失败");
    }
}

internal static class ExecutionContractTestData
{
    internal static ReserveBackupExecutionOperation Identity(BackupExecutionWorkItem work) => new(Guid.NewGuid(),
        work.Task.TaskId, work.Attempt.Id, BackupExecutionOperationKind.Metadata, 1, Guid.NewGuid(), DateTimeOffset.UtcNow);
    internal static async Task<AuthorizeBackupInvocation> AuthorizationAsync(PlatformDatabaseSqlServerFixture db, BackupExecutionWorkItem work)
    {
        await using var context = db.CreateContext();
        var attempt = await context.BackupAttempts.SingleAsync(x => x.Id == work.Attempt.Id);
        var id = Guid.NewGuid();
        var command = new AuthorizeBackupInvocation(id, id, id, work.Snapshot.Identity.DatabaseId,
            new() { CallerIncarnationId = Guid.NewGuid() }, DateTimeOffset.UtcNow, attempt.RowVersion);
        if (work.Snapshot.Purpose is not null)
        {
            var operation = new BackupPlanExecutionOperation(id, work.Task.TaskId, attempt.Id,
                BackupExecutionOperationKind.SqlResult, 1, id, command.GrantedAtUtc);
            context.Add(operation);
            await context.SaveChangesAsync();
        }
        return command;
    }
    internal static async Task<BackupExecutionWorkItem> PlanAsync(PlatformDatabaseSqlServerFixture database, BackupExecutionWorkItem legacy)
    {
        await using var db = database.CreateContext();
        var old = await db.BackupTaskSnapshots.SingleAsync(x => x.TaskId == legacy.Task.TaskId);
        var now = DateTimeOffset.UtcNow;
        var definition = new BackupPlanDefinition(BackupPlanMode.FullAndDifferential,
            new(BackupScheduleType.Daily, new(2, 0), BackupWeekdays.None),
            new(BackupScheduleType.Daily, new(3, 0), BackupWeekdays.None), null, "UTC", BackupStorageMode.LocalOnly,
            null, 14, null, true, false, 120, 60, 60);
        var plan = BackupPlan.Create(Guid.NewGuid(), old.DatabaseId, "合成执行计划", Guid.NewGuid(), definition, now);
        await new BackupPlanStore(new BackupExecutionContractSqlServerTests.Factory(database)).AddAsync(plan);
        var task = BackupTask.ForPlan(Guid.NewGuid(), plan.Id, plan.CurrentVersionId, BackupRunPurpose.PlanFull, BackupTaskTriggerType.Manual, null);
        var snapshot = BackupTaskSnapshot.ForPlan(task, plan.CurrentVersion, plan.Name, BackupRunPurpose.PlanFull,
            new(old.ServerId, old.ServerName, old.InstanceId, old.InstanceName, old.DatabaseId, old.DatabaseName),
            new(old.ConnectionAddress, old.SqlCredentialReferenceId, old.EncryptConnection, old.TrustServerCertificate,
                old.CertificateTrustReason, old.ConnectionTimeoutSeconds, old.AllowLegacyTls, old.LegacyTlsReason),
            new(old.LocalSqlBackupRootPath, "v3", new(old.SourceAccessProtocol, old.SourceAccessHost, old.SourceAccessPort,
                old.SourceAccessBasePath, old.SourceCredentialReferenceId, old.SourceSftpHostKeyFingerprint)));
        db.AddRange(task, snapshot);
        await db.SaveChangesAsync();
        var id = Guid.NewGuid();
        var attempt = new BackupAttempt(id, task.Id, 1, now,
            new($@"D:\Synthetic\Backup\{id:N}_FULL.bak", $@"\\synthetic-host\synthetic-share\{id:N}_FULL.bak", null, null, null));
        attempt.BindSqlIdentity(Guid.NewGuid(), Guid.NewGuid());
        task.ClaimExecution(BackupStorageMode.LocalOnly, id, Guid.NewGuid(), "合成夹具领取", now, now.AddMinutes(10));
        db.Add(attempt);
        await db.SaveChangesAsync();
        return new(task.ToStateModel(), snapshot.ToSnapshotModel(), attempt.ToAttemptModel(), task.ToLeaseHandle());
    }
}
