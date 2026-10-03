using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class PlatformDbContextModelTests
{
    [Fact]
    public void ModelContainsInitialEntitiesAndSqlServerGuards()
    {
        using var context = CreateContext();

        var adminType = context.Model.FindEntityType(typeof(AdminUser));
        var auditType = context.Model.FindEntityType(typeof(AuditRecord));

        Assert.NotNull(adminType);
        Assert.NotNull(auditType);

        var normalizedIndex = Assert.Single(
            adminType!.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AdminUser.NormalizedUsername)]));
        var actorForeignKey = Assert.Single(auditType!.GetForeignKeys());
        var rowVersion = adminType.FindProperty(nameof(AdminUser.RowVersion));

        Assert.True(normalizedIndex.IsUnique);
        Assert.NotNull(rowVersion);
        Assert.True(rowVersion!.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, rowVersion.ValueGenerated);
        Assert.Equal(DeleteBehavior.Restrict, actorForeignKey.DeleteBehavior);
    }

    [Fact]
    public void GeneratedCreateScriptContainsExpectedTablesAndConstraints()
    {
        using var context = CreateContext();

        var script = context.Database.GenerateCreateScript();

        Assert.Contains("CREATE TABLE [AdminUsers]", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [AuditRecords]", script, StringComparison.Ordinal);
        Assert.Contains("[RowVersion] rowversion", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UX_AdminUsers_NormalizedUsername", script, StringComparison.Ordinal);
        Assert.Contains("ON DELETE NO ACTION", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelContainsConfigurationEntitiesWithConcurrencyAndRestrictedRelationships()
    {
        using var context = CreateContext();
        Type[] entityTypes =
        [
            typeof(CredentialReference),
            typeof(DatabaseServer),
            typeof(DatabaseInstance),
            typeof(ManagedDatabase),
            typeof(StorageTarget),
            typeof(BackupPolicy),
        ];

        foreach (var entityType in entityTypes)
        {
            var modelType = Assert.IsAssignableFrom<IEntityType>(
                context.Model.FindEntityType(entityType));
            var rowVersion = Assert.IsAssignableFrom<IProperty>(
                modelType.FindProperty(nameof(ConcurrentEntity.RowVersion)));

            Assert.True(rowVersion.IsConcurrencyToken);
            Assert.Equal(ValueGenerated.OnAddOrUpdate, rowVersion.ValueGenerated);
            Assert.All(
                modelType.GetForeignKeys(),
                foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
        }
    }

    [Fact]
    public void GeneratedCreateScriptContainsConfigurationGuards()
    {
        using var context = CreateContext();

        var script = context.Database.GenerateCreateScript();

        Assert.Contains("CREATE TABLE [CredentialReferences]", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [DatabaseServers]", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [DatabaseInstances]", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [ManagedDatabases]", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [StorageTargets]", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [BackupPolicies]", script, StringComparison.Ordinal);
        Assert.Contains("CK_DatabaseInstances_EncryptConnection", script, StringComparison.Ordinal);
        Assert.Contains("CK_StorageTargets_Endpoint", script, StringComparison.Ordinal);
        Assert.Contains("CK_BackupPolicies_Storage", script, StringComparison.Ordinal);
        Assert.Contains("CK_BackupPolicies_ScheduleEffectiveFrom", script, StringComparison.Ordinal);
        Assert.Contains(
            "UX_BackupPolicies_DatabaseId_BackupType_Enabled",
            script,
            StringComparison.Ordinal);
        Assert.Contains("WHERE [IsEnabled] = 1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[Password]", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[PrivateKey]", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelContainsBackupTaskPersistenceBoundaries()
    {
        using var context = CreateContext();
        Type[] entityTypes =
        [
            typeof(BackupTask),
            typeof(BackupTaskSnapshot),
            typeof(BackupAttempt),
            typeof(BackupTaskStateChange),
        ];

        foreach (var entityType in entityTypes)
        {
            var modelType = Assert.IsAssignableFrom<IEntityType>(
                context.Model.FindEntityType(entityType));

            Assert.All(
                modelType.GetForeignKeys(),
                foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
        }

        foreach (var concurrentType in new[] { typeof(BackupTask), typeof(BackupAttempt) })
        {
            var modelType = Assert.IsAssignableFrom<IEntityType>(
                context.Model.FindEntityType(concurrentType));
            var rowVersion = Assert.IsAssignableFrom<IProperty>(
                modelType.FindProperty(nameof(ConcurrentEntity.RowVersion)));

            Assert.True(rowVersion.IsConcurrencyToken);
            Assert.Equal(ValueGenerated.OnAddOrUpdate, rowVersion.ValueGenerated);
        }

        var attemptType = Assert.IsAssignableFrom<IEntityType>(
            context.Model.FindEntityType(typeof(BackupAttempt)));
        Assert.Contains(
            attemptType.GetKeys(),
            key => key.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(BackupAttempt.TaskId), nameof(BackupAttempt.Id)]));
    }

    [Fact]
    public void GeneratedCreateScriptContainsBackupTaskGuardsAndNoSecretColumns()
    {
        using var context = CreateContext();

        var script = context.Database.GenerateCreateScript();

        Assert.Contains("CREATE TABLE [BackupTasks]", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [BackupTaskSnapshots]", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [BackupAttempts]", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [BackupTaskStateChanges]", script, StringComparison.Ordinal);
        Assert.Contains("UX_BackupTasks_PolicyId_ScheduledSlotAtUtc", script, StringComparison.Ordinal);
        Assert.Contains("UX_BackupTaskStateChanges_MutationId", script, StringComparison.Ordinal);
        Assert.Contains("CK_BackupTasks_LeaseState", script, StringComparison.Ordinal);
        Assert.Contains("CK_BackupTasks_ReconciliationSchedule", script, StringComparison.Ordinal);
        Assert.Contains("IX_BackupTasks_ReconciliationQueue", script, StringComparison.Ordinal);
        // P6.3：未发布读取与已发布清理是两个键相同、过滤相反的筛选索引，两者都必须留在幂等脚本里。
        Assert.Contains("IX_TaskEvents_OccurredAtUtc_EventId", script, StringComparison.Ordinal);
        Assert.Contains("IX_TaskEvents_Published_OccurredAtUtc_EventId", script, StringComparison.Ordinal);
        Assert.Contains("CK_BackupAttempts_RemotePaths", script, StringComparison.Ordinal);
        Assert.Contains("HASHBYTES('SHA2_256'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[Password]", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[PrivateKey]", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[LeaseToken]", GetCreateTable(script, "BackupTaskStateChanges"), StringComparison.Ordinal);
    }

    [Fact]
    public void ModelContainsBackupFileConcurrencyAndRestrictedRelationships()
    {
        using var context = CreateContext();
        var fileType = Assert.IsAssignableFrom<IEntityType>(
            context.Model.FindEntityType(typeof(BackupFile)));
        var historyType = Assert.IsAssignableFrom<IEntityType>(
            context.Model.FindEntityType(typeof(BackupFileStateChange)));
        var rowVersion = Assert.IsAssignableFrom<IProperty>(
            fileType.FindProperty(nameof(BackupFile.RowVersion)));

        Assert.True(rowVersion.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, rowVersion.ValueGenerated);
        Assert.All(
            fileType.GetForeignKeys().Concat(historyType.GetForeignKeys()),
            foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
        Assert.Contains(
            fileType.GetIndexes(),
            index => index.IsUnique
                && index.Properties.Select(property => property.Name)
                    .SequenceEqual([nameof(BackupFile.TaskId), nameof(BackupFile.Location)]));
        Assert.Contains(
            historyType.GetIndexes(),
            index => index.IsUnique
                && index.Properties.Select(property => property.Name)
                    .SequenceEqual([nameof(BackupFileStateChange.MutationId)]));
    }

    [Fact]
    public void GeneratedCreateScriptContainsBackupFileGuardsAndNoPathInHistory()
    {
        using var context = CreateContext();
        var script = context.Database.GenerateCreateScript();
        var historyTable = GetCreateTable(script, "BackupFileStateChanges");

        Assert.Contains("CREATE TABLE [BackupFiles]", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [BackupFileStateChanges]", script, StringComparison.Ordinal);
        Assert.Contains("CK_BackupFiles_Identity", script, StringComparison.Ordinal);
        Assert.Contains("CK_BackupFiles_DeletionLease", script, StringComparison.Ordinal);
        Assert.Contains("CK_BackupFiles_DeletionOutcome", script, StringComparison.Ordinal);
        Assert.Contains("UX_BackupFiles_LocalPath", script, StringComparison.Ordinal);
        Assert.Contains("UX_BackupFiles_RemotePath", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[Path]", historyTable, StringComparison.Ordinal);
        Assert.DoesNotContain("[DeletionLeaseToken]", historyTable, StringComparison.Ordinal);
    }

    private static string GetCreateTable(string script, string tableName)
    {
        var start = script.IndexOf($"CREATE TABLE [{tableName}]", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = script.IndexOf(';', start);
        Assert.True(end > start);
        return script[start..end];
    }

    private static PlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(
                "Server=localhost;Database=DbBackupManagerModelOnly;Integrated Security=true;Encrypt=true",
                sqlServer => sqlServer.UseCompatibilityLevel(
                    PlatformDatabaseServiceCollectionExtensions.CompatibilityLevel))
            .Options;

        return new PlatformDbContext(options);
    }
}
