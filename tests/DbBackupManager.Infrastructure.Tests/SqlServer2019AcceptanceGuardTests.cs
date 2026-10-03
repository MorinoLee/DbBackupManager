using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DbBackupManager.Infrastructure.Tests;

/// <summary>
/// P3.2 专属：连接安全边界与幂等 Migration SQL 关键内容检查（无需外部实例）。
/// </summary>
public sealed class SqlServer2019AcceptanceGuardTests
{
    [Fact]
    public void ConnectionGuardRejectsMissingEncryption()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SqlServer2019ConnectionGuard.ParseAndValidateConfiguration(
                "Server=localhost;Integrated Security=true;Encrypt=false;TrustServerCertificate=false"));

        Assert.Contains("Encrypt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionGuardRejectsTrustServerCertificate()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SqlServer2019ConnectionGuard.ParseAndValidateConfiguration(
                "Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true"));

        Assert.Contains("TrustServerCertificate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionGuardAcceptsEncryptedValidatedCertificateSettings()
    {
        var builder = SqlServer2019ConnectionGuard.ParseAndValidateConfiguration(
            "Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=false");

        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.False(builder.TrustServerCertificate);
    }

    [Fact]
    public void DatabaseNameGuardRejectsForeignPrefix()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SqlServer2019ConnectionGuard.EnsureAllowedDatabaseName("ProductionDb"));

        Assert.Contains(
            SqlServer2019AcceptanceConstants.SqlServer2019DatabaseNamePrefix,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void IdempotentMigrationScriptContainsExpectedGuards()
    {
        using var context = CreateDesignTimeContext();
        var migrator = context.GetService<IMigrator>();
        var script = migrator.GenerateScript(
            fromMigration: null,
            toMigration: null,
            options: MigrationsSqlGenerationOptions.Idempotent);

        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.Contains("AdminUsers", script, StringComparison.Ordinal);
        Assert.Contains("AuditRecords", script, StringComparison.Ordinal);
        Assert.Contains("UX_AdminUsers_NormalizedUsername", script, StringComparison.Ordinal);
        Assert.Contains("compatibility_level", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FailedLoginCount", script, StringComparison.Ordinal);
        Assert.Contains("FailedLoginWindowStartedAtUtc", script, StringComparison.Ordinal);
        Assert.Contains("LockoutEndUtc", script, StringComparison.Ordinal);
        Assert.Contains("CredentialReferences", script, StringComparison.Ordinal);
        Assert.Contains("DatabaseServers", script, StringComparison.Ordinal);
        Assert.Contains("DatabaseInstances", script, StringComparison.Ordinal);
        Assert.Contains("ManagedDatabases", script, StringComparison.Ordinal);
        Assert.Contains("StorageTargets", script, StringComparison.Ordinal);
        Assert.Contains("BackupPolicies", script, StringComparison.Ordinal);
        Assert.Contains("BackupTasks", script, StringComparison.Ordinal);
        Assert.Contains("BackupTaskSnapshots", script, StringComparison.Ordinal);
        Assert.Contains("BackupAttempts", script, StringComparison.Ordinal);
        Assert.Contains("BackupTaskStateChanges", script, StringComparison.Ordinal);
        Assert.Contains("BackupFiles", script, StringComparison.Ordinal);
        Assert.Contains("BackupFileStateChanges", script, StringComparison.Ordinal);
        Assert.Contains("UX_BackupFiles_LocalPath", script, StringComparison.Ordinal);
        Assert.Contains("UX_BackupTasks_PolicyId_ScheduledSlotAtUtc", script, StringComparison.Ordinal);
        Assert.Contains("UX_BackupTaskStateChanges_MutationId", script, StringComparison.Ordinal);
        Assert.Contains("CK_BackupTasks_LeaseState", script, StringComparison.Ordinal);
        Assert.Contains(
            "UX_BackupPolicies_DatabaseId_BackupType_Enabled",
            script,
            StringComparison.Ordinal);
        Assert.Contains("CK_BackupPolicies_ScheduleEffectiveFrom", script, StringComparison.Ordinal);
        Assert.Contains("IX_TaskEvents_Published_OccurredAtUtc_EventId", script, StringComparison.Ordinal);
        Assert.Contains("CK_DatabaseInstances_EncryptConnection", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwd=", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[Password]", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[PrivateKey]", script, StringComparison.OrdinalIgnoreCase);
    }

    private static PlatformDbContext CreateDesignTimeContext()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(
                "Server=localhost;Database=DbBackupManagerP32ScriptOnly;Integrated Security=true;Encrypt=true;TrustServerCertificate=false",
                sqlServer => sqlServer.UseCompatibilityLevel(
                    PlatformDatabaseServiceCollectionExtensions.CompatibilityLevel))
            .Options;

        return new PlatformDbContext(options);
    }
}

/// <summary>
/// 依赖真实 Fixture 的环境模式自检；外部模式额外核对主版本且不回显连接。
/// </summary>
public sealed class SqlServer2019AcceptanceModeTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Fact]
    public void FixtureModeMatchesEnvironmentVariablePresence()
    {
        var configured = Environment.GetEnvironmentVariable(
            SqlServer2019AcceptanceConstants.ConnectionEnvironmentVariable);
        var expectSql2019 = !string.IsNullOrWhiteSpace(configured);

        Assert.Equal(
            expectSql2019 ? PlatformDatabaseTestMode.SqlServer2019 : PlatformDatabaseTestMode.LocalDb,
            database.Mode);

        var expectedPrefix = expectSql2019
            ? SqlServer2019AcceptanceConstants.SqlServer2019DatabaseNamePrefix
            : SqlServer2019AcceptanceConstants.LocalDbDatabaseNamePrefix;
        Assert.Equal(expectedPrefix, database.DatabaseNamePrefix);
    }

    [Fact]
    public async Task ExternalModeReportsSqlServer2019MajorVersionWithoutLeakingConnection()
    {
        if (database.Mode != PlatformDatabaseTestMode.SqlServer2019)
        {
            return;
        }

        await using var context = database.CreateContext();
        await using var connection = (SqlConnection)context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        var (major, productLevel, productVersion) =
            await SqlServer2019ConnectionGuard.ReadVersionAsync(connection);

        Assert.Equal(SqlServer2019AcceptanceConstants.RequiredProductMajorVersion, major);
        Assert.False(string.IsNullOrWhiteSpace(productLevel));
        Assert.False(string.IsNullOrWhiteSpace(productVersion));
        Assert.StartsWith("15.", productVersion, StringComparison.Ordinal);
    }
}
