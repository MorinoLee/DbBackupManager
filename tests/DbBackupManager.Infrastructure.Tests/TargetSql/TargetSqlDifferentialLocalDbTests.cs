using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.Data.SqlClient;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class TargetSqlDifferentialLocalDbTests
{
    private static readonly string[] BackupNames = ["missing_DIFF.bak", "baseline_FULL.bak", "temporary_COPYONLY.bak", "changed_DIFF.bak"];

    [Fact]
    public async Task RealCommandsRejectMissingBaseAndCopyOnlyPreservesDifferentialBase()
    {
        const string prefix = "DbBackupManagerDiffCommands_";
        var databaseName = $"{prefix}{Guid.NewGuid():N}";
        Assert.StartsWith(prefix, databaseName, StringComparison.Ordinal);
        var directory = Path.Combine(Path.GetTempPath(), $"DbBackupManagerDiff_{Guid.NewGuid():N}");
        var paths = BackupNames
            .Select(name => Path.Combine(directory, name)).ToArray();
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB",
            InitialCatalog = "master",
            IntegratedSecurity = true,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = true,
            Pooling = false,
        };
        await using var master = new SqlConnection(builder.ConnectionString);
        await master.OpenAsync();
        try
        {
            Assert.False(Directory.Exists(directory));
            Directory.CreateDirectory(directory);
            await using (var create = new SqlCommand($"CREATE DATABASE [{databaseName}];", master))
            {
                await create.ExecuteNonQueryAsync();
            }

            builder.InitialCatalog = databaseName;
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using var session = new SqlClientTargetSqlBackupSession(connection);
            var failure = await Assert.ThrowsAsync<TargetSqlClientException>(() => session.ExecuteBackupAsync(
                Request(databaseName, paths[0], BackupRunPurpose.PlanDifferential), CancellationToken.None));
            Assert.Equal(TargetSqlFailureCode.DifferentialBaseMissing, failure.FailureCode);
            Assert.Equal(TargetSqlClientFailureCertainty.Confirmed, failure.Certainty);

            await using (var seed = new SqlCommand("CREATE TABLE [SyntheticData] ([Id] int NOT NULL); INSERT INTO [SyntheticData] VALUES (1);", connection))
            {
                await seed.ExecuteNonQueryAsync();
            }

            await session.ExecuteBackupAsync(Request(databaseName, paths[1], BackupRunPurpose.PlanFull), CancellationToken.None);
            await using (var change = new SqlCommand("INSERT INTO [SyntheticData] VALUES (2);", connection))
            {
                await change.ExecuteNonQueryAsync();
            }

            await session.ExecuteBackupAsync(Request(databaseName, paths[2], BackupRunPurpose.AdHocCopyOnlyFull), CancellationToken.None);
            await session.ExecuteBackupAsync(Request(databaseName, paths[3], BackupRunPurpose.PlanDifferential), CancellationToken.None);

            await using (var baseline = new SqlCommand(
                """
                SELECT COUNT(*)
                FROM [msdb].[dbo].[backupset] AS f
                JOIN [msdb].[dbo].[backupmediafamily] AS fm ON fm.[media_set_id] = f.[media_set_id]
                JOIN [msdb].[dbo].[backupset] AS d ON d.[differential_base_lsn] = f.[checkpoint_lsn]
                    AND d.[database_name] = f.[database_name]
                JOIN [msdb].[dbo].[backupmediafamily] AS dm ON dm.[media_set_id] = d.[media_set_id]
                WHERE f.[database_name] = @databaseName AND f.[type] = 'D' AND f.[is_copy_only] = 0
                    AND d.[type] = 'I' AND d.[is_copy_only] = 0
                    AND fm.[physical_device_name] = @fullPath AND dm.[physical_device_name] = @diffPath;
                """, connection))
            {
                baseline.Parameters.AddWithValue("@databaseName", databaseName);
                baseline.Parameters.AddWithValue("@fullPath", paths[1]);
                baseline.Parameters.AddWithValue("@diffPath", paths[3]);
                Assert.Equal(1, (int)(await baseline.ExecuteScalarAsync())!);
            }

            await using (var copyOnly = new SqlCommand(
                """
                SELECT COUNT(*) FROM [msdb].[dbo].[backupset] AS bs
                JOIN [msdb].[dbo].[backupmediafamily] AS bmf ON bs.[media_set_id] = bmf.[media_set_id]
                WHERE bs.[database_name] = @databaseName AND bs.[type] = 'D' AND bs.[is_copy_only] = 1
                    AND bmf.[physical_device_name] = @path;
                """, connection))
            {
                copyOnly.Parameters.AddWithValue("@databaseName", databaseName);
                copyOnly.Parameters.AddWithValue("@path", paths[2]);
                Assert.Equal(1, (int)(await copyOnly.ExecuteScalarAsync())!);
            }

            foreach (var path in paths.Skip(1))
            {
                Assert.True(new FileInfo(path).Length > 0);
                await using var verify = TargetSqlCommandFactory.CreateBackupVerification(connection, new(path, true, 60));
                await verify.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            // 数据库名在创建前已检查固定测试前缀，且仅由随机标识生成。
            await using var drop = new SqlCommand(
                $"IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END;", master);
            drop.Parameters.AddWithValue("@name", databaseName);
            await drop.ExecuteNonQueryAsync();
            await using var history = new SqlCommand("EXEC [msdb].[dbo].[sp_delete_database_backuphistory] @database_name = @name;", master);
            history.Parameters.AddWithValue("@name", databaseName);
            await history.ExecuteNonQueryAsync();
            foreach (var path in paths)
            {
                File.Delete(path);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }
    }

    private static TargetSqlBackupRequest Request(string databaseName, string path, BackupRunPurpose purpose) =>
        new(databaseName, path, purpose, true, false, 60);
}
