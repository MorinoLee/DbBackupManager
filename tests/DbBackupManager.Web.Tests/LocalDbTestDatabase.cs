using Microsoft.Data.SqlClient;

namespace DbBackupManager.Web.Tests;

/// <summary>
/// P4 Web 主机测试的 LocalDB 拆库辅助：强制单用户后删除，避免池化连接导致“数据库正在使用”，并对瞬时失败做有限重试。
/// </summary>
internal static class LocalDbTestDatabase
{
    private const string DatabaseNamePrefix = "DbBackupManagerP4WebTests_";
    private const int MaxAttempts = 3;

    public static async Task DropAsync(string databaseName)
    {
        if (!databaseName.StartsWith(DatabaseNamePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("拒绝删除不属于 P4 Web 测试的数据库。");
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(
                    "Server=(localdb)\\MSSQLLocalDB;Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Pooling=false");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                var safeName = databaseName.Replace("]", "]]", StringComparison.Ordinal);
                command.CommandText =
                    $"""
                    IF DB_ID(@name) IS NOT NULL
                    BEGIN
                        ALTER DATABASE [{safeName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                        DROP DATABASE [{safeName}];
                    END;
                    """;
                command.Parameters.AddWithValue("@name", databaseName);
                await command.ExecuteNonQueryAsync();
                return;
            }
            catch (SqlException) when (attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt));
            }
        }
    }
}
