using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Notifications;
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

        Assert.True(await ProjectAsync(factory, graph, Opened.AddMinutes(2), Gaps(graph.TargetId)));
        Assert.Equal(1, await CountNoticesAsync(graph.DatabaseId));
        Assert.Equal(0, await CountOpenAsync(graph.DatabaseId));

        Assert.True(await ProjectAsync(factory, graph, Opened.AddMinutes(3), gaps));
        Assert.Equal(2, await CountNoticesAsync(graph.DatabaseId));
        Assert.Equal(1, await CountOpenAsync(graph.DatabaseId));
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
    public async Task RemoteGapMigrationDownRejectsExistingRowsAndKeepsHistory()
    {
        var temporary = new PlatformDatabaseSqlServerFixture();
        try
        {
            await temporary.InitializeAsync();
            await using var db = temporary.CreateContext();
            var migrations = db.Database.GetMigrations().ToArray();
            var previous = migrations.Single(name => name.EndsWith("_AddRemoteUnrecoverableNotification", StringComparison.Ordinal));
            var current = migrations.Single(name => name.EndsWith("_AddRemoteGapEpisodes", StringComparison.Ordinal));
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync(previous);
            await migrator.MigrateAsync(current);
            var graph = await SeedAsync(temporary);
            using var provider = temporary.CreateServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
            Assert.True(await ProjectAsync(factory, graph, Opened, Gaps(graph.TargetId, "remote_base_unconfirmed")));
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

    private static Task<bool> ProjectAsync(
        IDbContextFactory<PlatformDbContext> factory,
        Graph graph,
        DateTimeOffset occurredAt,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> gaps) =>
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

    private static Dictionary<Guid, IReadOnlyCollection<string>> Gaps(Guid targetId, params string[] codes) =>
        new() { [targetId] = codes };

    private sealed record Graph(
        Guid DatabaseId,
        Guid TaskId,
        Guid TargetId,
        Guid? SecondTargetId,
        BackupTaskStage? Stage,
        Guid[] StorageTargetIds);
}
