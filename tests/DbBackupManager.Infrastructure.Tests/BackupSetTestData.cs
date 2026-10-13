using DbBackupManager.Application.BackupSets;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

internal static class BackupSetTestData
{
    internal static async Task<BackupTaskSnapshot> HostSnapshotAsync(PlatformDatabaseSqlServerFixture database)
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
        await using var db = database.CreateContext();
        db.AddRange(source, sql, server, instance, managed);
        await db.SaveChangesAsync();
        return HostSnapshot(server, instance, managed);
    }

    internal static async Task<BackupExecutionWorkItem> WorkAsync(
        PlatformDatabaseSqlServerFixture database, Guid? databaseId = null)
    {
        var host = databaseId is null
            ? await HostSnapshotAsync(database)
            : await LoadHostAsync(database, databaseId.Value);
        var now = DateTimeOffset.UtcNow;
        await using var db = database.CreateContext();
        var versionId = await db.BackupPlans.AsNoTracking()
            .Where(plan => plan.DatabaseId == host.DatabaseId)
            .Select(plan => EF.Property<Guid?>(plan, "_currentVersionId"))
            .SingleOrDefaultAsync();
        BackupPlanVersion version;
        string planName;
        if (versionId is null)
        {
            var definition = new BackupPlanDefinition(BackupPlanMode.Full,
                new(BackupScheduleType.Daily, new(2, 0), BackupWeekdays.None),
                null, null, "UTC", BackupStorageMode.LocalOnly, null, 7, null, true, true, 120, 60, 180);
            var plan = BackupPlan.Create(Guid.NewGuid(), host.DatabaseId, $"MetadataPlan-{Guid.NewGuid():N}",
                Guid.NewGuid(), definition, now);
            planName = plan.Name;
            using var provider = database.CreateServiceProvider();
            await new BackupPlanStore(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>()).AddAsync(plan);
            version = await db.BackupPlanVersions.SingleAsync(item => item.PlanId == plan.Id);
        }
        else
        {
            version = await db.BackupPlanVersions.SingleAsync(item => item.Id == versionId);
            planName = await db.BackupPlans.Where(plan => plan.Id == version.PlanId).Select(plan => plan.Name).SingleAsync();
        }

        var task = BackupTask.ForPlan(Guid.NewGuid(), version.PlanId, version.Id, BackupRunPurpose.PlanFull,
            BackupTaskTriggerType.Manual, null);
        var snapshot = BackupTaskSnapshot.ForPlan(task, version, planName, BackupRunPurpose.PlanFull,
            new(host.ServerId, host.ServerName, host.InstanceId, host.InstanceName, host.DatabaseId, host.DatabaseName),
            new(host.ConnectionAddress, host.SqlCredentialReferenceId, host.EncryptConnection, host.TrustServerCertificate,
                host.CertificateTrustReason, host.ConnectionTimeoutSeconds, host.AllowLegacyTls, host.LegacyTlsReason),
            new(host.LocalSqlBackupRootPath, "v3", new(host.SourceAccessProtocol, host.SourceAccessHost, host.SourceAccessPort,
                host.SourceAccessBasePath, host.SourceCredentialReferenceId, host.SourceSftpHostKeyFingerprint)));
        db.AddRange(task, snapshot);
        await db.SaveChangesAsync();
        var attemptId = Guid.NewGuid();
        var attempt = new BackupAttempt(attemptId, task.Id, 1, now,
            new($@"D:\Synthetic\Backup\{attemptId:N}_FULL.bak",
                $@"\\synthetic-host\synthetic-share\{attemptId:N}_FULL.bak", null, null, null));
        attempt.BindSqlIdentity(Guid.NewGuid(), Guid.NewGuid());
        task.ClaimExecution(BackupStorageMode.LocalOnly, attemptId, Guid.NewGuid(), "合成核对测试", now, now.AddMinutes(10));
        db.Add(attempt);
        await db.SaveChangesAsync();
        return new(task.ToStateModel(), snapshot.ToSnapshotModel(), attempt.ToAttemptModel(), task.ToLeaseHandle());
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

    private static async Task<BackupTaskSnapshot> LoadHostAsync(PlatformDatabaseSqlServerFixture database, Guid databaseId)
    {
        await using var db = database.CreateContext();
        var managed = await db.ManagedDatabases.AsNoTracking().SingleAsync(item => item.Id == databaseId);
        var instance = await db.DatabaseInstances.AsNoTracking().SingleAsync(item => item.Id == managed.InstanceId);
        var server = await db.DatabaseServers.AsNoTracking().SingleAsync(item => item.Id == instance.ServerId);
        return HostSnapshot(server, instance, managed);
    }

    private static BackupTaskSnapshot HostSnapshot(DatabaseServer server, DatabaseInstance instance, ManagedDatabase managed) =>
        new(Guid.NewGuid(), "合成主机",
            new(server.Id, server.Name, instance.Id, instance.Name, managed.Id, managed.DatabaseName),
            new(instance.ConnectionAddress, instance.SqlCredentialReferenceId, instance.EncryptConnection,
                instance.TrustServerCertificate, instance.CertificateTrustReason, instance.ConnectionTimeoutSeconds,
                instance.AllowLegacyTls, instance.LegacyTlsReason),
            new(server.LocalBackupRootPath, "v1", new(server.StagingAccessProtocol, server.StagingAccessHost,
                server.StagingAccessPort, server.StagingAccessBasePath, server.StagingCredentialReferenceId,
                server.StagingSftpHostKeyFingerprint)),
            new(BackupStorageMode.LocalOnly, null, 7, null, true, false, false, 120, 60, 180, "UTC"));
}
