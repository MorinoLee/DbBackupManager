using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

/// <summary>
/// 环境驱动的 Platform DB SQL 验收夹具：
/// 未设置环境变量时走 LocalDB 回归；设置后走 SQL Server 2019 最低版本验收。
/// </summary>
public sealed class PlatformDatabaseSqlServerFixture : IAsyncLifetime
{
    private readonly string _databaseName;
    private readonly SqlConnectionStringBuilder? _sqlServer2019Builder;
    private readonly bool _useSqlServer2019;

    public PlatformDatabaseSqlServerFixture()
    {
        var configured = Environment.GetEnvironmentVariable(
            SqlServer2019AcceptanceConstants.ConnectionEnvironmentVariable);

        _useSqlServer2019 = !string.IsNullOrWhiteSpace(configured);
        if (_useSqlServer2019)
        {
            _sqlServer2019Builder = SqlServer2019ConnectionGuard.ParseAndValidateConfiguration(configured!);
            _databaseName =
                $"{SqlServer2019AcceptanceConstants.SqlServer2019DatabaseNamePrefix}{Guid.NewGuid():N}";
            Mode = PlatformDatabaseTestMode.SqlServer2019;
        }
        else
        {
            _databaseName =
                $"{SqlServer2019AcceptanceConstants.LocalDbDatabaseNamePrefix}{Guid.NewGuid():N}";
            Mode = PlatformDatabaseTestMode.LocalDb;
        }
    }

    public PlatformDatabaseTestMode Mode { get; }

    public string DatabaseNamePrefix => _useSqlServer2019
        ? SqlServer2019AcceptanceConstants.SqlServer2019DatabaseNamePrefix
        : SqlServer2019AcceptanceConstants.LocalDbDatabaseNamePrefix;

    public PlatformDbContext CreateContext(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(
                CreateConnectionString(),
                sqlServer => sqlServer.UseCompatibilityLevel(
                    PlatformDatabaseServiceCollectionExtensions.CompatibilityLevel));
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return new PlatformDbContext(builder.Options);
    }

    public ServiceProvider CreateServiceProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{PlatformDatabaseServiceCollectionExtensions.ConnectionStringName}"] =
                    CreateConnectionString(),
            })
            .Build();
        var services = new ServiceCollection();
        services.AddPlatformDatabase(configuration);
        return services.BuildServiceProvider();
    }

    public async Task ClearIdentityDataAsync()
    {
        await using var context = CreateContext();
        await context.AuditRecords.ExecuteDeleteAsync();
        await context.AdminUsers.ExecuteDeleteAsync();
    }

    public async Task ClearBackupTaskDataAsync()
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM [NotificationOutbox];
            DELETE FROM [TaskEvents];
            DELETE FROM [BackupFileStateChanges];
            DELETE FROM [BackupFiles];
            DELETE FROM [BackupTaskStateChanges];
            DELETE FROM [BackupTaskSnapshots];
            UPDATE [BackupTasks]
            SET [Status] = 'Pending',
                [CurrentStage] = 'Backup',
                [CurrentBackupAttemptId] = NULL,
                [StartedAtUtc] = NULL,
                [CompletedAtUtc] = NULL,
                [CancellationRequestedAtUtc] = NULL,
                [ReconciliationAttemptCount] = 0,
                [NextReconciliationAtUtc] = NULL,
                [ErrorCode] = NULL,
                [ErrorMessage] = NULL,
                [LeasePurpose] = NULL,
                [LeaseToken] = NULL,
                [LeaseOwner] = NULL,
                [LeaseAcquiredAtUtc] = NULL,
                [LeaseExpiresAtUtc] = NULL;
            DELETE FROM [BackupAttempts];
            DELETE FROM [BackupTasks];
            """);
    }

    public async Task InitializeAsync()
    {
        if (_useSqlServer2019)
        {
            await CreateSqlServer2019DatabaseAsync();
        }

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_useSqlServer2019)
        {
            await DropSqlServer2019DatabaseAsync();
            return;
        }

        if (!_databaseName.StartsWith(
                SqlServer2019AcceptanceConstants.LocalDbDatabaseNamePrefix,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("拒绝删除不属于 P3 LocalDB 测试的数据库。");
        }

        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(LocalDbMasterConnectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                var safeName = _databaseName.Replace("]", "]]", StringComparison.Ordinal);
                command.CommandText =
                    $"""
                    IF DB_ID(@name) IS NOT NULL
                    BEGIN
                        ALTER DATABASE [{safeName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                        DROP DATABASE [{safeName}];
                    END;
                    """;
                command.Parameters.AddWithValue("@name", _databaseName);
                await command.ExecuteNonQueryAsync();
                return;
            }
            catch (SqlException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt));
            }
        }
    }

    private async Task CreateSqlServer2019DatabaseAsync()
    {
        SqlServer2019ConnectionGuard.EnsureAllowedDatabaseName(_databaseName);

        var masterConnectionString = SqlServer2019ConnectionGuard.BuildMasterConnectionString(
            _sqlServer2019Builder!);

        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync();
        await SqlServer2019ConnectionGuard.AssertEngineAndEncryptionAsync(connection);

        await using (var existsCommand = connection.CreateCommand())
        {
            existsCommand.CommandText =
                """
                SELECT COUNT(1)
                FROM sys.databases
                WHERE name = @name;
                """;
            existsCommand.Parameters.AddWithValue("@name", _databaseName);
            var exists = (int)(await existsCommand.ExecuteScalarAsync() ?? 0);
            if (exists != 0)
            {
                throw new InvalidOperationException("P3.2 临时数据库名称冲突，已中止创建。");
            }
        }

        await using (var createCommand = connection.CreateCommand())
        {
            // 名称已由前缀白名单校验；避免拼接业务库名。
            createCommand.CommandText = $"CREATE DATABASE [{_databaseName.Replace("]", "]]", StringComparison.Ordinal)}];";
            await createCommand.ExecuteNonQueryAsync();
        }
    }

    private async Task DropSqlServer2019DatabaseAsync()
    {
        SqlServer2019ConnectionGuard.EnsureAllowedDatabaseName(_databaseName);

        var masterConnectionString = SqlServer2019ConnectionGuard.BuildMasterConnectionString(
            _sqlServer2019Builder!);

        try
        {
            await using var connection = new SqlConnection(masterConnectionString);
            await connection.OpenAsync();
            await SqlServer2019ConnectionGuard.AssertEngineAndEncryptionAsync(connection);

            await using (var belongsCommand = connection.CreateCommand())
            {
                belongsCommand.CommandText =
                    """
                    SELECT COUNT(1)
                    FROM sys.databases
                    WHERE name = @name;
                    """;
                belongsCommand.Parameters.AddWithValue("@name", _databaseName);
                var exists = (int)(await belongsCommand.ExecuteScalarAsync() ?? 0);
                if (exists == 0)
                {
                    return;
                }
            }

            await using (var dropCommand = connection.CreateCommand())
            {
                var safeName = _databaseName.Replace("]", "]]", StringComparison.Ordinal);
                dropCommand.CommandText =
                    $"""
                    ALTER DATABASE [{safeName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{safeName}];
                    """;
                await dropCommand.ExecuteNonQueryAsync();
            }
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "P3.2 清理临时数据库失败；请人工确认仅残留 DbBackupManagerP32Sql2019_ 前缀库。",
                exception);
        }
    }

    private const string LocalDbMasterConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Pooling=false";

    private static string BuildLocalDbConnectionString(string databaseName)
    {
        return
            $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Pooling=false";
    }

    private string CreateConnectionString()
    {
        return _useSqlServer2019
            ? SqlServer2019ConnectionGuard.BuildDatabaseConnectionString(
                _sqlServer2019Builder!,
                _databaseName)
            : BuildLocalDbConnectionString(_databaseName);
    }
}

public enum PlatformDatabaseTestMode
{
    LocalDb,
    SqlServer2019,
}
