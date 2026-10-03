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

public sealed class BackupManagementRemotePolicyTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Fact]
    public async Task RemoteManualPolicyRequiresEnabledTargetAndFreezesSnapshot()
    {
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new BackupTaskExecutionStore(factory);
        var manager = new BackupManagementService(factory, store);
        var seed = await SeedAsync();

        Assert.Equal(BackupManagementCode.Invalid, (await manager.SavePolicyAsync(seed.Actor, null, null,
            new(seed.DatabaseId, "local-with-target", 7, 120, 60, false, true, 1, seed.TargetId, 14))).Code);
        Assert.Equal(BackupManagementCode.Invalid, (await manager.SavePolicyAsync(seed.Actor, null, null,
            new(seed.DatabaseId, "remote-missing-target", 7, 120, 60, false, true, 2, null, 14))).Code);
        Assert.Equal(BackupManagementCode.Invalid, (await manager.SavePolicyAsync(seed.Actor, null, null,
            new(seed.DatabaseId, "remote-disabled-target", 7, 120, 60, false, true, 2, seed.DisabledTargetId, 14))).Code);

        var saved = await manager.SavePolicyAsync(seed.Actor, null, null,
            new(seed.DatabaseId, $"remote-{Guid.NewGuid():N}", 7, 120, 60, false, true, 2, seed.TargetId, 14, 90));
        Assert.Equal(BackupManagementCode.Succeeded, saved.Code);
        Assert.Equal(2, saved.Value!.Settings.StorageMode);
        Assert.Equal(seed.TargetId, saved.Value.Settings.StorageTargetId);
        Assert.Equal(14, saved.Value.Settings.RemoteRetentionDays);
        Assert.Equal(90, saved.Value.Settings.TransferTimeoutMinutes);

        var requestId = Guid.NewGuid();
        var started = await manager.StartAsync(seed.Actor, saved.Value.Id, requestId);
        Assert.Equal(BackupManagementCode.Succeeded, started.Code);
        await using var snapshot = await factory.CreateDbContextAsync();
        var frozen = await snapshot.BackupTaskSnapshots.SingleAsync(x => x.TaskId == requestId);
        Assert.Equal(BackupStorageMode.LocalAndRemote, frozen.StorageMode);
        Assert.Equal(seed.TargetId, frozen.StorageTargetId);
        Assert.Equal(FileTransferProtocol.Smb, frozen.RemoteProtocol);
        await using var db = database.CreateContext();
        Assert.True(await db.AuditRecords.AnyAsync(x => x.Action == "backup.manual_policy.save"
            && x.TargetId == saved.Value.Id.ToString("N")));
    }

    private async Task<(AdminSession Actor, Guid DatabaseId, Guid TargetId, Guid DisabledTargetId)> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var actor = new AdminSession(Guid.NewGuid(), $"admin-{suffix}", $"stamp-{suffix}");
        var file = new CredentialReference(Guid.NewGuid(), $"smb-{suffix}", CredentialKind.SmbPassword, "synthetic", "synthetic-cipher", "dp-smb-password-v1");
        var sql = new CredentialReference(Guid.NewGuid(), $"sql-{suffix}", CredentialKind.SqlPassword, "synthetic", "synthetic-cipher", "dp-sql-password-v1");
        var target = new StorageTarget(Guid.NewGuid(), $"target-{suffix}",
            new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", file.Id, null));
        var disabled = new StorageTarget(Guid.NewGuid(), $"disabled-{suffix}",
            new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share-disabled", file.Id, null), false);
        var server = new DatabaseServer(Guid.NewGuid(), $"server-{suffix}", @"D:\Synthetic",
            new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", file.Id, null));
        var instance = new DatabaseInstance(Guid.NewGuid(), server.Id, "instance", $"synthetic-{suffix}", sql.Id, true, false, null, 15);
        instance.RecordConnectionSucceeded(DateTimeOffset.UtcNow, "15.0.synthetic", null, null);
        var managed = new ManagedDatabase(Guid.NewGuid(), instance.Id, $"db-{suffix}", false, true, DateTimeOffset.UtcNow);
        managed.SetManaged(true);
        await using var db = database.CreateContext();
        db.AddRange(file, sql, target, disabled, server, instance, managed,
            new AdminUser(actor.AdminUserId, actor.Username, actor.Username.ToUpperInvariant(), "synthetic-hash", actor.SecurityStamp));
        await db.SaveChangesAsync();
        return (actor, managed.Id, target.Id, disabled.Id);
    }
}
