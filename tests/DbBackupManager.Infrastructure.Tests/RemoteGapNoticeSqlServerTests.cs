using System.Data;
using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.Notifications;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Domain.Notifications;
using DbBackupManager.Infrastructure.BackupManagement;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class RemoteGapNoticeSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    private static readonly DateTimeOffset Opened = new(2026, 10, 13, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RepeatedGapSendsOneNoticeUntilRecoveryThenSendsAgain()
    {
        var graph = await SeedAsync();
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var gaps = Gaps(graph.TargetId, PlanLocationRecovery.RemoteBaseUnconfirmed);

        Assert.True(await ProjectAsync(factory, graph, Opened, gaps));
        Assert.True(await ProjectAsync(factory, graph, Opened.AddMinutes(1), gaps));
        Assert.Equal(1, await CountNoticesAsync(graph.DatabaseId));

        Assert.True(await ProjectAsync(factory, graph, Opened.AddMinutes(2), Recovered(graph.TargetId)));
        Assert.Equal(1, await CountNoticesAsync(graph.DatabaseId));
        Assert.Equal(0, await CountOpenAsync(graph.DatabaseId));

        Assert.True(await ProjectAsync(factory, graph, Opened.AddMinutes(3), gaps));
        Assert.Equal(2, await CountNoticesAsync(graph.DatabaseId));
        Assert.Equal(1, await CountOpenAsync(graph.DatabaseId));
    }

    [Fact]
    public async Task GapThenUnknownThenGapSendsOneNotice()
    {
        var graph = await SeedAsync();
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var gaps = Gaps(graph.TargetId, PlanLocationRecovery.RemoteBaseUnconfirmed);

        Assert.True(await ProjectAsync(factory, graph, Opened, gaps));
        Assert.True(await ProjectAsync(factory, graph, Opened.AddMinutes(1), Unknown(graph.TargetId)));
        Assert.True(await ProjectAsync(factory, graph, Opened.AddMinutes(2), new Dictionary<Guid, RemoteGapProjection>()));
        Assert.Equal(1, await CountNoticesAsync(graph.DatabaseId));
        Assert.Equal(1, await CountOpenAsync(graph.DatabaseId));

        Assert.True(await ProjectAsync(factory, graph, Opened.AddMinutes(3), gaps));
        Assert.Equal(1, await CountNoticesAsync(graph.DatabaseId));
        Assert.Equal(1, await CountOpenAsync(graph.DatabaseId));
    }

    [Fact]
    public async Task FailedProjectionIsRetriedByTheNextWriteAndReadsDoNotSend()
    {
        var graph = await SeedAsync();
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var observation = new RemoteGapNotices.Observation(
            graph.DatabaseId, graph.TaskId, graph.Stage, Opened, [graph.TargetId]);
        var gaps = Gaps(graph.TargetId, PlanLocationRecovery.RemoteBaseUnconfirmed);
        await using (var db = database.CreateContext())
        {
            await RemoteGapNotices.MarkPendingAsync(db, observation, CancellationToken.None);
            await RemoteGapNotices.MarkPendingAsync(db, observation, CancellationToken.None);
        }

        Assert.Equal(1, await CountPendingAsync(graph.DatabaseId));
        await RemoteGapNotices.TryProjectAsync(new ThrowingFactory(), observation, CancellationToken.None);
        Assert.Equal(0, await CountNoticesAsync(graph.DatabaseId));
        Assert.Equal(1, await CountPendingAsync(graph.DatabaseId));

        await new PlanLocationRetentionStore(factory).AssessAsync(graph.DatabaseId, Opened);
        var adminId = Guid.NewGuid();
        var stamp = "synthetic-stamp";
        await using (var db = database.CreateContext())
        {
            db.AdminUsers.Add(new AdminUser(adminId, $"admin-{adminId:N}", $"ADMIN-{adminId:N}", "synthetic-hash", stamp));
            await db.SaveChangesAsync();
        }

        var actor = new AdminSession(adminId, $"admin-{adminId:N}", stamp);
        var catalog = new BackupPlanCatalogService(factory, TimeProvider.System);
        var withoutPlan = await catalog.RecoveryAsync(actor, graph.DatabaseId);
        Assert.Equal(BackupPlanCatalogCode.Succeeded, withoutPlan.Code);
        Assert.False(withoutPlan.Value!.HasCurrentPlan);
        var definition = new BackupPlanDefinition(
            BackupPlanMode.Full,
            new RecurringBackupSchedule(BackupScheduleType.Daily, new TimeOnly(2, 0), BackupWeekdays.None),
            null,
            null,
            "UTC",
            BackupStorageMode.RemoteOnly,
            graph.TargetId,
            null,
            7,
            true,
            false,
            120,
            60,
            60);
        await new BackupPlanStore(factory).AddAsync(
            BackupPlan.Create(Guid.NewGuid(), graph.DatabaseId, "合成计划", Guid.NewGuid(), definition, Opened));
        var withPlan = await catalog.RecoveryAsync(actor, graph.DatabaseId);
        Assert.Equal(BackupPlanCatalogCode.Succeeded, withPlan.Code);
        Assert.True(withPlan.Value!.HasCurrentPlan);
        Assert.Equal(0, await CountNoticesAsync(graph.DatabaseId));
        Assert.Equal(1, await CountPendingAsync(graph.DatabaseId));

        await RemoteGapNotices.TryDrainPendingAsync(
            factory, graph.DatabaseId, CancellationToken.None, knownGaps: gaps);
        Assert.Equal(1, await CountNoticesAsync(graph.DatabaseId));
        Assert.Equal(0, await CountPendingAsync(graph.DatabaseId));
        Assert.Equal(1, await CountOpenAsync(graph.DatabaseId));

        await RemoteGapNotices.TryDrainPendingAsync(
            factory, graph.DatabaseId, CancellationToken.None, knownGaps: gaps);
        Assert.Equal(1, await CountNoticesAsync(graph.DatabaseId));
        Assert.Equal(1, await CountOpenAsync(graph.DatabaseId));
    }

    [Fact]
    public async Task MarkingWhileClearingCommitsFactsKeepsTheUnprojectedTargetAndSendsOnce()
    {
        var graph = await SeedAsync(secondTarget: true);
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var kept = graph.SecondTargetId!.Value;
        await using (var db = database.CreateContext())
        {
            await RemoteGapNotices.MarkPendingAsync(
                db,
                new RemoteGapNotices.Observation(graph.DatabaseId, graph.TaskId, graph.Stage, Opened, [graph.TargetId]),
                CancellationToken.None);
        }

        const int writers = 8;
        var facts = Enumerable.Range(0, writers).Select(index => CommitFactAsync(factory, graph, kept, Opened.AddSeconds(index)));
        var clears = Enumerable.Range(0, writers).Select(index => ProjectAsync(
            factory,
            graph,
            Opened.AddMinutes(1).AddSeconds(index),
            Gaps(graph.TargetId, PlanLocationRecovery.RemoteBaseUnconfirmed)));
        await Task.WhenAll(facts.Concat(clears));

        Assert.Equal(writers, await CountFactsAsync(graph.TaskId));
        Assert.Equal(1, await CountNoticesAsync(graph.DatabaseId));
        Assert.Equal(1, await CountOpenAsync(graph.DatabaseId));
        Assert.Contains(kept, await PendingTargetsAsync(graph.DatabaseId));
    }

    [Fact]
    public async Task DifferentTargetOrGapSendsItsOwnNotice()
    {
        var graph = await SeedAsync(secondTarget: true);
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        await ProjectAsync(factory, graph, Opened, Gaps(graph.TargetId, PlanLocationRecovery.RemoteBaseUnconfirmed));
        await ProjectAsync(
            factory,
            graph with { StorageTargetIds = [graph.SecondTargetId!.Value] },
            Opened.AddMinutes(1),
            Gaps(graph.SecondTargetId!.Value, PlanLocationRecovery.RemoteBaseUnconfirmed));
        await ProjectAsync(
            factory,
            graph,
            Opened.AddMinutes(2),
            Gaps(graph.TargetId, PlanLocationRecovery.RemoteBaseUnconfirmed, PlanLocationRecovery.RemoteDependentUnconfirmed));

        Assert.Equal(3, await CountNoticesAsync(graph.DatabaseId));
        Assert.Equal(3, await CountOpenAsync(graph.DatabaseId));
    }

    [Fact]
    public async Task MissingTaskStageStillNotifiesAtCleanup()
    {
        var graph = await SeedAsync();
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        Assert.True(await ProjectAsync(
            factory,
            graph with { Stage = null },
            Opened,
            Gaps(graph.TargetId, PlanLocationRecovery.RemoteBaseUnconfirmed)));

        await using var db = database.CreateContext();
        var notice = await db.NotificationOutbox.SingleAsync(item => item.TaskId == graph.TaskId);
        Assert.Equal(BackupTaskStage.Cleanup, notice.Stage);
        Assert.Equal(NotificationType.RemoteUnrecoverable, notice.Type);
    }

    [Fact]
    public async Task ConcurrentDetectionsKeepASingleOpenNotice()
    {
        var graph = await SeedAsync();
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var gaps = Gaps(graph.TargetId, PlanLocationRecovery.RemoteBaseUnconfirmed);
        var projections = Enumerable.Range(0, 8).Select(index => ProjectAsync(
            factory, graph, Opened.AddSeconds(index), gaps));
        var results = await Task.WhenAll(projections);

        Assert.Contains(true, results);
        Assert.Equal(1, await CountNoticesAsync(graph.DatabaseId));
        Assert.Equal(1, await CountOpenAsync(graph.DatabaseId));
    }

    [Fact]
    public async Task OpenGapIndexRejectsASecondEpisode()
    {
        var graph = await SeedAsync();
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        Assert.True(await ProjectAsync(factory, graph, Opened, Gaps(graph.TargetId, "remote_base_unconfirmed")));

        await using var db = database.CreateContext();
        var taskId = graph.TaskId;
        var notice = new NotificationOutbox(
            Guid.NewGuid(),
            Guid.NewGuid(),
            NotificationType.RemoteUnrecoverable,
            NotificationSourceKind.BackupTask,
            taskId,
            taskId,
            BackupTaskStage.Cleanup,
            Opened.AddMinutes(1),
            "remote_base_unconfirmed");
        db.NotificationOutbox.Add(notice);
        db.RemoteGapEpisodes.Add(new RemoteGapEpisode(
            Guid.NewGuid(), graph.DatabaseId, graph.TargetId, "remote_base_unconfirmed", notice.Id, Opened.AddMinutes(1)));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("UX_RemoteGapEpisodes_Open", error.InnerException?.Message, StringComparison.Ordinal);
        Assert.Equal(1, await CountOpenAsync(graph.DatabaseId));
    }

    [Fact]
    public async Task RemoteGapPendingMigrationDownRejectsExistingRowsAndKeepsHistory()
    {
        var temporary = new PlatformDatabaseSqlServerFixture();
        try
        {
            await temporary.InitializeAsync();
            await using var db = temporary.CreateContext();
            var migrations = db.Database.GetMigrations().ToArray();
            var pendingMigration = migrations.Single(name => name.EndsWith("_AddRemoteGapProjectionPendings", StringComparison.Ordinal));
            var previous = migrations.Single(name => name.EndsWith("_AddRemoteGapEpisodes", StringComparison.Ordinal));
            var graph = await SeedAsync(temporary);
            await using (var write = temporary.CreateContext())
            {
                await RemoteGapNotices.MarkPendingAsync(
                    write,
                    new RemoteGapNotices.Observation(graph.DatabaseId, graph.TaskId, graph.Stage, Opened, [graph.TargetId]),
                    CancellationToken.None);
            }

            var migrator = db.Database.GetService<IMigrator>();
            // 删表迁移不守卫待补发行，先单独退掉，再检验待补发迁移自身的 Down。
            await migrator.MigrateAsync(pendingMigration);
            var history = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            var error = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(previous));
            Assert.Equal(51007, error.Number);
            Assert.Equal(history, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
            await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM [RemoteGapProjectionPendingTargets];
                DELETE FROM [RemoteGapProjectionPendings];
                """);
            await migrator.MigrateAsync(previous);
            await migrator.MigrateAsync();
            Assert.Equal(0, await db.RemoteGapProjectionPendings.CountAsync());
        }
        finally
        {
            await temporary.DisposeAsync();
        }
    }

    [Fact]
    public async Task RemoteGapMigrationDownRejectsExistingRowsAndKeepsHistory()
    {
        var temporary = new PlatformDatabaseSqlServerFixture();
        try
        {
            await temporary.InitializeAsync();
            await using var db = temporary.CreateContext();
            var migrations = db.Database.GetMigrations().ToArray();
            var previous = migrations.Single(name => name.EndsWith("_AddRemoteUnrecoverableNotification", StringComparison.Ordinal));
            var episodes = migrations.Single(name => name.EndsWith("_AddRemoteGapEpisodes", StringComparison.Ordinal));
            var migrator = db.Database.GetService<IMigrator>();
            // 投影会读取待补发表。先在最新结构上写入缺口，再退掉后面没有数据的迁移，单独检验缺口迁移的 Down。
            var graph = await SeedAsync(temporary);
            using var provider = temporary.CreateServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
            Assert.True(await ProjectAsync(factory, graph, Opened, Gaps(graph.TargetId, "remote_base_unconfirmed")));
            await migrator.MigrateAsync(episodes);
            var history = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            var error = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(previous));
            Assert.Equal(51006, error.Number);
            Assert.Equal(history, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Equal(1, await db.RemoteGapEpisodes.CountAsync());
            await db.RemoteGapEpisodes.ExecuteDeleteAsync();
            await db.NotificationOutbox.ExecuteDeleteAsync();
            await migrator.MigrateAsync(previous);
            await migrator.MigrateAsync();
            Assert.Equal(0, await db.RemoteGapEpisodes.CountAsync());
        }
        finally
        {
            await temporary.DisposeAsync();
        }
    }

    private static async Task CommitFactAsync(
        IDbContextFactory<PlatformDbContext> factory,
        Graph graph,
        Guid keptTarget,
        DateTimeOffset occurredAt)
    {
        await using var strategyContext = await factory.CreateDbContextAsync();
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            db.AuditRecords.Add(new AuditRecord(
                null,
                "plan.remote_gap.fact",
                "BackupTask",
                graph.TaskId.ToString("N"),
                "succeeded",
                "synthetic"));
            await db.SaveChangesAsync();
            await RemoteGapNotices.MarkPendingAsync(
                db,
                new RemoteGapNotices.Observation(graph.DatabaseId, graph.TaskId, graph.Stage, occurredAt, [keptTarget]),
                CancellationToken.None);
            await transaction.CommitAsync();
        });
    }

    private async Task<int> CountFactsAsync(Guid taskId)
    {
        await using var db = database.CreateContext();
        var target = taskId.ToString("N");
        return await db.AuditRecords.CountAsync(item => item.Action == "plan.remote_gap.fact" && item.TargetId == target);
    }

    private async Task<Guid[]> PendingTargetsAsync(Guid databaseId)
    {
        await using var db = database.CreateContext();
        var pending = await db.RemoteGapProjectionPendings
            .Include(item => item.Targets)
            .SingleOrDefaultAsync(item => item.DatabaseId == databaseId);
        return pending is null ? [] : pending.TargetIds().ToArray();
    }

    private static Task<bool> ProjectAsync(
        IDbContextFactory<PlatformDbContext> factory,
        Graph graph,
        DateTimeOffset occurredAt,
        IReadOnlyDictionary<Guid, RemoteGapProjection> gaps) =>
        RemoteGapNotices.ProjectAsync(
            factory,
            new RemoteGapNotices.Observation(graph.DatabaseId, graph.TaskId, graph.Stage, occurredAt, graph.StorageTargetIds),
            gaps,
            CancellationToken.None);

    private async Task<int> CountNoticesAsync(Guid databaseId)
    {
        await using var db = database.CreateContext();
        var notificationIds = db.RemoteGapEpisodes.Where(episode => episode.DatabaseId == databaseId).Select(episode => episode.NotificationId);
        return await db.NotificationOutbox.CountAsync(item => notificationIds.Contains(item.Id));
    }

    private async Task<int> CountPendingAsync(Guid databaseId)
    {
        await using var db = database.CreateContext();
        return await db.RemoteGapProjectionPendings.CountAsync(item => item.DatabaseId == databaseId);
    }

    private async Task<int> CountOpenAsync(Guid databaseId)
    {
        await using var db = database.CreateContext();
        return await db.RemoteGapEpisodes.CountAsync(episode => episode.DatabaseId == databaseId && episode.RecoveredAtUtc == null);
    }

    private Task<Graph> SeedAsync(bool secondTarget = false) => SeedAsync(database, secondTarget);

    private static async Task<Graph> SeedAsync(PlatformDatabaseSqlServerFixture fixture, bool secondTarget = false)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var staging = new CredentialReference(Guid.NewGuid(), $"staging-{suffix}", CredentialKind.SmbPassword, "synthetic", "synthetic-cipher", "dp-smb-password-v1");
        var sql = new CredentialReference(Guid.NewGuid(), $"sql-{suffix}", CredentialKind.SqlPassword, "synthetic", "synthetic-cipher", "dp-sql-password-v1");
        var remote = new CredentialReference(Guid.NewGuid(), $"remote-{suffix}", CredentialKind.SmbPassword, "synthetic-remote", "synthetic-cipher", "dp-smb-password-v1");
        var server = new DatabaseServer(Guid.NewGuid(), $"server-{suffix}", @"D:\Synthetic", new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", staging.Id, null));
        var instance = new DatabaseInstance(Guid.NewGuid(), server.Id, "instance", $"synthetic-{suffix}", sql.Id, true, false, null, 15);
        var managed = new ManagedDatabase(Guid.NewGuid(), instance.Id, $"db-{suffix}", false, true, Opened);
        var target = new StorageTarget(Guid.NewGuid(), $"target-{suffix}", new(FileTransferProtocol.Smb, "synthetic-remote-host", null, "synthetic-remote-share", remote.Id, null));
        StorageTarget? other = secondTarget
            ? new StorageTarget(Guid.NewGuid(), $"target-b-{suffix}", new(FileTransferProtocol.Smb, "synthetic-remote-host-b", null, "synthetic-remote-share-b", remote.Id, null))
            : null;
        await using var db = fixture.CreateContext();
        db.AddRange(staging, sql, remote, server, instance, managed, target);
        if (other is not null)
        {
            db.Add(other);
        }

        await db.SaveChangesAsync();
        return new Graph(managed.Id, Guid.NewGuid(), target.Id, other?.Id, BackupTaskStage.ValidateCopy, [target.Id]);
    }

    private static Dictionary<Guid, RemoteGapProjection> Gaps(Guid targetId, params string[] codes) =>
        new() { [targetId] = RemoteGapProjection.Open(codes) };

    private static Dictionary<Guid, RemoteGapProjection> Recovered(Guid targetId) =>
        new() { [targetId] = RemoteGapProjection.Recovered };

    private static Dictionary<Guid, RemoteGapProjection> Unknown(Guid targetId) =>
        new() { [targetId] = RemoteGapProjection.Hold };

    private sealed class ThrowingFactory : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() =>
            throw new InvalidOperationException("Password=synthetic-secret;Data Source=synthetic-host");

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<PlatformDbContext>(
                new InvalidOperationException("Password=synthetic-secret;Data Source=synthetic-host"));
    }

    private sealed record Graph(
        Guid DatabaseId,
        Guid TaskId,
        Guid TargetId,
        Guid? SecondTargetId,
        BackupTaskStage? Stage,
        Guid[] StorageTargetIds);
}
