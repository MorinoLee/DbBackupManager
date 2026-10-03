using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace DbBackupManager.Infrastructure.Hosting;

public static class ExternalJsonConfiguration
{
    public const string PathKey = "DbBackupManager:ExternalConfigurationPath";

    public static void AddIfConfigured(ConfigurationManager configuration, string applicationBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDirectory);

        var configuredPath = configuration[PathKey];
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return;
        }

        if (!Path.IsPathFullyQualified(configuredPath)
            || !string.Equals(Path.GetExtension(configuredPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("DbBackupManager 外部配置必须是绝对 JSON 文件路径。");
        }

        var resolvedPath = Path.GetFullPath(configuredPath);
        var resolvedBaseDirectory = Path.GetFullPath(applicationBaseDirectory);
        if (IsWithinDirectory(resolvedPath, resolvedBaseDirectory))
        {
            throw new InvalidOperationException("DbBackupManager 外部配置不能位于程序发布目录中。");
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(resolvedPath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("DbBackupManager 外部配置无法读取或格式无效。");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("DbBackupManager 外部配置不能是重解析点。");
        }

        try
        {
            configuration.AddJsonFile(resolvedPath, optional: false, reloadOnChange: false);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or InvalidOperationException
            or FormatException
            or JsonException)
        {
            throw new InvalidOperationException("DbBackupManager 外部配置无法读取或格式无效。");
        }
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var relativePath = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relativePath)
            && !relativePath.Equals("..", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
