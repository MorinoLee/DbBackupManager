using System.Text.Json;
using DbBackupManager.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class ExternalJsonConfigurationTests : IDisposable
{
    private const string RootPrefix = "DbBackupManagerExternalConfigTests_";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"{RootPrefix}{Guid.NewGuid():N}");

    [Fact]
    public void MissingPathLeavesExistingConfigurationUnchanged()
    {
        var configuration = new ConfigurationManager();
        configuration["Marker"] = "existing";

        ExternalJsonConfiguration.AddIfConfigured(configuration, AppContext.BaseDirectory);

        Assert.Equal("existing", configuration["Marker"]);
    }

    [Fact]
    public void AbsoluteJsonOutsideApplicationDirectoryIsLoadedWithoutReload()
    {
        var applicationDirectory = CreateDirectory("application");
        var configurationDirectory = CreateDirectory("configuration");
        var configurationPath = Path.Combine(configurationDirectory, "web.json");
        File.WriteAllText(configurationPath, "{\"Marker\":\"external\"}");
        var configuration = new ConfigurationManager
        {
            [ExternalJsonConfiguration.PathKey] = configurationPath,
            ["Marker"] = "existing",
        };

        ExternalJsonConfiguration.AddIfConfigured(configuration, applicationDirectory);

        Assert.Equal("external", configuration["Marker"]);
    }

    [Theory]
    [InlineData("relative.json")]
    [InlineData("C:\\configuration\\settings.txt")]
    public void RelativeOrNonJsonPathIsRejected(string configuredPath)
    {
        var configuration = new ConfigurationManager
        {
            [ExternalJsonConfiguration.PathKey] = configuredPath,
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            ExternalJsonConfiguration.AddIfConfigured(configuration, AppContext.BaseDirectory));

        Assert.Equal("DbBackupManager 外部配置必须是绝对 JSON 文件路径。", error.Message);
    }

    [Fact]
    public void ConfigurationInsideApplicationDirectoryIsRejected()
    {
        var applicationDirectory = CreateDirectory("application");
        var configurationPath = Path.Combine(applicationDirectory, "settings.json");
        File.WriteAllText(configurationPath, "{}");
        var configuration = new ConfigurationManager
        {
            [ExternalJsonConfiguration.PathKey] = configurationPath,
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            ExternalJsonConfiguration.AddIfConfigured(configuration, applicationDirectory));

        Assert.Equal("DbBackupManager 外部配置不能位于程序发布目录中。", error.Message);
    }

    [Fact]
    public void MissingOrMalformedFileUsesStableFailureWithoutPath()
    {
        var applicationDirectory = CreateDirectory("application");
        var configurationDirectory = CreateDirectory("configuration");
        var missingPath = Path.Combine(configurationDirectory, "missing.json");
        var malformedPath = Path.Combine(configurationDirectory, "malformed.json");
        File.WriteAllText(malformedPath, "{");

        foreach (var configurationPath in new[] { missingPath, malformedPath })
        {
            var configuration = new ConfigurationManager
            {
                [ExternalJsonConfiguration.PathKey] = configurationPath,
            };

            var error = Assert.Throws<InvalidOperationException>(() =>
                ExternalJsonConfiguration.AddIfConfigured(configuration, applicationDirectory));

            Assert.Equal("DbBackupManager 外部配置无法读取或格式无效。", error.Message);
            Assert.DoesNotContain(configurationPath, error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Dispose()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var resolvedRoot = Path.GetFullPath(_root);
        if (!resolvedRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolvedRoot).StartsWith(RootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("拒绝删除不属于外部配置测试的临时目录。");
        }

        if (Directory.Exists(resolvedRoot))
        {
            Directory.Delete(resolvedRoot, recursive: true);
        }
    }

    private string CreateDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
