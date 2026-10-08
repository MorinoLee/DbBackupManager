using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

// 仅供实证测试使用：固定连接本机 LocalDB，不读取部署配置。
internal sealed class LocalDbBackupMetadataProofScope(ITestOutputHelper output) : IAsyncDisposable
{
    private const string Prefix = "DbBackupManagerMetadataProof_";
    private readonly string directory = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private readonly List<string> files = [];
    private bool creationAttempted;

    public string DatabaseName { get; } = Prefix + Guid.NewGuid().ToString("N");

    public SqlConnection Connection { get; private set; } = null!;

    public SqlConnection NewConnection() => new(ConnectionString(DatabaseName));

    public async Task InitializeAsync()
    {
        Assert.True(Guid.TryParseExact(DatabaseName[Prefix.Length..], "N", out _));
        await using var master = new SqlConnection(ConnectionString("master"));
        await master.OpenAsync();
        await using (var probe = new SqlCommand("SELECT CONVERT(int, SERVERPROPERTY('IsLocalDB')), CONVERT(varchar(40), SERVERPROPERTY('ProductVersion')), CONVERT(varchar(80), SERVERPROPERTY('Edition'));", master))
        await using (var reader = await probe.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            output.WriteLine("实际 SQL：{0}，{1}，IsLocalDB=1", reader.GetString(1), reader.GetString(2));
        }

        Assert.False(Directory.Exists(directory));
        Directory.CreateDirectory(directory);
        await using (var existing = new SqlCommand("SELECT DB_ID(@name);", master))
        {
            existing.Parameters.AddWithValue("@name", DatabaseName);
            Assert.IsType<DBNull>(await existing.ExecuteScalarAsync());
        }

        creationAttempted = true;
        await ExecuteAsync(master, $"CREATE DATABASE [{DatabaseName}];");
        Connection = NewConnection();
        await Connection.OpenAsync();
        var secondary = Path.Combine(directory, "synthetic_secondary.ndf");
        files.Add(secondary);
        await ExecuteAsync(Connection, $"""
            ALTER DATABASE [{DatabaseName}] SET RECOVERY FULL;
            ALTER DATABASE [{DatabaseName}] ADD FILEGROUP [SyntheticSecondary];
            ALTER DATABASE [{DatabaseName}] ADD FILE (NAME = N'SyntheticSecondary', FILENAME = N'{secondary.Replace("'", "''", StringComparison.Ordinal)}', SIZE = 4MB, FILEGROWTH = 4MB) TO FILEGROUP [SyntheticSecondary];
            CREATE TABLE [dbo].[SyntheticPrimary] ([Id] int NOT NULL PRIMARY KEY, [Value] int NOT NULL) ON [PRIMARY];
            CREATE TABLE [dbo].[SyntheticSecondary] ([Id] int NOT NULL PRIMARY KEY, [Value] int NOT NULL) ON [SyntheticSecondary];
            INSERT INTO [dbo].[SyntheticPrimary] VALUES (1, 1);
            INSERT INTO [dbo].[SyntheticSecondary] VALUES (1, 1);
            """);
    }

    public Task ChangeAsync() => ExecuteAsync(Connection, "UPDATE [dbo].[SyntheticSecondary] SET [Value] = [Value] + 1;");

    public async Task<ProofBackup> BackupAsync(string label, BackupRunPurpose purpose, bool external = false)
    {
        var path = Path.Combine(directory, $"{label}_{Guid.NewGuid():N}_{purpose}.bak");
        files.Add(path);
        Assert.False(File.Exists(path));
        await using var command = external
            ? new SqlCommand($"BACKUP DATABASE [{DatabaseName}] TO DISK = @backupPath WITH CHECKSUM;", Connection) { CommandTimeout = 60 }
            : TargetSqlCommandFactory.CreateBackup(Connection, new TargetSqlBackupRequest(DatabaseName, path, purpose, true, false, 60));
        if (external)
        {
            Assert.Equal(BackupRunPurpose.PlanFull, purpose);
            command.Parameters.AddWithValue("@backupPath", path);
        }

        await command.ExecuteNonQueryAsync();
        Assert.True(new FileInfo(path).Length > 0);
        return await ReadBackupAsync(label, path);
    }

    public async Task<ProofBackup> ReadBackupAsync(string label, string path)
    {
        await using var headerCommand = new SqlCommand("RESTORE HEADERONLY FROM DISK = @path;", Connection) { CommandTimeout = 60 };
        headerCommand.Parameters.AddWithValue("@path", path);
        ProofMetadata header;
        await using (var reader = await headerCommand.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(DatabaseName, reader.GetString(reader.GetOrdinal("DatabaseName")));
            output.WriteLine("HEADERONLY BackupType 实际列类型：{0}", reader.GetDataTypeName(reader.GetOrdinal("BackupType")));
            header = ReadMetadata(reader);
            Assert.False(await reader.ReadAsync());
        }

        // 由任务专属路径独立定位历史，不能用待验证的 GUID/LSN 作为 JOIN 条件。
        await using var historyCommand = new SqlCommand("""
            SELECT bs.database_name AS DatabaseName, bs.backup_set_uuid AS BackupSetGUID, bs.database_guid AS BindingID,
                bs.family_guid AS FamilyGUID, bs.first_recovery_fork_guid AS FirstRecoveryForkID,
                bs.last_recovery_fork_guid AS RecoveryForkID, bs.fork_point_lsn AS ForkPointLSN,
                CONVERT(smallint, CASE bs.type WHEN 'D' THEN 1 WHEN 'I' THEN 5 ELSE 0 END) AS BackupType,
                bs.first_lsn AS FirstLSN, bs.last_lsn AS LastLSN, bs.checkpoint_lsn AS CheckpointLSN,
                bs.database_backup_lsn AS DatabaseBackupLSN, bs.differential_base_lsn AS DifferentialBaseLSN,
                bs.differential_base_guid AS DifferentialBaseGUID, bs.is_copy_only AS IsCopyOnly,
                bs.has_backup_checksums AS HasBackupChecksums, bs.backup_start_date AS BackupStartDate,
                bs.backup_finish_date AS BackupFinishDate
            FROM msdb.dbo.backupset AS bs
            JOIN msdb.dbo.backupmediafamily AS mf ON mf.media_set_id = bs.media_set_id
            WHERE bs.database_name = @name AND mf.physical_device_name = @path;
            """, Connection);
        historyCommand.Parameters.AddWithValue("@name", DatabaseName);
        historyCommand.Parameters.AddWithValue("@path", path);
        ProofMetadata history;
        await using (var reader = await historyCommand.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(DatabaseName, reader.GetString(reader.GetOrdinal("DatabaseName")));
            history = ReadMetadata(reader);
            Assert.False(await reader.ReadAsync());
        }

        WriteMetadata(label, "HEADERONLY", header);
        WriteMetadata(label, "msdb", history);
        Assert.Equal(header, history);
        return new ProofBackup(label, path, header, history);
    }

    private void WriteMetadata(string label, string source, ProofMetadata metadata) => output.WriteLine(
        "{0} {1}：SetGUID={2}; Database={3}; Branch={4}; Type={5}; CopyOnly={6}; Checksum={7}; FirstLSN={8}; LastLSN={9}; CheckpointLSN={10}; DatabaseBackupLSN={11}; DifferentialBaseLSN={12}; DifferentialBaseGUID={13}; ForkPointLSN={14}; StartLocal={15:O}; FinishLocal={16:O}",
        label, source, metadata.BackupSetGuid, metadata.Database, metadata.Branch, metadata.Type, metadata.IsCopyOnly,
        metadata.HasChecksums, metadata.FirstLsn, metadata.LastLsn, metadata.CheckpointLsn, metadata.DatabaseBackupLsn,
        metadata.DifferentialBaseLsn, metadata.DifferentialBaseGuid, metadata.ForkPointLsn, metadata.StartedLocal, metadata.FinishedLocal);

    public async Task<ProofActiveBaseline> ReadActiveAsync()
    {
        await using var command = new SqlCommand("""
            SELECT df.file_id, df.file_guid, df.differential_base_guid, df.differential_base_lsn,
                df.differential_base_time, rs.database_guid, rs.family_guid,
                rs.first_recovery_fork_guid, rs.recovery_fork_guid
            FROM sys.database_files AS df
            CROSS JOIN sys.database_recovery_status AS rs
            WHERE df.type = 0 AND rs.database_id = DB_ID()
            ORDER BY df.file_id;
            """, Connection);
        await using var reader = await command.ExecuteReaderAsync();
        var dataFiles = new List<ProofDataFile>();
        BackupDatabaseIdentity? database = null;
        Guid? currentRecoveryForkId = null;
        Guid? diagnosticFirstRecoveryForkId = null;
        while (await reader.ReadAsync())
        {
            var rowDatabase = new BackupDatabaseIdentity(GuidValue(reader, "database_guid"), GuidValue(reader, "family_guid"));
            var rowCurrentFork = GuidValue(reader, "recovery_fork_guid");
            var rowFirstFork = GuidValue(reader, "first_recovery_fork_guid");
            if (database is null)
            {
                database = rowDatabase;
                currentRecoveryForkId = rowCurrentFork;
                diagnosticFirstRecoveryForkId = rowFirstFork;
            }
            else
            {
                Assert.Equal(database, rowDatabase);
                Assert.Equal(currentRecoveryForkId, rowCurrentFork);
                Assert.Equal(diagnosticFirstRecoveryForkId, rowFirstFork);
            }

            var file = new ProofDataFile(
                reader.GetInt32(reader.GetOrdinal("file_id")), GuidValue(reader, "file_guid"),
                new ActiveDataFileBaselineEvidence(GuidValue(reader, "differential_base_guid"), LsnValue(reader, "differential_base_lsn")),
                LocalTimeValue(reader, "differential_base_time"));
            output.WriteLine("活动数据文件：{0}", file);
            dataFiles.Add(file);
        }

        Assert.Equal(2, dataFiles.Count);
        Assert.All(dataFiles, file => Assert.NotNull(file.FileGuid));
        Assert.NotNull(database);
        // 目录起始分支只诊断，不反填当前分支，也不要求它在所有版本上为 NULL。
        output.WriteLine("数据库当前状态：{0}; recovery_fork_guid={1}; first_recovery_fork_guid（仅诊断）={2}",
            database, currentRecoveryForkId, diagnosticFirstRecoveryForkId);
        return new ProofActiveBaseline(database, currentRecoveryForkId, diagnosticFirstRecoveryForkId, dataFiles);
    }

    public async ValueTask DisposeAsync()
    {
        // 每一步都尝试清理；即使测试断言、关闭连接或删库失败，也不跳过其余资源。
        var failures = new List<Exception>();
        async Task Attempt(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (Connection is not null)
        {
            await Attempt(() => Connection.DisposeAsync().AsTask());
        }

        if (creationAttempted)
        {
            await Attempt(async () =>
            {
                await using var cleanup = new SqlConnection(ConnectionString("master"));
                await cleanup.OpenAsync();
                await using var drop = new SqlCommand($"IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}]; END;", cleanup);
                drop.Parameters.AddWithValue("@name", DatabaseName);
                await drop.ExecuteNonQueryAsync();
                await using var verify = new SqlCommand("SELECT DB_ID(@name);", cleanup);
                verify.Parameters.AddWithValue("@name", DatabaseName);
                Assert.IsType<DBNull>(await verify.ExecuteScalarAsync());
            });
            await Attempt(async () =>
            {
                await using var cleanup = new SqlConnection(ConnectionString("master"));
                await cleanup.OpenAsync();
                await using var history = new SqlCommand("EXEC msdb.dbo.sp_delete_database_backuphistory @database_name = @name;", cleanup);
                history.Parameters.AddWithValue("@name", DatabaseName);
                await history.ExecuteNonQueryAsync();
            });
        }

        foreach (var path in files)
        {
            await Attempt(() =>
            {
                Assert.Equal(Path.GetFullPath(directory), Path.GetDirectoryName(Path.GetFullPath(path)));
                File.Delete(path);
                Assert.False(File.Exists(path));
                return Task.CompletedTask;
            });
        }

        await Attempt(() =>
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(directory)));
            Assert.True(Guid.TryParseExact(Path.GetFileName(directory)[Prefix.Length..], "N", out _));
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory); // 不递归删除，也不枚举未知路径。
            }

            Assert.False(Directory.Exists(directory));
            return Task.CompletedTask;
        });
        if (failures.Count != 0)
        {
            throw new AggregateException("本次随机测试资源未全部清理。", failures);
        }

        output.WriteLine("已清理本次随机数据库、备份历史及临时目录。");
    }

    internal static async Task ExecuteAsync(SqlConnection connection, string sql, SqlTransaction? transaction = null)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }

    private static string ConnectionString(string databaseName) => new SqlConnectionStringBuilder
    {
        DataSource = @"(localdb)\MSSQLLocalDB",
        InitialCatalog = databaseName,
        IntegratedSecurity = true,
        Encrypt = SqlConnectionEncryptOption.Mandatory,
        TrustServerCertificate = true,
        Pooling = false,
    }.ConnectionString;

    private static ProofMetadata ReadMetadata(SqlDataReader reader) => new(
        GuidValue(reader, "BackupSetGUID"),
        new(GuidValue(reader, "BindingID"), GuidValue(reader, "FamilyGUID")),
        new(GuidValue(reader, "FirstRecoveryForkID"), GuidValue(reader, "RecoveryForkID")),
        // 文档标 smallint，但本机 HEADERONLY 返回 tinyint；仅对类型码做整数拓宽，LSN 始终 GetDecimal。
        reader.GetValue(reader.GetOrdinal("BackupType")) switch
        {
            (byte)1 or (short)1 => BackupType.Full,
            (byte)5 or (short)5 => BackupType.Differential,
            var type => throw new InvalidOperationException($"实证样本出现意外备份类型：{type}。"),
        },
        LsnValue(reader, "FirstLSN"), LsnValue(reader, "LastLSN"), LsnValue(reader, "CheckpointLSN"),
        LsnValue(reader, "DatabaseBackupLSN"), LsnValue(reader, "DifferentialBaseLSN"),
        GuidValue(reader, "DifferentialBaseGUID"), LsnValue(reader, "ForkPointLSN"),
        reader.GetBoolean(reader.GetOrdinal("IsCopyOnly")), reader.GetBoolean(reader.GetOrdinal("HasBackupChecksums")),
        LocalTimeValue(reader, "BackupStartDate"), LocalTimeValue(reader, "BackupFinishDate"));

    private static Guid? GuidValue(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }

    private static BackupLsn? LsnValue(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : new BackupLsn(reader.GetDecimal(ordinal));
    }

    private static DateTime? LocalTimeValue(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var local = reader.GetDateTime(ordinal);
        Assert.Equal(DateTimeKind.Unspecified, local.Kind);
        return local;
    }
}

internal sealed record ProofMetadata(
    Guid? BackupSetGuid, BackupDatabaseIdentity Database, BackupRecoveryBranch Branch, BackupType Type,
    BackupLsn? FirstLsn, BackupLsn? LastLsn, BackupLsn? CheckpointLsn, BackupLsn? DatabaseBackupLsn,
    BackupLsn? DifferentialBaseLsn, Guid? DifferentialBaseGuid, BackupLsn? ForkPointLsn,
    bool IsCopyOnly, bool HasChecksums, DateTime? StartedLocal, DateTime? FinishedLocal)
{
    public FullBackupBaselineEvidence FullEvidence => new(BackupSetGuid, Database, Branch, Type, IsCopyOnly, CheckpointLsn);

    public DifferentialBackupEvidence DifferentialEvidence => new(
        new(Database, Branch, DifferentialBaseGuid, DifferentialBaseLsn), Type, IsCopyOnly, DatabaseBackupLsn);
}

internal sealed record ProofBackup(string Label, string Path, ProofMetadata Header, ProofMetadata History);

internal sealed record ProofDataFile(int FileId, Guid? FileGuid, ActiveDataFileBaselineEvidence Evidence, DateTime? BaseTimeRaw);

internal sealed record ProofActiveBaseline(
    BackupDatabaseIdentity Database,
    Guid? CurrentRecoveryForkId,
    Guid? DiagnosticFirstRecoveryForkId,
    IReadOnlyList<ProofDataFile> Files)
{
    public ActiveDifferentialBaselineEvidence Evidence => new(Database, CurrentRecoveryForkId, Files.Select(file => file.Evidence));
}
