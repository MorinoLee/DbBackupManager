using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.BackupManagement;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

[CollectionDefinition("BackupPlanBaselineEnablements", DisableParallelization = true)]
public sealed class BackupPlanBaselineEnablementFixture : ICollectionFixture<PlatformDatabaseSqlServerFixture>;

[Collection("BackupPlanBaselineEnablements")]
public sealed class BackupPlanBaselineEnablementSqlServerTests(PlatformDatabaseSqlServerFixture database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FirstUnpausedDifferentialQueuesOnePlanFullAndALaterRevisionDoesNot()
    {
        var graph = await SeedAsync(database, recordConnection: true);
        using var provider = database.CreateServiceProvider();
        var catalog = new BackupPlanCatalogService(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(), new FrozenTime());
        var created = await catalog.CreateAsync(graph.Actor, Input(graph.DatabaseId, "首次差异", BackupPlanMode.FullAndDifferential, false));
        Assert.Equal(BackupPlanCatalogCode.Succeeded, created.Code);
        Assert.Empty(created.Value!.Notices);
        Assert.Equal(1, await CountPlanFullsAsync(database, graph.DatabaseId));
        var enablement = await SingleEnablementAsync(database, created.Value.Id);
        Assert.Equal(BackupPlanBaselineEnablementDecision.Queued, enablement.Decision);
        Assert.Equal(created.Value.CurrentVersionId, enablement.PlanVersionId);

        var revised = await catalog.UpdateAsync(
            graph.Actor, created.Value.Id, created.Value.Version, Input(graph.DatabaseId, "首次差异", BackupPlanMode.FullAndDifferential, false, 5));
        Assert.Equal(BackupPlanCatalogCode.Succeeded, revised.Code);
        Assert.Equal(2, revised.Value!.VersionNumber);
        Assert.Equal(1, await CountPlanFullsAsync(database, graph.DatabaseId));
        Assert.Equal(enablement.Id, (await SingleEnablementAsync(database, created.Value.Id)).Id);
    }

    [Fact]
    public async Task ReplayingTheSameEnablementDoesNotCreateAnotherTask()
    {
        var graph = await SeedAsync(database);
        var plan = await AddPlanAsync(database, graph, BackupPlanMode.FullAndDifferential, paused: false);
        var mutation = Guid.NewGuid();
        var first = await SupplementAsync(database, plan.Id, mutation, graph.Actor);
        var second = await SupplementAsync(database, plan.Id, mutation, graph.Actor);

        Assert.Equal(BackupPlanBaselineSupplementCode.Queued, first.Code);
        Assert.Equal(BackupPlanBaselineSupplementCode.AlreadyApplied, second.Code);
        Assert.Equal(first.TaskId, second.TaskId);
        Assert.Equal(1, await CountPlanFullsAsync(database, graph.DatabaseId));
        Assert.Equal(plan.Id, (await SingleEnablementAsync(database, plan.Id)).PlanId);
    }

    [Fact]
    public async Task ConcurrentEnablementsForTheSameVersionCreateOneTask()
    {
        var graph = await SeedAsync(database);
        var plan = await AddPlanAsync(database, graph, BackupPlanMode.FullAndDifferential, paused: false);
        var first = SupplementAsync(database, plan.Id, Guid.NewGuid(), graph.Actor);
        var second = SupplementAsync(database, plan.Id, Guid.NewGuid(), graph.Actor);
        await Task.WhenAll(first, second);

        Assert.Equal(1, await CountPlanFullsAsync(database, graph.DatabaseId));
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.BackupPlanBaselineEnablements.CountAsync(item => item.PlanId == plan.Id));
    }

    [Theory]
    [InlineData(BackupPlanMode.Full, false)]
    [InlineData(BackupPlanMode.FullAndDifferential, true)]
    public async Task FullOnlyAndPausedPlansDoNotSupplement(BackupPlanMode mode, bool paused)
    {
        var graph = await SeedAsync(database);
        var plan = await AddPlanAsync(database, graph, mode, paused);
        var result = await SupplementAsync(database, plan.Id, Guid.NewGuid(), graph.Actor);

        Assert.Equal(BackupPlanBaselineSupplementCode.None, result.Code);
        Assert.Equal(0, await CountPlanFullsAsync(database, graph.DatabaseId));
        await using var db = database.CreateContext();
        Assert.Equal(0, await db.BackupPlanBaselineEnablements.CountAsync(item => item.PlanId == plan.Id));
    }

    [Fact]
    public async Task ResumingTheFirstDifferentialVersionQueuesOnce()
    {
        var graph = await SeedAsync(database);
        var plan = await AddPlanAsync(database, graph, BackupPlanMode.FullAndDifferential, paused: true);
        Assert.Equal(BackupPlanBaselineSupplementCode.None, (await SupplementAsync(database, plan.Id, Guid.NewGuid(), graph.Actor)).Code);
        await using (var db = database.CreateContext())
        {
            var stored = await db.BackupPlans.Include(item => item.Versions).SingleAsync(item => item.Id == plan.Id);
            stored.Resume();
            await db.SaveChangesAsync();
        }

        var queued = await SupplementAsync(database, plan.Id, Guid.NewGuid(), graph.Actor);
        Assert.Equal(BackupPlanBaselineSupplementCode.Queued, queued.Code);
        Assert.Equal(BackupPlanBaselineSupplementCode.AlreadyApplied, (await SupplementAsync(database, plan.Id, Guid.NewGuid(), graph.Actor)).Code);
        Assert.Equal(1, await CountPlanFullsAsync(database, graph.DatabaseId));
    }

    [Theory]
    [InlineData(DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields)]
    [InlineData(DifferentialBaselineConclusion.Unmanaged, DifferentialBaselineReason.ExternalFullObserved)]
    [InlineData(DifferentialBaselineConclusion.Mismatch, DifferentialBaselineReason.SourceConflict)]
    public async Task UncertainActiveBaselineIsNotSupplemented(DifferentialBaselineConclusion conclusion, string reason)
    {
        var graph = await SeedAsync(database);
        var plan = await AddPlanAsync(database, graph, BackupPlanMode.FullAndDifferential, paused: false);
        var differential = await CreateTaskAsync(database, graph, plan, BackupRunPurpose.PlanDifferential);
        await AddActiveBaselineAsync(database, differential.TaskId, conclusion, reason);

        var result = await SupplementAsync(database, plan.Id, Guid.NewGuid(), graph.Actor);

        Assert.Equal(BackupPlanBaselineSupplementCode.Skipped, result.Code);
        Assert.Null(result.TaskId);
        Assert.Equal(0, await CountPlanFullsAsync(database, graph.DatabaseId));
        Assert.Equal(BackupPlanBaselineEnablementDecision.SkippedUncertainBaseline, (await SingleEnablementAsync(database, plan.Id)).Decision);
        using var provider = database.CreateServiceProvider();
        var catalog = new BackupPlanCatalogService(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(), new FrozenTime());
        var recovery = await catalog.RecoveryAsync(graph.Actor, graph.DatabaseId);
        Assert.Equal(conclusion switch
        {
            DifferentialBaselineConclusion.Unknown => BackupPlanBaselineObservation.BaselineUnknown,
            DifferentialBaselineConclusion.Unmanaged => BackupPlanBaselineObservation.BaselineUnmanaged,
            _ => BackupPlanBaselineObservation.BaselineMismatch,
        }, recovery.Value!.BaselineNotice);
    }

    [Fact]
    public async Task OutstandingOrdinaryFullIsNotSupplemented()
    {
        var graph = await SeedAsync(database);
        var plan = await AddPlanAsync(database, graph, BackupPlanMode.FullAndDifferential, paused: false);
        await CreateTaskAsync(database, graph, plan, BackupRunPurpose.PlanFull);
        var result = await SupplementAsync(database, plan.Id, Guid.NewGuid(), graph.Actor);

        Assert.Equal(BackupPlanBaselineSupplementCode.Skipped, result.Code);
        Assert.Equal(BackupPlanBaselineEnablementDecision.SkippedOutstandingFull, (await SingleEnablementAsync(database, plan.Id)).Decision);
        Assert.Equal(1, await CountPlanFullsAsync(database, graph.DatabaseId));
    }

    [Fact]
    public async Task ManualFullAndCopyOnlyRunWhileDifferentialAndLogStayClosedWithoutABaseline()
    {
        var graph = await SeedAsync(database);
        var paused = await AddPlanAsync(database, graph, BackupPlanMode.Full, paused: true);
        using var provider = database.CreateServiceProvider();
        var runs = provider.GetRequiredService<IBackupPlanManualExecution>();
        Assert.Equal(BackupPlanTaskCreationCode.StageNotOpen, (await runs.RunAsync(graph.Actor, paused.Id, Guid.NewGuid(), Guid.NewGuid(), BackupRunPurpose.PlanLog)).Code);
        Assert.Equal(0, await CountTasksAsync(database, paused.Id));

        var full = await runs.RunAsync(graph.Actor, paused.Id, Guid.NewGuid(), Guid.NewGuid(), BackupRunPurpose.PlanFull);
        var copy = await runs.RunAsync(graph.Actor, paused.Id, Guid.NewGuid(), Guid.NewGuid(), BackupRunPurpose.AdHocCopyOnlyFull);
        Assert.Equal(BackupPlanTaskCreationCode.Created, full.Code);
        Assert.Equal(BackupRunPurpose.PlanFull, full.Task!.Purpose);
        Assert.Equal(BackupPlanTaskCreationCode.Created, copy.Code);
        Assert.Equal(BackupRunPurpose.AdHocCopyOnlyFull, copy.Task!.Purpose);
        await using var db = database.CreateContext();
        Assert.False(await db.BackupTaskSnapshots.Where(item => item.TaskId == full.Task.TaskId).Select(item => item.UseCopyOnly).SingleAsync());
        Assert.True(await db.BackupTaskSnapshots.Where(item => item.TaskId == copy.Task.TaskId).Select(item => item.UseCopyOnly).SingleAsync());

        var differentialGraph = await SeedAsync(database);
        var differentialPlan = await AddPlanAsync(database, differentialGraph, BackupPlanMode.FullAndDifferential, paused: false);
        var before = await CountTasksAsync(database, differentialPlan.Id);
        var rejected = await runs.RunAsync(differentialGraph.Actor, differentialPlan.Id, Guid.NewGuid(), Guid.NewGuid(), BackupRunPurpose.PlanDifferential);
        Assert.Equal(BackupPlanTaskCreationCode.BaselineRequired, rejected.Code);
        Assert.Null(rejected.Task);
        Assert.Equal(before, await CountTasksAsync(database, differentialPlan.Id));
    }

    [Fact]
    public async Task WorkerSelectsOneEligibleTaskAndDoesNotResendUnknownWork()
    {
        var isolated = new PlatformDatabaseSqlServerFixture();
        try
        {
            await isolated.InitializeAsync();
            var graph = await SeedAsync(isolated);
            var plan = await AddPlanAsync(isolated, graph, BackupPlanMode.FullAndDifferential, paused: false);
            var olderDifferential = await CreateTaskAsync(isolated, graph, plan, BackupRunPurpose.PlanDifferential);
            var full = await CreateTaskAsync(isolated, graph, plan, BackupRunPurpose.PlanFull);
            var newerFull = await CreateTaskAsync(isolated, graph, plan, BackupRunPurpose.PlanFull);
            using var provider = isolated.CreateServiceProvider();
            var queue = provider.GetRequiredService<IBackupPlanWorkerStore>();

            var selected = await queue.SelectAsync(Now.AddHours(1));
            Assert.Equal(BackupPlanWorkerSelectionKind.Execute, selected.Kind);
            Assert.Equal(full.TaskId, selected.TaskId);
            Assert.NotEqual(olderDifferential.TaskId, selected.TaskId);
            Assert.NotEqual(newerFull.TaskId, selected.TaskId);

            await ClaimAsync(isolated, full.TaskId, Now, Now.AddMinutes(10));
            Assert.Equal(BackupPlanWorkerSelectionKind.Wait, (await queue.SelectAsync(Now.AddMinutes(1))).Kind);
            var expired = await queue.SelectAsync(Now.AddHours(2));
            Assert.Equal(BackupPlanWorkerSelectionKind.Reconcile, expired.Kind);
            Assert.Equal(full.TaskId, expired.TaskId);
            Assert.NotEqual(newerFull.TaskId, expired.TaskId);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task WorkerWaitsWhileAnAttemptIsStillRunningOrIndeterminate()
    {
        var isolated = new PlatformDatabaseSqlServerFixture();
        try
        {
            await isolated.InitializeAsync();
            var graph = await SeedAsync(isolated);
            var plan = await AddPlanAsync(isolated, graph, BackupPlanMode.Full, paused: false);
            var running = await CreateTaskAsync(isolated, graph, plan, BackupRunPurpose.PlanFull);
            var pending = await CreateTaskAsync(isolated, graph, plan, BackupRunPurpose.PlanFull);
            await using (var db = isolated.CreateContext())
            {
                var attemptId = Guid.NewGuid();
                var attempt = new BackupAttempt(attemptId, running.TaskId, 1, Now, new(
                    $@"D:\Synthetic\Backup\{attemptId:N}.bak", $@"\\synthetic-host\synthetic-share\{attemptId:N}.bak", null, null, null));
                attempt.MarkBackupRunning(Now);
                db.BackupAttempts.Add(attempt);
                await db.SaveChangesAsync();
            }

            using var provider = isolated.CreateServiceProvider();
            var queue = provider.GetRequiredService<IBackupPlanWorkerStore>();
            Assert.Equal(BackupPlanWorkerSelectionKind.Wait, (await queue.SelectAsync(Now.AddMinutes(1))).Kind);
            await using (var db = isolated.CreateContext())
            {
                var attempt = await db.BackupAttempts.SingleAsync(item => item.TaskId == running.TaskId);
                attempt.RecordBackupIndeterminate(Now.AddMinutes(1), "plan.result_unknown");
                await db.SaveChangesAsync();
            }

            Assert.Equal(BackupPlanWorkerSelectionKind.Wait, (await queue.SelectAsync(Now.AddMinutes(2))).Kind);
            await using var check = isolated.CreateContext();
            Assert.Equal(BackupTaskStatus.Pending, await check.BackupTasks.Where(item => item.Id == pending.TaskId).Select(item => item.Status).SingleAsync());
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task ResumeAfterPausedCreationAndAfterAddingDifferentialBothQueueOnceAndReplay()
    {
        var graph = await SeedAsync(database, recordConnection: true);
        using var provider = database.CreateServiceProvider();
        var catalog = new BackupPlanCatalogService(
            provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(), new FrozenTime());

        var pausedDifferential = await catalog.CreateAsync(
            graph.Actor, Input(graph.DatabaseId, "暂停创建", BackupPlanMode.FullAndDifferential, true));
        Assert.Equal(BackupPlanCatalogCode.Succeeded, pausedDifferential.Code);
        Assert.Equal(0, await CountPlanFullsAsync(database, graph.DatabaseId));
        var resumePausedCreation = Guid.NewGuid();
        var resumedCreation = await catalog.UpdateAsync(
            graph.Actor,
            pausedDifferential.Value!.Id,
            pausedDifferential.Value.Version,
            Input(graph.DatabaseId, "暂停创建", BackupPlanMode.FullAndDifferential, false, mutationId: resumePausedCreation));
        Assert.Equal(BackupPlanCatalogCode.Succeeded, resumedCreation.Code);
        await AssertSupplementedOnceAsync(database, graph.DatabaseId, pausedDifferential.Value.Id, resumePausedCreation, graph.Actor);

        var fullGraph = await SeedAsync(database, recordConnection: true);
        var pausedFull = await catalog.CreateAsync(
            fullGraph.Actor, Input(fullGraph.DatabaseId, "暂停完整", BackupPlanMode.Full, true));
        Assert.Equal(BackupPlanCatalogCode.Succeeded, pausedFull.Code);
        var stillPaused = await catalog.UpdateAsync(
            fullGraph.Actor,
            pausedFull.Value!.Id,
            pausedFull.Value.Version,
            Input(fullGraph.DatabaseId, "暂停完整", BackupPlanMode.FullAndDifferential, true, fullHour: 4));
        Assert.Equal(BackupPlanCatalogCode.Succeeded, stillPaused.Code);
        Assert.Equal(0, await CountPlanFullsAsync(database, fullGraph.DatabaseId));
        var resumeAddedDifferential = Guid.NewGuid();
        var resumedDifferential = await catalog.UpdateAsync(
            fullGraph.Actor,
            pausedFull.Value.Id,
            stillPaused.Value!.Version,
            Input(fullGraph.DatabaseId, "暂停完整", BackupPlanMode.FullAndDifferential, false, 4, resumeAddedDifferential));
        Assert.Equal(BackupPlanCatalogCode.Succeeded, resumedDifferential.Code);
        await AssertSupplementedOnceAsync(database, fullGraph.DatabaseId, pausedFull.Value.Id, resumeAddedDifferential, fullGraph.Actor);
    }

    [Fact]
    public async Task FullStillExecutesWhenMoreThanFiftyDifferentialsLackABaseline()
    {
        var isolated = new PlatformDatabaseSqlServerFixture();
        try
        {
            await isolated.InitializeAsync();
            var graph = await SeedAsync(isolated);
            var plan = await AddPlanAsync(isolated, graph, BackupPlanMode.FullAndDifferential, paused: false);
            var differentialIds = new List<Guid>();
            for (var index = 0; index < 51; index++)
            {
                differentialIds.Add(await InsertTaskAsync(
                    isolated, plan.Id, BackupRunPurpose.PlanDifferential, BackupTaskTriggerType.Scheduled, Now.AddMinutes(-index - 1)));
            }

            var fullId = await InsertTaskAsync(isolated, plan.Id, BackupRunPurpose.PlanFull, BackupTaskTriggerType.Manual, null);
            using var provider = isolated.CreateServiceProvider();
            var selected = await provider.GetRequiredService<IBackupPlanWorkerStore>().SelectAsync(Now.AddHours(1));
            Assert.Equal(BackupPlanWorkerSelectionKind.Execute, selected.Kind);
            Assert.Equal(fullId, selected.TaskId);

            await using var db = isolated.CreateContext();
            var closed = await db.BackupTasks.Where(item => differentialIds.Contains(item.Id)).ToListAsync();
            Assert.Equal(51, closed.Count);
            Assert.All(closed, item => Assert.Equal(BackupTaskStatus.Failed, item.Status));
            Assert.All(closed, item => Assert.Equal("baseline_required", item.ErrorCode));
            Assert.Equal(BackupTaskStatus.Pending, (await db.BackupTasks.SingleAsync(item => item.Id == fullId)).Status);
            Assert.Equal(1, await db.BackupTaskSnapshots.CountAsync(item => item.DatabaseId == graph.DatabaseId && item.Purpose == BackupRunPurpose.PlanFull));
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnknownReconciliationThatIsNotDueDoesNotOccupyTheRound()
    {
        var isolated = new PlatformDatabaseSqlServerFixture();
        try
        {
            await isolated.InitializeAsync();
            var graph = await SeedAsync(isolated);
            var plan = await AddPlanAsync(isolated, graph, BackupPlanMode.Full, paused: false);
            var unknownId = await InsertTaskAsync(isolated, plan.Id, BackupRunPurpose.PlanFull, BackupTaskTriggerType.Manual, null);
            var committedAt = Now.AddMinutes(1).AddSeconds(1);
            await using (var db = isolated.CreateContext())
            {
                var task = await db.BackupTasks.SingleAsync(item => item.Id == unknownId);
                var attemptId = Guid.NewGuid();
                var lease = Guid.NewGuid();
                db.BackupAttempts.Add(new BackupAttempt(attemptId, unknownId, 1, Now, new(
                    $@"D:\Synthetic\Backup\{attemptId:N}.bak", $@"\\synthetic-host\synthetic-share\{attemptId:N}.bak", null, null, null)));
                task.ClaimExecution(BackupStorageMode.LocalOnly, attemptId, lease, "合成执行", Now, Now.AddMinutes(5));
                task.RecordIndeterminateResult(BackupStorageMode.LocalOnly, lease, Now.AddMinutes(1), "backup_result_unknown", "结果待核对");
                var reconciliation = Guid.NewGuid();
                task.AcquireReconciliationLease(reconciliation, "合成核对", Now.AddMinutes(1), Now.AddMinutes(4));
                task.CompleteReconciliation(
                    BackupStorageMode.LocalOnly, reconciliation, committedAt, BackupReconciliationOutcome.Inconclusive, "evidence_missing", "只读证据不足");
                await db.SaveChangesAsync();
            }

            using var provider = isolated.CreateServiceProvider();
            var queue = provider.GetRequiredService<IBackupPlanWorkerStore>();
            Assert.Equal(BackupPlanWorkerSelectionKind.Idle, (await queue.SelectAsync(committedAt.AddSeconds(1))).Kind);
            var fullId = await InsertTaskAsync(isolated, plan.Id, BackupRunPurpose.PlanFull, BackupTaskTriggerType.Manual, null);
            var selected = await queue.SelectAsync(committedAt.AddSeconds(2));
            Assert.Equal(BackupPlanWorkerSelectionKind.Execute, selected.Kind);
            Assert.Equal(fullId, selected.TaskId);
            await using var dbCheck = isolated.CreateContext();
            var unknown = await dbCheck.BackupTasks.SingleAsync(item => item.Id == unknownId);
            Assert.Equal(BackupTaskStatus.NeedsAttention, unknown.Status);
            Assert.Equal(committedAt + TimeSpan.FromSeconds(15), unknown.NextReconciliationAtUtc);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task EnablementMigrationDownRejectsExistingRowsAndKeepsHistory()
    {
        var isolated = new PlatformDatabaseSqlServerFixture();
        try
        {
            await isolated.InitializeAsync();
            await using var db = isolated.CreateContext();
            var migrations = db.Database.GetMigrations().ToArray();
            var current = migrations.Single(name => name.EndsWith("_AddBackupPlanBaselineEnablements", StringComparison.Ordinal));
            var migrator = db.Database.GetService<IMigrator>();
            var graph = await SeedAsync(isolated);
            var plan = await AddPlanAsync(isolated, graph, BackupPlanMode.FullAndDifferential, paused: false);
            Assert.Equal(BackupPlanBaselineSupplementCode.Queued, (await SupplementAsync(isolated, plan.Id, Guid.NewGuid(), graph.Actor)).Code);

            var historyAtLatest = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            var blocked = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(current));
            Assert.Equal(51014, blocked.Number);
            Assert.Equal(historyAtLatest, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Equal(1, await db.BackupPlanBaselineEnablements.CountAsync());
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task EnablementSequenceMigrationDownIsBlockedByLogMetadataGuard()
    {
        var isolated = new PlatformDatabaseSqlServerFixture();
        try
        {
            await isolated.InitializeAsync();
            await using var db = isolated.CreateContext();
            var migrations = db.Database.GetMigrations().ToArray();
            var current = migrations.Single(name => name.EndsWith("_AddBackupPlanEnablementSequenceAndBaselineTrigger", StringComparison.Ordinal));
            var migrator = db.Database.GetService<IMigrator>();
            var graph = await SeedAsync(isolated);
            var plan = await AddPlanAsync(isolated, graph, BackupPlanMode.FullAndDifferential, paused: false);
            Assert.Equal(BackupPlanBaselineSupplementCode.Queued, (await SupplementAsync(isolated, plan.Id, Guid.NewGuid(), graph.Actor)).Code);

            var history = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            var error = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(current));
            Assert.Equal(51014, error.Number);
            Assert.Equal(history, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Equal(1, await db.BackupPlanBaselineEnablements.CountAsync());
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    private static async Task<Graph> SeedAsync(PlatformDatabaseSqlServerFixture database, bool recordConnection = false)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sql = new CredentialReference(Guid.NewGuid(), $"SQL-{suffix}", CredentialKind.SqlPassword, "synthetic-user", "protected:synthetic", "dp-v1");
        var source = new CredentialReference(Guid.NewGuid(), $"SRC-{suffix}", CredentialKind.SmbPassword, "synthetic-user", "protected:synthetic", "dp-v1");
        var server = new DatabaseServer(Guid.NewGuid(), $"SRC-{suffix}", @"D:\Synthetic", new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", source.Id, null));
        var instance = new DatabaseInstance(Guid.NewGuid(), server.Id, $"SQL-{suffix}", $"synthetic-sql-{suffix}", sql.Id, true, false, null, 30);
        if (recordConnection)
        {
            instance.RecordConnectionSucceeded(Now, "15.0.synthetic", null, null);
        }

        var managed = new ManagedDatabase(Guid.NewGuid(), instance.Id, $"DB_{suffix}", false, true, Now, "SIMPLE", "ONLINE");
        managed.SetManaged(true);
        var admin = new AdminUser(Guid.NewGuid(), $"admin-{suffix}", $"ADMIN-{suffix}", "synthetic-hash", "synthetic-stamp");
        await using (var db = database.CreateContext())
        {
            db.AddRange(sql, source, server, instance, managed, admin);
            await db.SaveChangesAsync();
        }

        return new(managed.Id, new(admin.Id, admin.Username, admin.SecurityStamp));
    }

    private static async Task<BackupPlan> AddPlanAsync(
        PlatformDatabaseSqlServerFixture database, Graph graph, BackupPlanMode mode, bool paused)
    {
        var plan = BackupPlan.Create(Guid.NewGuid(), graph.DatabaseId, $"合成计划-{Guid.NewGuid():N}"[..12], Guid.NewGuid(), Definition(mode), Now.AddDays(-2));
        if (paused)
        {
            plan.Pause();
        }

        using var provider = database.CreateServiceProvider();
        await new BackupPlanStore(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>()).AddAsync(plan);
        return plan;
    }

    private static async Task AssertSupplementedOnceAsync(
        PlatformDatabaseSqlServerFixture database, Guid databaseId, Guid planId, Guid mutationId, AdminSession actor)
    {
        Assert.Equal(1, await CountPlanFullsAsync(database, databaseId));
        await using var db = database.CreateContext();
        var enablement = await db.BackupPlanBaselineEnablements.SingleAsync(item => item.PlanId == planId && item.MutationId == mutationId);
        Assert.Equal(BackupPlanBaselineEnablementDecision.Queued, enablement.Decision);
        var task = await db.BackupTasks.SingleAsync(item => item.Id == enablement.TaskId);
        Assert.Equal(BackupTaskTriggerType.BaselineSupplement, task.TriggerType);
        Assert.Null(task.ScheduledSlotAtUtc);
        var replay = await SupplementAsync(database, planId, mutationId, actor);
        Assert.Equal(BackupPlanBaselineSupplementCode.AlreadyApplied, replay.Code);
        Assert.Equal(task.Id, replay.TaskId);
        Assert.Equal(1, await CountPlanFullsAsync(database, databaseId));
    }

    private static async Task<Guid> InsertTaskAsync(
        PlatformDatabaseSqlServerFixture database, Guid planId, BackupRunPurpose purpose, BackupTaskTriggerType trigger, DateTimeOffset? slot)
    {
        await using var db = database.CreateContext();
        var plan = await db.BackupPlans.Include(item => item.Versions).SingleAsync(item => item.Id == planId);
        var managed = await db.ManagedDatabases.SingleAsync(item => item.Id == plan.DatabaseId);
        var instance = await db.DatabaseInstances.SingleAsync(item => item.Id == managed.InstanceId);
        var server = await db.DatabaseServers.SingleAsync(item => item.Id == instance.ServerId);
        var task = BackupTask.ForPlan(Guid.NewGuid(), plan.Id, plan.CurrentVersionId, purpose, trigger, slot);
        var snapshot = BackupTaskSnapshot.ForPlan(
            task,
            plan.CurrentVersion,
            plan.Name,
            purpose,
            new(server.Id, server.Name, instance.Id, instance.Name, managed.Id, managed.DatabaseName),
            new(instance.ConnectionAddress, instance.SqlCredentialReferenceId, instance.EncryptConnection, instance.TrustServerCertificate, instance.CertificateTrustReason, instance.ConnectionTimeoutSeconds),
            new(server.LocalBackupRootPath, "v3", new(server.StagingAccessProtocol, server.StagingAccessHost, server.StagingAccessPort, server.StagingAccessBasePath, server.StagingCredentialReferenceId, server.StagingSftpHostKeyFingerprint)),
            null);
        db.AddRange(task, snapshot);
        await db.SaveChangesAsync();
        return task.Id;
    }

    private static async Task<BackupPlanBaselineSupplementResult> SupplementAsync(
        PlatformDatabaseSqlServerFixture database, Guid planId, Guid mutationId, AdminSession actor)
    {
        using var provider = database.CreateServiceProvider();
        return await new BackupPlanBaselineSupplementStore(
            provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(), new FrozenTime()).SupplementAsync(planId, mutationId, actor);
    }

    private static async Task<CreatedBackupPlanTask> CreateTaskAsync(
        PlatformDatabaseSqlServerFixture database, Graph graph, BackupPlan plan, BackupRunPurpose purpose)
    {
        using var provider = database.CreateServiceProvider();
        var created = await provider.GetRequiredService<IBackupPlanTaskCreationStore>().CreateAsync(new(
            new(Guid.NewGuid(), Guid.NewGuid(), plan.Id, plan.CurrentVersionId, purpose),
            BackupTaskTriggerType.Manual, Now, graph.Actor.AdminUserId, graph.Actor.SecurityStamp));
        Assert.Equal(BackupPlanTaskCreationCode.Created, created.Code);
        return created.Task!;
    }

    private static async Task AddActiveBaselineAsync(
        PlatformDatabaseSqlServerFixture database, Guid taskId, DifferentialBaselineConclusion conclusion, string reason)
    {
        await using var db = database.CreateContext();
        var attemptId = Guid.NewGuid();
        db.BackupAttempts.Add(new BackupAttempt(attemptId, taskId, 1, Now, new(
            $@"D:\Synthetic\Backup\{attemptId:N}.bak", $@"\\synthetic-host\synthetic-share\{attemptId:N}.bak", null, null, null)));
        db.BackupSetEvidence.Add(new BackupSetEvidence(
            Guid.NewGuid(), taskId, attemptId, null, Guid.NewGuid(), 1, 0,
            BackupSetEvidenceSource.Database, BackupSetEvidenceKind.ActiveBaseline, new BackupSetMetadata(),
            new BackupCompletionTimeDecision(null, BackupCompletionTimeSource.Unknown, BackupCompletionTimeReason.SqlFinishMissing),
            new BackupSetAssessment(BackupMetadataState.Known, conclusion, reason), Now, false));
        await db.SaveChangesAsync();
    }

    private static async Task ClaimAsync(PlatformDatabaseSqlServerFixture database, Guid taskId, DateTimeOffset acquired, DateTimeOffset expires)
    {
        await using var db = database.CreateContext();
        var task = await db.BackupTasks.SingleAsync(item => item.Id == taskId);
        var attemptId = Guid.NewGuid();
        db.BackupAttempts.Add(new BackupAttempt(attemptId, taskId, 1, acquired, new(
            $@"D:\Synthetic\Backup\{attemptId:N}.bak", $@"\\synthetic-host\synthetic-share\{attemptId:N}.bak", null, null, null)));
        task.ClaimExecution(BackupStorageMode.LocalOnly, attemptId, Guid.NewGuid(), "合成执行", acquired, expires);
        await db.SaveChangesAsync();
    }

    private static async Task<int> CountPlanFullsAsync(PlatformDatabaseSqlServerFixture database, Guid databaseId)
    {
        await using var db = database.CreateContext();
        return await db.BackupTaskSnapshots.CountAsync(item => item.DatabaseId == databaseId && item.Purpose == BackupRunPurpose.PlanFull && !item.UseCopyOnly);
    }

    private static async Task<int> CountTasksAsync(PlatformDatabaseSqlServerFixture database, Guid planId)
    {
        await using var db = database.CreateContext();
        return await db.BackupTasks.CountAsync(item => item.PlanId == planId);
    }

    private static async Task<BackupPlanBaselineEnablement> SingleEnablementAsync(PlatformDatabaseSqlServerFixture database, Guid planId)
    {
        await using var db = database.CreateContext();
        return await db.BackupPlanBaselineEnablements.SingleAsync(item => item.PlanId == planId);
    }

    private static BackupPlanDefinition Definition(BackupPlanMode mode) => new(
        mode,
        new(BackupScheduleType.Daily, new TimeOnly(2, 0), BackupWeekdays.None),
        mode == BackupPlanMode.Full ? null : new(BackupScheduleType.Daily, new TimeOnly(3, 0), BackupWeekdays.None),
        null, "UTC", BackupStorageMode.LocalOnly, null, 14, null, true, false, 120, 60, 90);

    private static BackupPlanInput Input(
        Guid databaseId, string name, BackupPlanMode mode, bool paused, int fullHour = 1, Guid? mutationId = null) => new(
        databaseId, name, (int)mode, new(1, new TimeOnly(fullHour, 15), 0),
        mode == BackupPlanMode.Full ? null : new(1, new TimeOnly(2, 30), 0),
        "UTC", (int)BackupStorageMode.LocalOnly, null, 7, null, true, false, 60, 30, 45, paused,
        mutationId ?? Guid.NewGuid());

    private sealed record Graph(Guid DatabaseId, AdminSession Actor);

    private sealed class FrozenTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
