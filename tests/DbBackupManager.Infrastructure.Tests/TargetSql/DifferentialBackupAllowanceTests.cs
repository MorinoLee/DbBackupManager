using DbBackupManager.Application.TargetSql;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class DifferentialBackupAllowanceTests
{
    [Theory]
    [InlineData("10.50.1600.1")]
    [InlineData("15.0.2000.5")]
    [InlineData("16.0.1000.6")]
    public void BuiltInListStartsEmpty(string productVersion)
    {
        Assert.False(DifferentialBackupAllowance.None.Allows(Server(productVersion)));
    }

    [Theory]
    [InlineData("15.0.2000.5", 2, true)]
    [InlineData("15.0.9999.1", 3, true)]
    [InlineData("15.0.9999.1", 4, true)]
    [InlineData("15.1.2000.5", 3, false)]
    [InlineData("16.0.1000.6", 3, false)]
    public void OverrideMatchesOnlyMajorMinorAndLogsItsReason(string productVersion, int edition, bool allowed)
    {
        var logger = new RecordingLogger();
        var allowance = new DifferentialBackupAllowance(Configuration("15.0", "专用环境验收差异备份"), logger);

        Assert.Equal(allowed, allowance.Allows(Server(productVersion, edition)));
        if (allowed)
        {
            var message = Assert.Single(logger.Messages);
            Assert.Contains("15.0", message, StringComparison.Ordinal);
            Assert.Contains("专用环境验收差异备份", message, StringComparison.Ordinal);
            Assert.Equal(6101, Assert.Single(logger.EventIds));
        }
        else
        {
            Assert.Empty(logger.Messages);
        }
    }

    [Theory]
    [InlineData("15.0", null)]
    [InlineData("15.0", " ")]
    [InlineData("15.0", "换行\n原因")]
    [InlineData(null, "合成原因")]
    [InlineData("15", "合成原因")]
    [InlineData("15.0.2000.5", "合成原因")]
    [InlineData("invalid", "合成原因")]
    public void RegistrationRejectsMalformedOverrideBeforeAnyDatabaseWork(string? version, string? reason)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddTargetSqlAdapter(Configuration(version, reason)));
    }

    [Fact]
    public void DuplicateOverrideIsRejected()
    {
        var values = new Dictionary<string, string?>
        {
            [$"{DifferentialBackupAllowance.ConfigurationSection}:0:Version"] = "15.0",
            [$"{DifferentialBackupAllowance.ConfigurationSection}:0:Reason"] = "合成原因",
            [$"{DifferentialBackupAllowance.ConfigurationSection}:1:Version"] = "15.0",
            [$"{DifferentialBackupAllowance.ConfigurationSection}:1:Reason"] = "另一合成原因",
        };
        Assert.Throws<InvalidOperationException>(() => new DifferentialBackupAllowance(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(), NullLogger<DifferentialBackupAllowance>.Instance));
    }

    private static IConfiguration Configuration(string? version, string? reason) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{DifferentialBackupAllowance.ConfigurationSection}:0:Version"] = version,
            [$"{DifferentialBackupAllowance.ConfigurationSection}:0:Reason"] = reason,
        }).Build();

    private static TargetSqlServerInfo Server(string productVersion, int edition = 3) =>
        new(productVersion, "Synthetic", "Synthetic", edition, Version.Parse(productVersion).Major, true);

    private sealed class RecordingLogger : ILogger<DifferentialBackupAllowance>
    {
        public List<string> Messages { get; } = [];
        public List<int> EventIds { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Equal(LogLevel.Warning, logLevel);
            Messages.Add(formatter(state, exception));
            EventIds.Add(eventId.Id);
        }
    }
}
