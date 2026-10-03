using System.Data.Common;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.BackupManagement;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class TaskEventRetentionSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ClearBackupTaskDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CleanupDeletesExpiredPublishedEventsAndKeepsEverythingElse()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var taskId = await SeedTaskAsync(factory);
        var expired = Guid.NewGuid();
        var recent = Guid.NewGuid();
        var unpublished = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var db = database.CreateContext())
        {
            db.TaskEvents.AddRange(
                new TaskEvent(expired, taskId, now.AddDays(-40)),
                new TaskEvent(recent, taskId, now.AddDays(-1)),
                new TaskEvent(unpublished, taskId, now.AddDays(-40)));
            await db.SaveChangesAsync();
            await db.TaskEvents.Where(item => item.EventId == expired)
                .ExecuteUpdateAsync(setter => setter.SetProperty(item => item.PublishedAtUtc, now.AddDays(-40)));
            await db.TaskEvents.Where(item => item.EventId == recent)
                .ExecuteUpdateAsync(setter => setter.SetProperty(item => item.PublishedAtUtc, now.AddDays(-1)));
        }

        var store = new TaskEventRetentionStore(factory);
        var runner = new TaskEventRetentionRunner(
            store,
            new FrozenTime(now),
            new(TimeSpan.FromDays(30), 200_000, TimeSpan.FromHours(1), 5_000, 500, TimeSpan.FromHours(2)));
        var result = await runner.RunOnceAsync();

        Assert.Equal(1, result.WindowDeleted);
        await using var verify = database.CreateContext();
        Assert.False(await verify.TaskEvents.AnyAsync(item => item.EventId == expired));
        Assert.True(await verify.TaskEvents.AnyAsync(item => item.EventId == recent));
        Assert.True(await verify.TaskEvents.AnyAsync(item => item.EventId == unpublished));
    }

    [Fact]
    public async Task CleanupWithCapZeroStillKeepsUnpublishedEvents()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var taskId = await SeedTaskAsync(factory);
        var published = Guid.NewGuid();
        var unpublished = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var db = database.CreateContext())
        {
            db.TaskEvents.AddRange(
                new TaskEvent(published, taskId, now.AddMinutes(-30)),
                new TaskEvent(unpublished, taskId, now.AddMinutes(-30)));
            await db.SaveChangesAsync();
            await db.TaskEvents.Where(item => item.EventId == published)
                .ExecuteUpdateAsync(setter => setter.SetProperty(item => item.PublishedAtUtc, now.AddMinutes(-29)));
        }

        var store = new TaskEventRetentionStore(factory);
        var runner = new TaskEventRetentionRunner(
            store,
            new FrozenTime(now),
            new(TimeSpan.FromDays(1), 0, TimeSpan.FromHours(1), 5_000, 500, TimeSpan.FromHours(2)));
        await runner.RunOnceAsync();

        await using var verify = database.CreateContext();
        Assert.False(await verify.TaskEvents.AnyAsync(item => item.EventId == published));
        Assert.True(await verify.TaskEvents.AnyAsync(item => item.EventId == unpublished));
    }

    [Fact]
    public async Task WindowAndCapDeletesCarryTheirDistinctFinalPredicates()
    {
        using var provider = database.CreateServiceProvider();
        var seedFactory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var taskId = await SeedTaskAsync(seedFactory);
        var now = DateTimeOffset.UtcNow;
        var expired = Guid.NewGuid();
        var recent = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            await db.TaskEvents.Where(item => item.TaskId == taskId).ExecuteDeleteAsync();
            db.TaskEvents.AddRange(
                new TaskEvent(expired, taskId, now.AddDays(-40)),
                new TaskEvent(recent, taskId, now.AddMinutes(-30)));
            await db.SaveChangesAsync();
            await db.TaskEvents.ExecuteUpdateAsync(setter => setter.SetProperty(
                item => item.PublishedAtUtc,
                now.AddMinutes(-1)));
        }

        var commands = new DeleteCommandCaptureInterceptor();
        var store = new TaskEventRetentionStore(new InterceptingFactory(database, commands));

        Assert.Equal(1, (await store.DeleteExpiredBatchAsync(now.AddDays(-30), 10)).DeletedCount);
        Assert.Equal(1, (await store.DeleteOldestPublishedBatchAsync(10)).DeletedCount);

        Assert.Collection(
            commands.CommandTexts,
            windowDelete =>
            {
                Assert.Contains("[EventId]", windowDelete, StringComparison.Ordinal);
                Assert.Contains("[PublishedAtUtc] IS NOT NULL", windowDelete, StringComparison.Ordinal);
                Assert.Contains("[OccurredAtUtc] <", windowDelete, StringComparison.Ordinal);
                Assert.Contains("cutoffUtc", windowDelete, StringComparison.Ordinal);
            },
            capDelete =>
            {
                Assert.Contains("[EventId]", capDelete, StringComparison.Ordinal);
                Assert.Contains("[PublishedAtUtc] IS NOT NULL", capDelete, StringComparison.Ordinal);
                Assert.DoesNotContain("[OccurredAtUtc] <", capDelete, StringComparison.Ordinal);
                Assert.DoesNotContain("cutoffUtc", capDelete, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task ConcurrentAcknowledgeNeverCausesUnpublishedDeletion()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var taskId = await SeedTaskAsync(factory);
        var now = DateTimeOffset.UtcNow;
        var acknowledged = new Guid[10];
        var untouched = new Guid[10];
        for (var index = 0; index < 10; index++)
        {
            acknowledged[index] = Guid.NewGuid();
            untouched[index] = Guid.NewGuid();
        }

        await using (var db = database.CreateContext())
        {
            db.TaskEvents.AddRange(
                acknowledged.Select(id => new TaskEvent(id, taskId, now.AddDays(-40)))
                    .Concat(untouched.Select(id => new TaskEvent(id, taskId, now.AddDays(-40)))));
            await db.SaveChangesAsync();
        }

        var store = new TaskEventRetentionStore(factory);
        var events = new TaskEventStore(factory);
        var runner = new TaskEventRetentionRunner(
            store,
            new FrozenTime(now),
            new(TimeSpan.FromDays(30), 200_000, TimeSpan.FromHours(1), 5_000, 500, TimeSpan.FromHours(2)));
        await Task.WhenAll(
            runner.RunOnceAsync(),
            events.AcknowledgeAsync(acknowledged, CancellationToken.None),
            runner.RunOnceAsync(),
            events.AcknowledgeAsync(acknowledged, CancellationToken.None));

        await using var verify = database.CreateContext();
        var survivors = await verify.TaskEvents
            .Where(item => untouched.Contains(item.EventId))
            .Select(item => item.EventId)
            .ToArrayAsync();
        Assert.Equal(untouched.Length, survivors.Length);
        Assert.Equal(untouched.Order(), survivors.Order());
    }

    [Fact]
    public async Task ContextGuardStillRejectsTrackedTaskEventDeletion()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var taskId = await SeedTaskAsync(factory);
        var eventId = Guid.NewGuid();
        await using (var seed = database.CreateContext())
        {
            seed.TaskEvents.Add(new TaskEvent(eventId, taskId, DateTimeOffset.UtcNow.AddDays(-40)));
            await seed.SaveChangesAsync();
        }

        await using var db = database.CreateContext();
        db.TaskEvents.Remove(await db.TaskEvents.SingleAsync(item => item.EventId == eventId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void RelayAndRetentionIndexesCoexistInModel()
    {
        using var db = database.CreateContext();
        var indexes = db.Model.FindEntityType(typeof(TaskEvent))!.GetIndexes()
            .Where(index => index.GetDatabaseName() is not null)
            .ToDictionary(index => index.GetDatabaseName()!, index => index.GetFilter());

        Assert.Equal("[PublishedAtUtc] IS NULL", indexes["IX_TaskEvents_OccurredAtUtc_EventId"]);
        Assert.Equal("[PublishedAtUtc] IS NOT NULL", indexes["IX_TaskEvents_Published_OccurredAtUtc_EventId"]);
    }

    private sealed class FrozenTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class InterceptingFactory(
        PlatformDatabaseSqlServerFixture database,
        IInterceptor interceptor) : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => database.CreateContext(interceptor);

        public Task<PlatformDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class DeleteCommandCaptureInterceptor : DbCommandInterceptor
    {
        public List<string> CommandTexts { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                CommandTexts.Add(command.CommandText);
            }

            return ValueTask.FromResult(result);
        }
    }

    private async Task<Guid> SeedTaskAsync(IDbContextFactory<PlatformDbContext> factory)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var actor = new AdminSession(Guid.NewGuid(), $"admin-{suffix}", $"stamp-{suffix}");
        var file = new CredentialReference(
            Guid.NewGuid(),
            $"smb-{suffix}",
            CredentialKind.SmbPassword,
            "synthetic",
            "synthetic-cipher",
            "dp-smb-password-v1");
        var sql = new CredentialReference(
            Guid.NewGuid(),
            $"sql-{suffix}",
            CredentialKind.SqlPassword,
            "synthetic",
            "synthetic-cipher",
            "dp-sql-password-v1");
        var server = new DatabaseServer(
            Guid.NewGuid(),
            $"server-{suffix}",
            @"D:\Synthetic",
            new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", file.Id, null));
        var instance = new DatabaseInstance(
            Guid.NewGuid(),
            server.Id,
            "instance",
            $"synthetic-{suffix}",
            sql.Id,
            true,
            false,
            null,
            15);
        instance.RecordConnectionSucceeded(DateTimeOffset.UtcNow, "15.0.synthetic", null, null);
        var managed = new ManagedDatabase(Guid.NewGuid(), instance.Id, $"db-{suffix}", false, true, DateTimeOffset.UtcNow);
        managed.SetManaged(true);
        var policy = new BackupPolicy(
            Guid.NewGuid(),
            $"policy-{suffix}",
            managed.Id,
            new(
                BackupStorageMode.LocalOnly,
                null,
                BackupScheduleType.Daily,
                TimeOnly.MinValue,
                BackupWeekdays.None,
                "UTC",
                7,
                null,
                true,
                false,
                true,
                120,
                60,
                60),
            isEnabled: true,
            isManualOnly: true);
        await using var db = database.CreateContext();
        db.AddRange(
            file,
            sql,
            server,
            instance,
            managed,
            policy,
            new AdminUser(
                actor.AdminUserId,
                actor.Username,
                actor.Username.ToUpperInvariant(),
                "synthetic-hash",
                actor.SecurityStamp));
        await db.SaveChangesAsync();
        var taskId = Guid.NewGuid();
        var created = await new BackupTaskExecutionStore(factory).CreateTaskAsync(new(
            taskId,
            policy.Id,
            BackupTaskTriggerType.Manual,
            null,
            taskId,
            DateTimeOffset.UtcNow,
            actor.AdminUserId,
            actor.SecurityStamp));
        Assert.True(created.IsSucceeded);
        var events = new TaskEventStore(factory);
        var existing = await events.ReadPendingAsync(100, CancellationToken.None);
        if (existing.Count > 0)
        {
            await events.AcknowledgeAsync([.. existing.Select(item => item.EventId)], CancellationToken.None);
        }

        return taskId;
    }
}
