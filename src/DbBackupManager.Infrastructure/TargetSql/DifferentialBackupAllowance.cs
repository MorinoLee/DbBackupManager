using System.Globalization;
using DbBackupManager.Application.TargetSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbBackupManager.Infrastructure.TargetSql;

internal sealed class DifferentialBackupAllowance
{
    internal const string ConfigurationSection = "TargetSql:DifferentialBackupAcceptance:Overrides";

    // 卡 12 人工验收通过后，按产品主次版本号登记；默认不继承 FULL 的验收结论。
    private static readonly HashSet<string> ValidatedVersions = new(StringComparer.Ordinal);
    internal static DifferentialBackupAllowance None { get; } = new(
        new ConfigurationBuilder().Build(), NullLogger<DifferentialBackupAllowance>.Instance);

    private readonly Dictionary<string, string> _overrides = new(StringComparer.Ordinal);
    private readonly ILogger<DifferentialBackupAllowance> _logger;
    private static readonly Action<ILogger, string, string, Exception?> ExplicitAllowance = LoggerMessage.Define<string, string>(
        LogLevel.Warning, new EventId(6101, "DifferentialBackupAcceptanceOverride"),
        "验收环境显式放行差异备份：SQL Server 产品版本 {ProductVersion}，原因：{Reason}");

    public DifferentialBackupAllowance(IConfiguration configuration, ILogger<DifferentialBackupAllowance> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        foreach (var item in configuration.GetSection(ConfigurationSection).GetChildren())
        {
            var key = item["Version"]?.Trim();
            var reason = item["Reason"]?.Trim();
            if (!Version.TryParse(key, out var version) || version.Major <= 0 || version.Build != -1
                || key != VersionKey(version) || string.IsNullOrWhiteSpace(reason) || reason.Length > 500
                || reason.Any(char.IsControl) || !_overrides.TryAdd(key, reason))
            {
                throw new InvalidOperationException(
                    "差异备份验收放行项必须包含唯一的产品主次版本号和非空原因（最多 500 字且无控制字符）。");
            }
        }
    }

    public bool Allows(TargetSqlServerInfo server)
    {
        if (!Version.TryParse(server.ProductVersion, out var version))
        {
            return false;
        }

        var key = VersionKey(version);
        if (ValidatedVersions.Contains(key))
        {
            return true;
        }

        if (!_overrides.TryGetValue(key, out var reason))
        {
            return false;
        }

        ExplicitAllowance(_logger, key, reason, null);
        return true;
    }

    private static string VersionKey(Version version) =>
        string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}");
}
