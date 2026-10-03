using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.BackupManagement;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class TaskEventStoreSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => database.ClearBackupTaskDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReadPendingRespectsTakeAndStableOrder()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var seed = await SeedTaskAsync(factory);
        var now = DateTimeOffset.UtcNow;
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            db.TaskEvents.AddRange(
                new TaskEvent(newer, seed, now.AddMinutes(-1)),
                new TaskEvent(older, seed, now.AddMinutes(-5)));
            await db.SaveChangesAsync();
        }

        var store = new TaskEventStore(factory);
        var first = await store.ReadPendingAsync(1, CancellationToken.None);

        Assert.Equal(older, Assert.Single(first).EventId);
        Assert.Equal(2, (await store.ReadPendingAsync(100, CancellationToken.None)).Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadPendingAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadPendingAsync(10_001));
    }

    [Fact]
    public async Task AcknowledgeMarksOnlyUnpublishedAndLeavesLateEvents()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var seed = await SeedTaskAsync(factory);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            db.TaskEvents.AddRange(
                new TaskEvent(firstId, seed, DateTimeOffset.UtcNow.AddMinutes(-2)),
                new TaskEvent(secondId, seed, DateTimeOffset.UtcNow.AddMinutes(-1)));
            await db.SaveChangesAsync();
        }

        var store = new TaskEventStore(factory);
        await store.AcknowledgeAsync([firstId], CancellationToken.None);
        await store.AcknowledgeAsync([firstId], CancellationToken.None);
        var remaining = await store.ReadPendingAsync(100, CancellationToken.None);

        Assert.Equal(secondId, Assert.Single(remaining).EventId);
        await using var verify = database.CreateContext();
        Assert.NotNull((await verify.TaskEvents.SingleAsync(item => item.EventId == firstId)).PublishedAtUtc);
        Assert.Null((await verify.TaskEvents.SingleAsync(item => item.EventId == secondId)).PublishedAtUtc);
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
            true,
            true);
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
