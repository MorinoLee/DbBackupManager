using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class PlatformDatabaseSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Fact]
    public async Task InitialMigrationCreatesSchemaAtCompatibilityLevel150()
    {
        await using var context = database.CreateContext();

        var appliedMigrations = await context.Database.GetAppliedMigrationsAsync();
        var compatibilityLevel = await context.Database
            .SqlQueryRaw<int>(
                "SELECT CAST([compatibility_level] AS int) AS [Value] FROM [sys].[databases] WHERE [name] = DB_NAME()")
            .SingleAsync();

        var appliedMigration = Assert.Single(appliedMigrations);
        Assert.EndsWith("_InitialPlatformSchema", appliedMigration, StringComparison.Ordinal);
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal(PlatformDatabaseServiceCollectionExtensions.CompatibilityLevel, compatibilityLevel);
    }

    [Fact]
    public async Task ConfigurationGraphRoundTripsWithProtectedCredentialValues()
    {
        var graph = await AddConfigurationGraphAsync();

        await using var context = database.CreateContext();
        var credential = await context.CredentialReferences
            .AsNoTracking()
            .SingleAsync(x => x.Id == graph.SqlCredentialId);
        var instance = await context.DatabaseInstances
            .AsNoTracking()
            .SingleAsync(x => x.Id == graph.InstanceId);
        var policy = await context.BackupPolicies
            .AsNoTracking()
            .SingleAsync(x => x.Id == graph.PolicyId);

        Assert.Equal("protected:synthetic-sql-secret", credential.ProtectedSecret);
        Assert.Equal("dp-v1", credential.ProtectionVersion);
        Assert.True(instance.EncryptConnection);
        Assert.False(instance.TrustServerCertificate);
        Assert.Equal(BackupStorageMode.LocalAndRemote, policy.StorageMode);
        Assert.NotEmpty(credential.RowVersion);
        Assert.NotEmpty(instance.RowVersion);
        Assert.NotEmpty(policy.RowVersion);
    }

    [Fact]
    public async Task OnlyOneEnabledFullPolicyPerDatabaseIsAllowed()
    {
        var graph = await AddConfigurationGraphAsync();
        await using var context = database.CreateContext();
        context.BackupPolicies.Add(new BackupPolicy(
            Guid.NewGuid(),
            $"重复启用策略-{Guid.NewGuid():N}",
            graph.DatabaseId,
            CreatePolicySettings(graph.StorageTargetId),
            isEnabled: true,
            nowUtc: DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task RowVersionRejectsStaleCredentialReferenceUpdate()
    {
        var graph = await AddConfigurationGraphAsync();
        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var firstCopy = await first.CredentialReferences
            .AsTracking()
            .SingleAsync(x => x.Id == graph.SqlCredentialId);
        var staleCopy = await second.CredentialReferences
            .AsTracking()
            .SingleAsync(x => x.Id == graph.SqlCredentialId);

        firstCopy.Rename($"第一轮更新-{Guid.NewGuid():N}");
        await first.SaveChangesAsync();
        staleCopy.Rename($"过期更新-{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task ReferencedCredentialCannotBeDeleted()
    {
        var graph = await AddConfigurationGraphAsync();
        await using var context = database.CreateContext();

        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            context.CredentialReferences
                .Where(x => x.Id == graph.SqlCredentialId)
                .ExecuteDeleteAsync());

        Assert.Equal(547, exception.Number);
    }

    [Fact]
    public async Task DatabaseConstraintRejectsDisablingSqlTransportEncryption()
    {
        var graph = await AddConfigurationGraphAsync();
        await using var context = database.CreateContext();

        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [DatabaseInstances] SET [EncryptConnection] = 0 WHERE [Id] = {graph.InstanceId}"));

        Assert.Equal(547, exception.Number);
    }

    [Fact]
    public async Task RowVersionRejectsStaleAdminUpdate()
    {
        var user = CreateUser("并发管理员", "CONCURRENT_ADMIN");

        await using (var setup = database.CreateContext())
        {
            setup.AdminUsers.Add(user);
            await setup.SaveChangesAsync();
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var firstCopy = await first.AdminUsers.AsTracking().SingleAsync(x => x.Id == user.Id);
        var staleCopy = await second.AdminUsers.AsTracking().SingleAsync(x => x.Id == user.Id);

        first.Entry(firstCopy).Property(x => x.IsEnabled).CurrentValue = false;
        await first.SaveChangesAsync();
        second.Entry(staleCopy).Property(x => x.IsEnabled).CurrentValue = false;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task NormalizedUsernameIsUniqueInDatabase()
    {
        await using var context = database.CreateContext();
        context.AdminUsers.Add(CreateUser("首位管理员", "DUPLICATE_ADMIN"));
        context.AdminUsers.Add(CreateUser("第二位管理员", "DUPLICATE_ADMIN"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task AuditRecordIsTimestampedAndCannotBeChanged()
    {
        var actor = CreateUser("审计管理员", "AUDIT_ADMIN");
        var record = new AuditRecord(
            actor.Id,
            "admin.created",
            "AdminUser",
            actor.Id.ToString("N"),
            "succeeded",
            null);

        await using (var setup = database.CreateContext())
        {
            setup.AdminUsers.Add(actor);
            setup.AuditRecords.Add(record);
            await setup.SaveChangesAsync();
        }

        Assert.NotEqual(default, record.OccurredAtUtc);

        await using var context = database.CreateContext();
        var stored = await context.AuditRecords.AsTracking().SingleAsync(x => x.Id == record.Id);
        context.Entry(stored).Property(x => x.Result).CurrentValue = "failed";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains("只能追加", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditForeignKeyRestrictsAdminDeletionInDatabase()
    {
        var actor = CreateUser("外键管理员", "FOREIGN_KEY_ADMIN");
        var record = new AuditRecord(
            actor.Id,
            "admin.created",
            "AdminUser",
            actor.Id.ToString("N"),
            "succeeded",
            null);

        await using (var setup = database.CreateContext())
        {
            setup.AdminUsers.Add(actor);
            setup.AuditRecords.Add(record);
            await setup.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            context.AdminUsers.Where(x => x.Id == actor.Id).ExecuteDeleteAsync());

        Assert.Equal(547, exception.Number);
    }

    [Fact]
    public async Task ConcurrentFirstAdminSetupCreatesExactlyOneAdminAndAudit()
    {
        await database.ClearIdentityDataAsync();
        using var provider = database.CreateServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var firstStore = firstScope.ServiceProvider.GetRequiredService<IAdminIdentityStore>();
        var secondStore = secondScope.ServiceProvider.GetRequiredService<IAdminIdentityStore>();
        var firstCredential = CreateCredential("并发设置一", "SETUP_ONE");
        var secondCredential = CreateCredential("并发设置二", "SETUP_TWO");

        var results = await Task.WhenAll(
            firstStore.TryCreateFirstAdminAsync(firstCredential),
            secondStore.TryCreateFirstAdminAsync(secondCredential));

        Assert.Single(results, result => result == AdminStoreResult.Succeeded);
        Assert.Single(results, result => result == AdminStoreResult.SetupUnavailable);

        await using var context = database.CreateContext();
        Assert.Equal(1, await context.AdminUsers.CountAsync());
        Assert.Equal(1, await context.AuditRecords.CountAsync());
    }

    private static AdminUser CreateUser(string username, string normalizedUsername)
    {
        return new AdminUser(
            Guid.NewGuid(),
            username,
            normalizedUsername,
            "synthetic-password-hash",
            Guid.NewGuid().ToString("N"));
    }

    private static NewAdminCredential CreateCredential(string username, string normalizedUsername)
    {
        return new NewAdminCredential(
            Guid.NewGuid(),
            username,
            normalizedUsername,
            "synthetic-password-hash",
            Guid.NewGuid().ToString("N"));
    }

    private async Task<ConfigurationGraphIds> AddConfigurationGraphAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stagingCredential = new CredentialReference(
            Guid.NewGuid(),
            $"合成暂存凭据-{suffix}",
            CredentialKind.SmbPassword,
            "synthetic-file-user",
            "protected:synthetic-staging-secret",
            "dp-v1");
        var sqlCredential = new CredentialReference(
            Guid.NewGuid(),
            $"合成 SQL 凭据-{suffix}",
            CredentialKind.SqlPassword,
            "synthetic-sql-user",
            "protected:synthetic-sql-secret",
            "dp-v1");
        var remoteCredential = new CredentialReference(
            Guid.NewGuid(),
            $"合成远程凭据-{suffix}",
            CredentialKind.SftpPassword,
            "synthetic-transfer-user",
            "protected:synthetic-transfer-secret",
            "dp-v1");
        var server = new DatabaseServer(
            Guid.NewGuid(),
            $"合成服务器-{suffix}",
            "synthetic-sql-local-root",
            new FileEndpointSettings(
                FileTransferProtocol.Smb,
                "synthetic-smb-host",
                null,
                "synthetic-share",
                stagingCredential.Id,
                null));
        var instance = new DatabaseInstance(
            Guid.NewGuid(),
            server.Id,
            $"合成实例-{suffix}",
            $"synthetic-sql-host-{suffix}",
            sqlCredential.Id,
            encryptConnection: true,
            trustServerCertificate: false,
            certificateTrustReason: null,
            connectionTimeoutSeconds: 30);
        var managedDatabase = new ManagedDatabase(
            Guid.NewGuid(),
            instance.Id,
            $"SyntheticDatabase_{suffix}",
            isSystemDatabase: false,
            isAvailable: true,
            DateTimeOffset.UtcNow,
            "FULL",
            "ONLINE");
        managedDatabase.SetManaged(true);
        var storageTarget = new StorageTarget(
            Guid.NewGuid(),
            $"合成存储目标-{suffix}",
            new FileEndpointSettings(
                FileTransferProtocol.Sftp,
                "synthetic-sftp-host",
                22,
                "/synthetic/root",
                remoteCredential.Id,
                "SHA256:synthetic-host-key"));
        var policy = new BackupPolicy(
            Guid.NewGuid(),
            $"合成备份策略-{suffix}",
            managedDatabase.Id,
            CreatePolicySettings(storageTarget.Id),
            isEnabled: true,
            nowUtc: DateTimeOffset.UtcNow);

        await using var context = database.CreateContext();
        context.AddRange(
            stagingCredential,
            sqlCredential,
            remoteCredential,
            server,
            instance,
            managedDatabase,
            storageTarget,
            policy);
        await context.SaveChangesAsync();

        return new ConfigurationGraphIds(
            sqlCredential.Id,
            instance.Id,
            managedDatabase.Id,
            storageTarget.Id,
            policy.Id);
    }

    private static BackupPolicySettings CreatePolicySettings(Guid storageTargetId)
    {
        return new BackupPolicySettings(
            BackupStorageMode.LocalAndRemote,
            storageTargetId,
            BackupScheduleType.Weekly,
            new TimeOnly(2, 0),
            BackupWeekdays.Monday | BackupWeekdays.Thursday,
            "Taipei Standard Time",
            LocalRetentionDays: 7,
            RemoteRetentionDays: 30,
            UseChecksum: true,
            UseCompression: true,
            UseCopyOnly: false,
            BackupTimeoutMinutes: 120,
            VerifyTimeoutMinutes: 60,
            TransferTimeoutMinutes: 180);
    }

    private sealed record ConfigurationGraphIds(
        Guid SqlCredentialId,
        Guid InstanceId,
        Guid DatabaseId,
        Guid StorageTargetId,
        Guid PolicyId);
}
