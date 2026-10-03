using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace DbBackupManager.Infrastructure.BackupExecution;

internal sealed class WorkerStartupDiagnostics(IConfiguration configuration) : IWorkerStartupDiagnostics
{
    public string? CheckConfiguration()
    {
        var connection = configuration.GetConnectionString("PlatformDatabase");
        if (string.IsNullOrWhiteSpace(connection)) return "worker_platform_configuration_missing";
        try
        {
            var parsed = new SqlConnectionStringBuilder(connection);
            if (string.IsNullOrWhiteSpace(parsed.DataSource) || string.IsNullOrWhiteSpace(parsed.InitialCatalog))
                return "worker_platform_configuration_invalid";
        }
        catch (ArgumentException) { return "worker_platform_configuration_invalid"; }

        var path = configuration[BusinessCredentialDataProtector.KeyRingPathConfigurationKey];
        if (string.IsNullOrWhiteSpace(path)) return "worker_business_keys_missing";
        try
        {
            if (!Path.IsPathFullyQualified(path)) return "worker_business_keys_path_invalid";
            // 只检查已有密钥文件的可读性，不生成密钥，也不输出路径或密钥内容。
            var keys = Directory.EnumerateFiles(path, "key-*.xml").ToArray();
            if (keys.Length == 0) return "worker_business_keys_empty";
            foreach (var key in keys)
            {
                using var stream = File.Open(key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.ReadByte() < 0) return "worker_business_keys_unreadable";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return "worker_business_keys_unreadable"; }
        return null;
    }
}
