using DbBackupManager.Application.BackupSets;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

internal static class BackupSetTestData
{
    internal static async Task<BackupExecutionWorkItem> WorkAsync(PlatformDatabaseSqlServerFixture database, Guid? policyId = null)
    {
        if (policyId is null)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var source = new CredentialReference(Guid.NewGuid(), $"MetadataSource-{suffix}",
                CredentialKind.SmbPassword, "synthetic-user", "protected:synthetic-value", "dp-v1");
            var sql = new CredentialReference(Guid.NewGuid(), $"MetadataSql-{suffix}",
                CredentialKind.SqlPassword, "synthetic-user", "protected:synthetic-value", "dp-v1");
            var server = new DatabaseServer(Guid.NewGuid(), $"MetadataServer-{suffix}", @"D:\Synthetic\Backup",
                new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", source.Id, null));
            var instance = new DatabaseInstance(Guid.NewGuid(), server.Id, $"MetadataInstance-{suffix}",
                $"synthetic-sql-{suffix}", sql.Id, true, false, null, 30);
            var managed = new ManagedDatabase(Guid.NewGuid(), instance.Id, $"MetadataDatabase_{suffix}",
                false, true, DateTimeOffset.UtcNow, "FULL", "ONLINE");
            managed.SetManaged(true);
            var policy = new BackupPolicy(Guid.NewGuid(), $"MetadataPolicy-{suffix}", managed.Id,
                new(BackupStorageMode.LocalOnly, null, BackupScheduleType.Daily, new(2, 0), BackupWeekdays.None,
                    "Taipei Standard Time", 7, null, true, true, false, 120, 60, 180), isEnabled: true, nowUtc: DateTimeOffset.UtcNow);
            await using var db = database.CreateContext();
            db.AddRange(source, sql, server, instance, managed, policy);
            await db.SaveChangesAsync();
            policyId = policy.Id;
        }
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        var now = DateTimeOffset.UtcNow;
        var taskId = Guid.NewGuid();
        var created = await store.CreateTaskAsync(new(taskId, policyId.Value, BackupTaskTriggerType.Manual,
            null, Guid.NewGuid(), now));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, created.Code);
        var claimed = await store.ClaimTaskAsync(taskId, new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "合成核对测试", now, now.AddMinutes(10)));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, claimed.Code);
        return claimed.Value!;
    }

    internal static BackupSetMetadata Full(Guid? setGuid = null) => new()
    {
        Type = BackupMetadata.Known(BackupType.Full),
        BackupSetGuid = BackupMetadata.Known(setGuid ?? Guid.NewGuid()),
        DatabaseGuid = BackupMetadata.Known(Guid.NewGuid()),
        FamilyGuid = BackupMetadata.Known(Guid.NewGuid()),
        FirstRecoveryForkId = BackupMetadata.Known(new Guid("00000001-0000-0000-0000-000000000001")),
        RecoveryForkId = BackupMetadata.Known(new Guid("00000001-0000-0000-0000-000000000001")),
        FirstLsn = BackupMetadata.Known(new BackupLsn(1234567890123456789012345m)),
        LastLsn = BackupMetadata.Known(new BackupLsn(1234567890123456789012346m)),
        CheckpointLsn = BackupMetadata.Known(new BackupLsn(1234567890123456789012345m)),
        DatabaseBackupLsn = BackupMetadata.Known(new BackupLsn(1234567890123456789012346m)),
        ForkPointLsn = BackupMetadata.Known(new BackupLsn(1234567890123456789012345m)),
        DifferentialBaseLsn = BackupMetadata.NotApplicable<BackupLsn>(),
        DifferentialBaseGuid = BackupMetadata.NotApplicable<Guid>(),
        IsCopyOnly = BackupMetadata.Known(false),
        HasBackupChecksums = BackupMetadata.Known(true),
        IsCompressed = BackupMetadata.Known(true),
        IsDamaged = BackupMetadata.Known(false),
        IsSnapshot = BackupMetadata.Known(false),
        HasIncompleteMetadata = BackupMetadata.Known(false),
        SqlStartedLocal = BackupMetadata.Known(new DateTime(2026, 10, 7, 18, 0, 0, DateTimeKind.Unspecified)),
        SqlFinishedLocal = BackupMetadata.Known(new DateTime(2026, 10, 7, 18, 1, 0, DateTimeKind.Unspecified))
    };

    internal static RegisterBackupSetCommand Command(BackupExecutionWorkItem work, BackupSetMetadata? metadata = null) =>
        new(work.Lease, Guid.NewGuid(), work.Snapshot.Identity.DatabaseId, Guid.NewGuid(), DateTimeOffset.UtcNow,
            metadata ?? Full(), new(DateTimeOffset.UtcNow, BackupCompletionTimeSource.PlatformObserved,
                BackupCompletionTimeReason.PlatformObserved), BackupSetAssessment.NotApplicable, null, true, []);

    internal static async Task<BackupTaskStoreResult<BackupSetRegistrationResult>> RegisterAsync(
        PlatformDatabaseSqlServerFixture database, RegisterBackupSetCommand command)
    {
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IBackupSetRegistrationStore>().RegisterAsync(command);
    }
}
