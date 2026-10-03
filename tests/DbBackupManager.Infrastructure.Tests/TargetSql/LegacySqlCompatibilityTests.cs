using DbBackupManager.Application.TargetSql;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.Data.SqlClient;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class LegacySqlCompatibilityTests
{
    [Theory]
    [InlineData("10.50.1600.1", false, false)]
    [InlineData("10.50.1600.1", true, true)]
    [InlineData("10.50.6000.34", true, true)]
    [InlineData("10.50.1599.0", true, false)]
    [InlineData("10.0.6000.29", true, false)]
    [InlineData("10.50.6542.0", false, true)]
    public void OptInOnlyExtendsThe2008R2BuildRange(string version, bool enabled, bool expected)
    {
        var info = TargetSqlServerInfoMapper.Map(version, "synthetic", "synthetic", 3);
        Assert.Equal(expected, TargetSqlServerInfoMapper.IsSupported(info, enabled));
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(true, " ")]
    [InlineData(false, "old server")]
    [InlineData(true, "invalid\nreason")]
    public void ExceptionRequiresAnExplicitValidReason(bool enabled, string? reason) =>
        Assert.Throws<ArgumentException>(() => new TargetSqlConnectionInput(
            "synthetic", Guid.NewGuid(), true, false, null, 15, enabled, reason));

    [Fact]
    public void LegacyOptInDoesNotDisableEncryptionOrCertificateValidation()
    {
        var input = new TargetSqlConnectionInput("synthetic", Guid.NewGuid(), true, false, null, 15, true, "legacy fixture");
        using var credential = TargetSqlCredentialLease.CreateAndClear("synthetic", "synthetic-password".ToCharArray());
        using var connection = SqlClientTargetSqlConnectionFactory.Create(input, credential);
        var options = new SqlConnectionStringBuilder(connection.ConnectionString);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, options.Encrypt);
        Assert.False(options.TrustServerCertificate);
        Assert.False(options.Pooling);
        Assert.Equal(0, options.ConnectRetryCount);
    }
}
