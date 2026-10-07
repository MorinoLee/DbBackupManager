using System.Data;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.Data.SqlClient;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class TargetSqlCommandFactoryTests
{
    [Theory]
    [InlineData(BackupRunPurpose.PlanFull, "WITH CHECKSUM, COMPRESSION;")]
    [InlineData(BackupRunPurpose.PlanDifferential, "WITH DIFFERENTIAL, CHECKSUM, COMPRESSION;")]
    [InlineData(BackupRunPurpose.AdHocCopyOnlyFull, "WITH COPY_ONLY, CHECKSUM, COMPRESSION;")]
    public void PlanCommandUsesDomainPurposeAndProtectedSqlConstruction(BackupRunPurpose purpose, string options)
    {
        using var connection = new SqlConnection();
        var request = new TargetSqlBackupRequest("Synthetic]Db", "synthetic.bak", purpose, true, true, 7200);
        using var command = TargetSqlCommandFactory.CreateBackup(connection, request);
        Assert.Equal($"BACKUP DATABASE [Synthetic]]Db]\nTO DISK = @backupPath\n{options}", command.CommandText);
        Assert.Equal(7200, command.CommandTimeout);
        var parameter = Assert.Single(command.Parameters.Cast<SqlParameter>());
        Assert.Equal(SqlDbType.NVarChar, parameter.SqlDbType);
        Assert.Equal(2048, parameter.Size);
        Assert.Equal("synthetic.bak", parameter.Value);
        Assert.DoesNotContain("INIT", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("FORMAT", command.CommandText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, "WITH DIFFERENTIAL;")]
    [InlineData(true, false, "WITH DIFFERENTIAL, CHECKSUM;")]
    [InlineData(false, true, "WITH DIFFERENTIAL, COMPRESSION;")]
    public void DifferentialAddsOnlyRequestedProtectionOptions(bool checksum, bool compression, string options)
    {
        using var connection = new SqlConnection();
        using var command = TargetSqlCommandFactory.CreateBackup(connection,
            new("SyntheticDatabase", "synthetic.bak", BackupRunPurpose.PlanDifferential, checksum, compression, 60));
        Assert.EndsWith(options, command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("COPY_ONLY", command.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlBoundaryRejectsDifferentialCombinedWithCopyOnly()
    {
        using var connection = new SqlConnection();
        Assert.Throws<ArgumentException>(() => TargetSqlCommandFactory.CreateDatabaseBackup(
            connection, "SyntheticDatabase", "synthetic.bak", BackupType.Differential, true, true, false, 60));
    }

    [Fact]
    public void ProbeCommandsUseOnlySqlServer2008R2MetadataAndCatalogFields()
    {
        using var connection = new SqlConnection();
        using var server = TargetSqlCommandFactory.CreateServerProbe(connection);
        using var databases = TargetSqlCommandFactory.CreateDatabaseDiscovery(connection);

        Assert.Equal(CommandType.Text, server.CommandType);
        Assert.Equal(TargetSqlCommandFactory.ProbeCommandTimeoutSeconds, server.CommandTimeout);
        Assert.Contains("SERVERPROPERTY('ProductVersion')", server.CommandText, StringComparison.Ordinal);
        Assert.Contains("SERVERPROPERTY('EngineEdition')", server.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("ProductMajorVersion", server.CommandText, StringComparison.Ordinal);
        Assert.Empty(server.Parameters.Cast<SqlParameter>());

        Assert.Equal(TargetSqlCommandFactory.DiscoveryCommandTimeoutSeconds, databases.CommandTimeout);
        Assert.Contains("FROM [sys].[databases]", databases.CommandText, StringComparison.Ordinal);
        Assert.Contains("[source_database_id]", databases.CommandText, StringComparison.Ordinal);
        Assert.Contains("[recovery_model_desc]", databases.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("BACKUP DATABASE", databases.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RESTORE", databases.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(databases.Parameters.Cast<SqlParameter>());
    }

    [Fact]
    public void FullBackupQuotesIdentifierAndParameterizesProtectedPath()
    {
        const string localSqlPath = @"D:\SyntheticRoot\attempt.bak";
        var request = new TargetSqlFullBackupRequest(
            "Synthetic]Db; DROP DATABASE [Other",
            localSqlPath,
            useCopyOnly: true,
            useChecksum: true,
            useCompression: true,
            commandTimeoutSeconds: 7_200);
        using var connection = new SqlConnection();
        using var command = TargetSqlCommandFactory.CreateFullBackup(connection, request);

        Assert.Equal(
            "BACKUP DATABASE [Synthetic]]Db; DROP DATABASE [Other]\n"
            + "TO DISK = @backupPath\n"
            + "WITH COPY_ONLY, CHECKSUM, COMPRESSION;",
            command.CommandText);
        Assert.DoesNotContain(localSqlPath, command.CommandText, StringComparison.Ordinal);
        Assert.Equal(7_200, command.CommandTimeout);
        var parameter = Assert.Single(command.Parameters.Cast<SqlParameter>());
        Assert.Equal("@backupPath", parameter.ParameterName);
        Assert.Equal(SqlDbType.NVarChar, parameter.SqlDbType);
        Assert.Equal(2_048, parameter.Size);
        Assert.Equal(localSqlPath, parameter.Value);
    }

    [Theory]
    [InlineData(false, false, false, "TO DISK = @backupPath;")]
    [InlineData(true, false, false, "WITH COPY_ONLY;")]
    [InlineData(false, true, false, "WITH CHECKSUM;")]
    [InlineData(false, false, true, "WITH COMPRESSION;")]
    public void FullBackupIncludesOnlyExplicitOptions(
        bool useCopyOnly,
        bool useChecksum,
        bool useCompression,
        string expectedEnding)
    {
        var request = new TargetSqlFullBackupRequest(
            "SyntheticDatabase",
            "synthetic.bak",
            useCopyOnly,
            useChecksum,
            useCompression,
            commandTimeoutSeconds: 60);
        using var connection = new SqlConnection();
        using var command = TargetSqlCommandFactory.CreateFullBackup(connection, request);

        Assert.EndsWith(expectedEnding, command.CommandText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "RESTORE VERIFYONLY\nFROM DISK = @backupPath;")]
    [InlineData(true, "RESTORE VERIFYONLY\nFROM DISK = @backupPath\nWITH CHECKSUM;")]
    public void VerificationUsesParameterizedPathAndExplicitChecksum(
        bool useChecksum,
        string expectedCommandText)
    {
        const string localSqlPath = @"D:\SyntheticRoot\attempt.bak";
        var request = new TargetSqlBackupVerificationRequest(
            localSqlPath,
            useChecksum,
            commandTimeoutSeconds: 600);
        using var connection = new SqlConnection();
        using var command = TargetSqlCommandFactory.CreateBackupVerification(connection, request);

        Assert.Equal(expectedCommandText, command.CommandText);
        Assert.DoesNotContain(localSqlPath, command.CommandText, StringComparison.Ordinal);
        Assert.Equal(localSqlPath, Assert.Single(command.Parameters.Cast<SqlParameter>()).Value);
    }

    [Fact]
    public void BackupIdentityInspectionUsesSqlServer2008R2MetadataAndParameters()
    {
        const string databaseName = "Synthetic]Db; DROP DATABASE [Other";
        const string localSqlPath = @"D:\SyntheticRoot\attempt.bak";
        var request = new TargetSqlBackupIdentityRequest(
            databaseName,
            localSqlPath,
            commandTimeoutSeconds: 600);
        using var connection = new SqlConnection();
        using var command = TargetSqlCommandFactory.CreateBackupIdentityInspection(connection, request);

        Assert.Contains("[msdb].[dbo].[backupset]", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("[msdb].[dbo].[backupmediafamily]", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("[backup_finish_date]", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("[physical_device_name] = @backupPath", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("[database_name] = @databaseName", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain(databaseName, command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain(localSqlPath, command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("STRING_AGG", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("THROW", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(600, command.CommandTimeout);
        Assert.Equal(databaseName, command.Parameters["@databaseName"].Value);
        Assert.Equal(localSqlPath, command.Parameters["@backupPath"].Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\0name")]
    public void IdentifierQuotingRejectsInvalidNames(string databaseName)
    {
        Assert.Throws<ArgumentException>(() => TargetSqlCommandFactory.QuoteIdentifier(databaseName));
    }
}
