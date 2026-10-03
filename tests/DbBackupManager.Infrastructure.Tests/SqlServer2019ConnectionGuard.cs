using System.Globalization;
using Microsoft.Data.SqlClient;

namespace DbBackupManager.Infrastructure.Tests;

/// <summary>
/// 校验 P3.2 外部 SQL Server 2019 测试连接的安全与版本边界；绝不记录完整连接字符串。
/// </summary>
internal static class SqlServer2019ConnectionGuard
{
    public static SqlConnectionStringBuilder ParseAndValidateConfiguration(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "P3.2 测试连接字符串无法解析；请检查环境变量格式，勿在日志中打印其值。",
                exception);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "P3.2 测试连接字符串无法解析；请检查环境变量格式，勿在日志中打印其值。",
                exception);
        }

        // SqlClient 5+：Encrypt 为 SqlConnectionEncryptOption；true/Mandatory/Strict 均要求加密。
        if (builder.Encrypt is null || builder.Encrypt == SqlConnectionEncryptOption.Optional)
        {
            throw new InvalidOperationException(
                "P3.2 拒绝不安全连接：Encrypt 必须为 True/Mandatory/Strict，且不会自动降级。");
        }

        if (builder.TrustServerCertificate)
        {
            throw new InvalidOperationException(
                "P3.2 拒绝不安全连接：TrustServerCertificate 必须为 False，且不会自动降级。");
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            throw new InvalidOperationException("P3.2 测试连接缺少服务器地址。");
        }

        return builder;
    }

    public static string BuildMasterConnectionString(SqlConnectionStringBuilder validated)
    {
        var master = new SqlConnectionStringBuilder(validated.ConnectionString)
        {
            InitialCatalog = "master",
        };
        return master.ConnectionString;
    }

    public static string BuildDatabaseConnectionString(SqlConnectionStringBuilder validated, string databaseName)
    {
        EnsureAllowedDatabaseName(databaseName);

        var database = new SqlConnectionStringBuilder(validated.ConnectionString)
        {
            InitialCatalog = databaseName,
        };
        return database.ConnectionString;
    }

    public static void EnsureAllowedDatabaseName(string databaseName)
    {
        if (!databaseName.StartsWith(
                SqlServer2019AcceptanceConstants.SqlServer2019DatabaseNamePrefix,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"拒绝操作不属于 P3.2 的数据库名称前缀。期望前缀：{SqlServer2019AcceptanceConstants.SqlServer2019DatabaseNamePrefix}");
        }
    }

    public static async Task AssertEngineAndEncryptionAsync(
        SqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText =
                "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int);";
            var major = (int)(await versionCommand.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("无法读取 SQL Server 产品主版本。"));

            if (major != SqlServer2019AcceptanceConstants.RequiredProductMajorVersion)
            {
                throw new InvalidOperationException(
                    $"P3.2 要求 SQL Server 产品主版本为 {SqlServer2019AcceptanceConstants.RequiredProductMajorVersion}（2019），实际为 {major}；不会改用其他版本冒充通过。");
            }
        }

        await using (var encryptCommand = connection.CreateCommand())
        {
            encryptCommand.CommandText =
                """
                SELECT encrypt_option
                FROM sys.dm_exec_connections
                WHERE session_id = @@SPID;
                """;
            var encryptOption = Convert.ToString(
                await encryptCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);

            if (!string.Equals(encryptOption, "TRUE", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "P3.2 要求当前会话实际加密（encrypt_option=TRUE）；检测到未加密或无法确认，已中止。");
            }
        }
    }

    public static async Task<(int MajorVersion, string ProductLevel, string ProductVersion)> ReadVersionAsync(
        SqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                CAST(SERVERPROPERTY('ProductMajorVersion') AS int),
                CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(128)),
                CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128));
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("无法读取 SQL Server 版本属性。");
        }

        return (reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
    }
}
