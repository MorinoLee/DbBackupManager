using DbBackupManager.Infrastructure.BackupExecution;
using Microsoft.Extensions.Configuration;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class WorkerStartupDiagnosticsTests
{
    [Theory]
    [InlineData(null, null, "worker_platform_configuration_missing")]
    [InlineData("synthetic-invalid", null, "worker_platform_configuration_invalid")]
    [InlineData("Server=synthetic;Database=synthetic", null, "worker_business_keys_missing")]
    [InlineData("Server=synthetic;Database=synthetic", "relative", "worker_business_keys_path_invalid")]
    public void InvalidConfigurationFailsWithoutOpeningDatabaseOrExposingValues(string? connection, string? path, string expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PlatformDatabase"] = connection,
            ["DataProtection:BusinessCredentialKeyRingPath"] = path,
        }).Build();
        Assert.Equal(expected, new WorkerStartupDiagnostics(config).CheckConfiguration());
    }

    [Fact]
    public void ReadsExistingKeysWithoutGeneratingOrChangingFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DbBackupManagerStartup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        var key = Path.Combine(path, "key-synthetic.xml");
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PlatformDatabase"] = "Server=synthetic;Database=synthetic",
                ["DataProtection:BusinessCredentialKeyRingPath"] = path,
            }).Build();
            var diagnostics = new WorkerStartupDiagnostics(config);
            Assert.Equal("worker_business_keys_empty", diagnostics.CheckConfiguration());
            File.WriteAllText(key, "<synthetic />");
            Assert.Null(diagnostics.CheckConfiguration());
            Assert.Equal("<synthetic />", File.ReadAllText(key));
            Assert.Single(Directory.GetFiles(path));
        }
        finally { if (File.Exists(key)) File.Delete(key); Directory.Delete(path); }
    }
}
