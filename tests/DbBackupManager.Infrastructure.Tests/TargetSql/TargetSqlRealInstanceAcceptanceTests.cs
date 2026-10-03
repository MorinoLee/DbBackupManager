using System.Data;
using System.Globalization;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class TargetSqlRealInstanceAcceptanceTests(ITestOutputHelper output)
{
    public const string Category = "P56TargetSqlAcceptance";

    [Fact]
    [Trait("Category", Category)]
    public async Task ConfiguredTargetPassesReadOnlySafetyMatrix()
    {
        var environment = TargetSqlAcceptanceEnvironment.LoadFromProcess();
        if (environment is null)
        {
            output.WriteLine("P5.6 真实目标环境未配置；只执行默认拒绝与静态 Guard 测试。");
            return;
        }

        var (_, probe, connection) = CreateAdapters(environment);
        var server = await RequireSuccessAsync(
            probe.ProbeServerAsync(connection),
            "实例探测");
        environment.ValidateServer(server);
        await AssertEncryptedSessionAsync(environment, connection);

        var catalog = await RequireSuccessAsync(
            probe.DiscoverDatabasesAsync(connection),
            "数据库发现");
        var database = catalog.Databases.SingleOrDefault(item =>
            string.Equals(item.Name, environment.DatabaseName, StringComparison.Ordinal));
        if (database is null || !database.CanBeManaged)
        {
            throw new InvalidOperationException(
                "P5.6 专用源数据库不可见、非 ONLINE、属于系统库或属于快照；未执行写操作。");
        }

        output.WriteLine(
            "P5.6 只读矩阵通过：配置档={0}，产品版本={1}，ProductLevel={2}，EngineEdition={3}，证书策略={4}。",
            environment.Profile,
            server.ProductVersion,
            server.ProductLevel ?? "未提供",
            server.EngineEdition.ToString(CultureInfo.InvariantCulture),
            environment.TrustServerCertificate ? "受控信任例外" : "严格验证");
    }

    [Fact]
    [Trait("Category", Category)]
    public async Task MutationEnabledTargetPassesBackupVerifyAndIsolatedRestoreMatrix()
    {
        var environment = TargetSqlAcceptanceEnvironment.LoadFromProcess();
        if (environment is null)
        {
            output.WriteLine("P5.6 真实目标环境未配置；没有执行 Backup、Verify 或 Restore。");
            return;
        }

        if (!environment.AllowMutation)
        {
            output.WriteLine(
                $"{TargetSqlAcceptanceEnvironment.AllowMutationVariable} 未显式设为 true；没有执行 Backup、Verify 或 Restore。");
            return;
        }

        var runId = Guid.NewGuid();
        var restoreDatabase = $"{TargetSqlAcceptanceEnvironment.RestoreDatabasePrefix}{runId:N}";
        TargetSqlAcceptanceEnvironment.EnsureRestoreDatabaseName(restoreDatabase);
        var backupPaths = environment.CreateBackupPaths(runId);
        var restoreFiles = new List<(string SqlPath, string WorkerPath)>();
        string? operationFailure = null;

        try
        {
            EnsureFileDoesNotExist(backupPaths.WorkerPath);
            var (executor, probe, connection) = CreateAdapters(environment);
            var server = await RequireSuccessAsync(
                probe.ProbeServerAsync(connection),
                "写入前实例探测");
            environment.ValidateServer(server);
            await AssertEncryptedSessionAsync(environment, connection);

            var catalog = await RequireSuccessAsync(
                probe.DiscoverDatabasesAsync(connection),
                "写入前数据库发现");
            if (!catalog.Databases.Any(item =>
                    string.Equals(item.Name, environment.DatabaseName, StringComparison.Ordinal)
                    && item.CanBeManaged))
            {
                throw new InvalidOperationException(
                    "P5.6 专用源数据库未通过写入前身份与状态核对；没有执行 Backup。");
            }

            var backup = await RequireSuccessAsync(
                executor.ExecuteFullBackupAsync(
                    connection,
                    new TargetSqlFullBackupRequest(
                        environment.DatabaseName,
                        backupPaths.SqlPath,
                        useCopyOnly: true,
                        useChecksum: true,
                        useCompression: false,
                        commandTimeoutSeconds: 3_600)),
                "FULL Backup");
            if (!backup.UsedCopyOnly || !backup.UsedChecksum || backup.UsedCompression)
            {
                throw new InvalidOperationException("P5.6 FULL Backup 返回的选项证据不符合最低矩阵。");
            }

            var backupLength = RequireNonEmptyFile(backupPaths.WorkerPath);
            var verification = await RequireSuccessAsync(
                probe.VerifyBackupAsync(
                    connection,
                    new TargetSqlBackupVerificationRequest(
                        backupPaths.SqlPath,
                        useChecksum: true,
                        commandTimeoutSeconds: 3_600)),
                "RESTORE VERIFYONLY");
            if (!verification.UsedChecksum)
            {
                throw new InvalidOperationException("P5.6 Verify 没有返回 CHECKSUM 证据。");
            }

            var logicalFiles = await ReadBackupFileListAsync(environment, connection, backupPaths.SqlPath);
            for (var index = 0; index < logicalFiles.Count; index++)
            {
                restoreFiles.Add(environment.CreateRestoreFilePaths(
                    runId,
                    index,
                    logicalFiles[index].FileType));
                EnsureFileDoesNotExist(restoreFiles[^1].WorkerPath);
            }

            await RestoreDatabaseAsync(
                environment,
                connection,
                restoreDatabase,
                backupPaths.SqlPath,
                logicalFiles,
                restoreFiles);
            await AssertDatabaseOnlineAsync(environment, connection, restoreDatabase);

            output.WriteLine(
                "P5.6 写入矩阵通过：配置档={0}，产品版本={1}，ProductLevel={2}，EngineEdition={3}，备份字节数={4}，COPY_ONLY=True，CHECKSUM=True，COMPRESSION=False，Verify=True，隔离Restore=True，证书策略={5}。",
                environment.Profile,
                server.ProductVersion,
                server.ProductLevel ?? "未提供",
                server.EngineEdition.ToString(CultureInfo.InvariantCulture),
                backupLength.ToString(CultureInfo.InvariantCulture),
                environment.TrustServerCertificate ? "受控信任例外" : "严格验证");
        }
        catch (InvalidOperationException exception)
        {
            operationFailure = exception.Message;
        }

        var cleanupSucceeded = await CleanupAsync(
            environment,
            restoreDatabase,
            backupPaths.WorkerPath,
            restoreFiles);
        if (!cleanupSucceeded)
        {
            throw new InvalidOperationException(
                "P5.6 隔离资源清理未全部完成；请仅按固定 Restore/文件前缀人工核对，禁止扩大清理范围。");
        }

        if (operationFailure is not null)
        {
            throw new InvalidOperationException(operationFailure);
        }
    }

    private static (
        ITargetSqlBackupExecutor Executor,
        ITargetSqlReadOnlyProbe Probe,
        TargetSqlConnectionInput Connection) CreateAdapters(
            TargetSqlAcceptanceEnvironment environment)
    {
        var credentialReferenceId = Guid.NewGuid();
        var resolver = new AcceptanceEnvironmentCredentialResolver(
            environment,
            credentialReferenceId);
        var connection = environment.CreateConnectionInput(credentialReferenceId);
        return (
            new SqlClientTargetSqlBackupExecutor(
                resolver,
                new SqlClientTargetSqlBackupSessionFactory()),
            new SqlClientTargetSqlReadOnlyProbe(
                resolver,
                new SqlClientTargetSqlSessionFactory()),
            connection);
    }

    private static async Task<T> RequireSuccessAsync<T>(
        Task<TargetSqlResult<T>> operation,
        string operationName)
        where T : class
    {
        var result = await operation;
        if (!result.IsSucceeded)
        {
            throw new InvalidOperationException(
                $"P5.6 {operationName} 未通过：Outcome={result.Outcome}，Code={result.Failure!.Code}，Phase={result.Failure.Phase}。");
        }

        return result.Value!;
    }

    private static async Task AssertEncryptedSessionAsync(
        TargetSqlAcceptanceEnvironment environment,
        TargetSqlConnectionInput connectionInput)
    {
        try
        {
            using var credential = environment.CreateCredentialLease();
            await using var connection = SqlClientTargetSqlConnectionFactory.Create(
                connectionInput,
                credential);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT [encrypt_option] FROM [sys].[dm_exec_connections] WHERE [session_id] = @@SPID;";
            var encryption = Convert.ToString(
                await command.ExecuteScalarAsync(),
                CultureInfo.InvariantCulture);
            if (!string.Equals(encryption, "TRUE", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("P5.6 目标会话无法确认已实际加密。");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (TargetSqlClientException.CanClassify(exception))
        {
            throw new InvalidOperationException("P5.6 无法完成会话加密状态核对。");
        }
    }

    private static async Task<List<BackupLogicalFile>> ReadBackupFileListAsync(
        TargetSqlAcceptanceEnvironment environment,
        TargetSqlConnectionInput connectionInput,
        string backupPath)
    {
        try
        {
            using var credential = environment.CreateCredentialLease();
            await using var connection = SqlClientTargetSqlConnectionFactory.Create(
                connectionInput,
                credential);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "RESTORE FILELISTONLY FROM DISK = @backupPath;";
            command.CommandTimeout = 3_600;
            command.Parameters.Add("@backupPath", SqlDbType.NVarChar, 2_048).Value = backupPath;
            await using var reader = await command.ExecuteReaderAsync();
            var logicalNameOrdinal = reader.GetOrdinal("LogicalName");
            var typeOrdinal = reader.GetOrdinal("Type");
            var files = new List<BackupLogicalFile>();
            while (await reader.ReadAsync())
            {
                var typeValue = reader.GetString(typeOrdinal);
                if (typeValue.Length != 1 || typeValue[0] is not ('D' or 'L'))
                {
                    throw new InvalidOperationException(
                        "P5.6 隔离 Restore 只接受普通数据文件与日志文件备份集。");
                }

                files.Add(new BackupLogicalFile(reader.GetString(logicalNameOrdinal), typeValue[0]));
            }

            if (files.Count == 0
                || !files.Any(file => file.FileType == 'D')
                || !files.Any(file => file.FileType == 'L'))
            {
                throw new InvalidOperationException("P5.6 备份集没有完整的数据文件与日志文件清单。");
            }

            return files;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (TargetSqlClientException.CanClassify(exception))
        {
            throw new InvalidOperationException("P5.6 无法读取隔离 Restore 所需的备份文件清单。");
        }
    }

    private static async Task RestoreDatabaseAsync(
        TargetSqlAcceptanceEnvironment environment,
        TargetSqlConnectionInput connectionInput,
        string restoreDatabase,
        string backupPath,
        IReadOnlyList<BackupLogicalFile> logicalFiles,
        List<(string SqlPath, string WorkerPath)> restoreFiles)
    {
        TargetSqlAcceptanceEnvironment.EnsureRestoreDatabaseName(restoreDatabase);
        if (logicalFiles.Count != restoreFiles.Count)
        {
            throw new InvalidOperationException("P5.6 Restore 文件映射数量不一致。");
        }

        try
        {
            using var credential = environment.CreateCredentialLease();
            await using var connection = SqlClientTargetSqlConnectionFactory.Create(
                connectionInput,
                credential);
            await connection.OpenAsync();
            await AssertDatabaseDoesNotExistAsync(connection, restoreDatabase);
            var moveClauses = logicalFiles
                .Select((file, index) =>
                    $"MOVE {QuoteSqlString(file.LogicalName)} TO {QuoteSqlString(restoreFiles[index].SqlPath)}")
                .ToArray();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"RESTORE DATABASE {QuoteIdentifier(restoreDatabase)} FROM DISK = @backupPath WITH RECOVERY, {string.Join(", ", moveClauses)};";
            command.CommandTimeout = 3_600;
            command.Parameters.Add("@backupPath", SqlDbType.NVarChar, 2_048).Value = backupPath;
            await command.ExecuteNonQueryAsync();
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (TargetSqlClientException.CanClassify(exception))
        {
            throw new InvalidOperationException("P5.6 隔离 Restore 执行失败；原始目标信息未输出。");
        }
    }

    private static async Task AssertDatabaseDoesNotExistAsync(
        SqlConnection connection,
        string databaseName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM [sys].[databases] WHERE [name] = @name;";
        command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = databaseName;
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        if (count != 0)
        {
            throw new InvalidOperationException("P5.6 隔离 Restore 数据库名称发生碰撞；已拒绝覆盖。");
        }
    }

    private static async Task AssertDatabaseOnlineAsync(
        TargetSqlAcceptanceEnvironment environment,
        TargetSqlConnectionInput connectionInput,
        string databaseName)
    {
        try
        {
            using var credential = environment.CreateCredentialLease();
            await using var connection = SqlClientTargetSqlConnectionFactory.Create(
                connectionInput,
                credential);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT [state_desc] FROM [sys].[databases] WHERE [name] = @name;";
            command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = databaseName;
            var state = Convert.ToString(
                await command.ExecuteScalarAsync(),
                CultureInfo.InvariantCulture);
            if (!string.Equals(state, "ONLINE", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("P5.6 隔离 Restore 数据库未进入 ONLINE 状态。");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (TargetSqlClientException.CanClassify(exception))
        {
            throw new InvalidOperationException("P5.6 无法核对隔离 Restore 数据库状态。");
        }
    }

    private static async Task<bool> CleanupAsync(
        TargetSqlAcceptanceEnvironment environment,
        string restoreDatabase,
        string backupWorkerPath,
        IEnumerable<(string SqlPath, string WorkerPath)> restoreFiles)
    {
        var succeeded = await TryDropRestoreDatabaseAsync(environment, restoreDatabase);
        foreach (var restoreFile in restoreFiles)
        {
            try
            {
                environment.DeleteGeneratedFile(restoreFile.WorkerPath);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                succeeded = false;
            }
        }

        try
        {
            environment.DeleteGeneratedFile(backupWorkerPath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            succeeded = false;
        }

        return succeeded;
    }

    private static async Task<bool> TryDropRestoreDatabaseAsync(
        TargetSqlAcceptanceEnvironment environment,
        string restoreDatabase)
    {
        TargetSqlAcceptanceEnvironment.EnsureRestoreDatabaseName(restoreDatabase);
        try
        {
            var credentialReferenceId = Guid.NewGuid();
            var connectionInput = environment.CreateConnectionInput(credentialReferenceId);
            using var credential = environment.CreateCredentialLease();
            await using var connection = SqlClientTargetSqlConnectionFactory.Create(
                connectionInput,
                credential);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            var quotedName = QuoteIdentifier(restoreDatabase);
            command.CommandText =
                $"IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE {quotedName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quotedName}; END;";
            command.CommandTimeout = 300;
            command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = restoreDatabase;
            await command.ExecuteNonQueryAsync();
            return true;
        }
        catch (Exception exception) when (exception is SqlException
            or InvalidOperationException
            or TimeoutException)
        {
            return false;
        }
    }

    private static void EnsureFileDoesNotExist(string path)
    {
        if (File.Exists(path))
        {
            throw new InvalidOperationException("P5.6 本轮生成的备份文件名发生碰撞；已拒绝覆盖。");
        }
    }

    private static long RequireNonEmptyFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= 0)
            {
                throw new InvalidOperationException("P5.6 Worker 侧未观察到非空备份文件。");
            }

            return file.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("P5.6 Worker 侧无法安全读取本轮备份文件元数据。");
        }
    }

    private static string QuoteIdentifier(string identifier)
    {
        TargetSqlAcceptanceEnvironment.EnsureRestoreDatabaseName(identifier);
        return $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    private static string QuoteSqlString(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(char.IsControl))
        {
            throw new InvalidOperationException("P5.6 Restore 文件映射包含无效文本。");
        }

        return $"N'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private sealed record BackupLogicalFile(string LogicalName, char FileType);
}
