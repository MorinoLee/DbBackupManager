namespace DbBackupManager.Infrastructure.Tests;

internal static class SqlServer2019AcceptanceConstants
{
    public const string ConnectionEnvironmentVariable = "DBBACKUPMANAGER_TEST_SQLSERVER2019_CONNECTION";

    public const string LocalDbDatabaseNamePrefix = "DbBackupManagerP3Tests_";

    public const string SqlServer2019DatabaseNamePrefix = "DbBackupManagerP32Sql2019_";

    public const int RequiredProductMajorVersion = 15;
}
