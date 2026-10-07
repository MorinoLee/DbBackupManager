using System.Data;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.Configuration;
using Microsoft.Data.SqlClient;

namespace DbBackupManager.Infrastructure.TargetSql;

internal static class TargetSqlCommandFactory
{
    internal const int ProbeCommandTimeoutSeconds = 15;

    internal const int DiscoveryCommandTimeoutSeconds = 30;

    internal const string ServerProbeCommandText = """
        SELECT
            CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
            CONVERT(nvarchar(128), SERVERPROPERTY('ProductLevel')),
            CONVERT(nvarchar(300), SERVERPROPERTY('Edition')),
            CONVERT(int, SERVERPROPERTY('EngineEdition'));
        """;

    internal const string DatabaseDiscoveryCommandText = """
        SELECT
            [name],
            [state_desc],
            [recovery_model_desc],
            CONVERT(bit, CASE WHEN [database_id] <= 4 THEN 1 ELSE 0 END),
            CONVERT(bit, CASE WHEN [source_database_id] IS NOT NULL THEN 1 ELSE 0 END)
        FROM [sys].[databases]
        ORDER BY [name];
        """;

    internal const string BackupIdentityCommandText = """
        SELECT TOP (1)
            CONVERT(bit, CASE WHEN [bs].[database_name] = @databaseName THEN 1 ELSE 0 END),
            CONVERT(bit, CASE WHEN [bs].[type] = N'D' THEN 1 ELSE 0 END),
            CONVERT(bit, CASE WHEN [bs].[backup_finish_date] IS NULL THEN 0 ELSE 1 END)
        FROM [msdb].[dbo].[backupset] AS [bs]
        INNER JOIN [msdb].[dbo].[backupmediafamily] AS [bmf]
            ON [bs].[media_set_id] = [bmf].[media_set_id]
        WHERE [bmf].[physical_device_name] = @backupPath
        ORDER BY [bs].[backup_set_id] DESC;
        """;

    public static SqlCommand CreateServerProbe(SqlConnection connection)
    {
        return CreateTextCommand(
            connection,
            ServerProbeCommandText,
            ProbeCommandTimeoutSeconds);
    }

    public static SqlCommand CreateDatabaseDiscovery(SqlConnection connection)
    {
        return CreateTextCommand(
            connection,
            DatabaseDiscoveryCommandText,
            DiscoveryCommandTimeoutSeconds);
    }

    public static SqlCommand CreateFullBackup(
        SqlConnection connection,
        TargetSqlFullBackupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return CreateDatabaseBackup(connection, request.DatabaseName, request.LocalSqlFilePath, BackupType.Full,
            request.UseCopyOnly, request.UseChecksum, request.UseCompression, request.CommandTimeoutSeconds);
    }

    public static SqlCommand CreateBackup(SqlConnection connection, TargetSqlBackupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CreateDatabaseBackup(connection, request.DatabaseName, request.LocalSqlFilePath, request.BackupType,
            request.UseCopyOnly, request.UseChecksum, request.UseCompression, request.CommandTimeoutSeconds);
    }

    internal static SqlCommand CreateDatabaseBackup(
        SqlConnection connection, string databaseName, string localSqlFilePath, BackupType backupType,
        bool useCopyOnly, bool useChecksum, bool useCompression, int commandTimeoutSeconds)
    {
        if (backupType is not (BackupType.Full or BackupType.Differential))
        {
            throw new ArgumentException("当前命令工厂只支持完整和差异数据库备份。", nameof(backupType));
        }

        if (backupType == BackupType.Differential && useCopyOnly)
        {
            throw new ArgumentException("差异备份不能同时使用 COPY_ONLY。", nameof(useCopyOnly));
        }

        var options = new List<string>();
        if (backupType == BackupType.Differential)
        {
            options.Add("DIFFERENTIAL");
        }

        if (useCopyOnly)
        {
            options.Add("COPY_ONLY");
        }

        if (useChecksum)
        {
            options.Add("CHECKSUM");
        }

        if (useCompression)
        {
            options.Add("COMPRESSION");
        }

        var withClause = options.Count == 0
            ? string.Empty
            : $"\nWITH {string.Join(", ", options)}";
        var command = CreateTextCommand(
            connection,
            $"BACKUP DATABASE {QuoteIdentifier(databaseName)}\nTO DISK = @backupPath{withClause};",
            commandTimeoutSeconds);
        AddBackupPathParameter(command, localSqlFilePath);
        return command;
    }

    public static SqlCommand CreateBackupVerification(
        SqlConnection connection,
        TargetSqlBackupVerificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var checksumClause = request.UseChecksum ? "\nWITH CHECKSUM" : string.Empty;
        var command = CreateTextCommand(
            connection,
            $"RESTORE VERIFYONLY\nFROM DISK = @backupPath{checksumClause};",
            request.CommandTimeoutSeconds);
        AddBackupPathParameter(command, request.LocalSqlFilePath);
        return command;
    }

    public static SqlCommand CreateBackupIdentityInspection(
        SqlConnection connection,
        TargetSqlBackupIdentityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var command = CreateTextCommand(
            connection,
            BackupIdentityCommandText,
            request.CommandTimeoutSeconds);
        command.Parameters.Add("@databaseName", SqlDbType.NVarChar, 128).Value = request.DatabaseName;
        AddBackupPathParameter(command, request.LocalSqlFilePath);
        return command;
    }

    internal static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)
            || identifier.Length > 128
            || identifier.Any(char.IsControl))
        {
            throw new ArgumentException(
                "SQL 标识符不能为空、不能包含控制字符且长度不能超过 128。",
                nameof(identifier));
        }

        return $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    private static SqlCommand CreateTextCommand(
        SqlConnection connection,
        string commandText,
        int commandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return new SqlCommand(commandText, connection)
        {
            CommandType = CommandType.Text,
            CommandTimeout = commandTimeoutSeconds,
        };
    }

    private static void AddBackupPathParameter(SqlCommand command, string localSqlFilePath)
    {
        command.Parameters.Add("@backupPath", SqlDbType.NVarChar, 2_048).Value = localSqlFilePath;
    }
}
