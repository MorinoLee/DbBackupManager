using Microsoft.Data.SqlClient;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class TargetSqlAcceptanceEnvironmentTests : IDisposable
{
    private readonly string _workerRoot = Path.Combine(
        Path.GetTempPath(),
        $"{TargetSqlAcceptanceEnvironment.ControlledRootPrefix}Guard_{Guid.NewGuid():N}");

    [Fact]
    public void EmptyEnvironmentKeepsRealTargetMatrixDisabled()
    {
        var environment = TargetSqlAcceptanceEnvironment.Load(_ => null);

        Assert.Null(environment);
    }

    [Fact]
    public void PartialConfigurationFailsBeforeAnyTargetOperation()
    {
        var values = new Dictionary<string, string?>
        {
            [TargetSqlAcceptanceEnvironment.ExpectedProfileVariable] = "SqlServer2019",
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TargetSqlAcceptanceEnvironment.Load(values.GetValueOrDefault));

        Assert.Contains(TargetSqlAcceptanceEnvironment.ConnectionVariable, exception.Message);
        Assert.DoesNotContain("Password", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MutationIsDeniedByDefaultForCompleteSafeConfiguration()
    {
        Directory.CreateDirectory(_workerRoot);
        var values = CreateCompleteValues();

        var environment = TargetSqlAcceptanceEnvironment.Load(values.GetValueOrDefault);

        Assert.NotNull(environment);
        Assert.False(environment.AllowMutation);
        Assert.False(environment.TrustServerCertificate);
        Assert.Equal(TargetSqlAcceptanceProfile.SqlServer2019, environment.Profile);
    }

    [Fact]
    public void SqlPasswordEncryptionAndControlledCertificateExceptionAreRequired()
    {
        Directory.CreateDirectory(_workerRoot);
        var values = CreateCompleteValues();
        values[TargetSqlAcceptanceEnvironment.ConnectionVariable] =
            "Server=synthetic-target;User ID=synthetic-login;Password=synthetic-password;Encrypt=false";

        var encryption = Assert.Throws<InvalidOperationException>(() =>
            TargetSqlAcceptanceEnvironment.Load(values.GetValueOrDefault));

        Assert.Contains("加密", encryption.Message, StringComparison.Ordinal);

        values[TargetSqlAcceptanceEnvironment.ConnectionVariable] =
            "Server=synthetic-target;User ID=synthetic-login;Password=synthetic-password;Encrypt=true;TrustServerCertificate=true";
        var trust = Assert.Throws<InvalidOperationException>(() =>
            TargetSqlAcceptanceEnvironment.Load(values.GetValueOrDefault));

        Assert.Contains("例外", trust.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ProductionDatabase")]
    [InlineData("master")]
    public void ForeignDatabaseNamesAreRejected(string databaseName)
    {
        Directory.CreateDirectory(_workerRoot);
        var values = CreateCompleteValues();
        values[TargetSqlAcceptanceEnvironment.DatabaseVariable] = databaseName;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TargetSqlAcceptanceEnvironment.Load(values.GetValueOrDefault));

        Assert.Contains(TargetSqlAcceptanceEnvironment.TargetDatabasePrefix, exception.Message);
    }

    [Fact]
    public void GeneratedFileDeletionRejectsPathsOutsideControlledRoot()
    {
        Directory.CreateDirectory(_workerRoot);
        var environment = TargetSqlAcceptanceEnvironment.Load(
            CreateCompleteValues().GetValueOrDefault)!;
        var foreignPath = Path.Combine(
            Path.GetTempPath(),
            $"{TargetSqlAcceptanceEnvironment.BackupFilePrefix}{Guid.NewGuid():N}.bak");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            environment.DeleteGeneratedFile(foreignPath));

        Assert.Contains("拒绝删除", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServer2008R2ProfileRejectsStrictTds8Configuration()
    {
        Directory.CreateDirectory(_workerRoot);
        var values = CreateCompleteValues();
        values[TargetSqlAcceptanceEnvironment.ExpectedProfileVariable] = "SqlServer2008R2";
        var builder = new SqlConnectionStringBuilder(values[
            TargetSqlAcceptanceEnvironment.ConnectionVariable])
        {
            Encrypt = SqlConnectionEncryptOption.Strict,
        };
        values[TargetSqlAcceptanceEnvironment.ConnectionVariable] = builder.ConnectionString;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TargetSqlAcceptanceEnvironment.Load(values.GetValueOrDefault));

        Assert.Contains("Strict", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_workerRoot))
        {
            return;
        }

        if (!Path.GetFileName(_workerRoot).StartsWith(
                $"{TargetSqlAcceptanceEnvironment.ControlledRootPrefix}Guard_",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("拒绝清理不属于 P5.6 Guard 测试的目录。");
        }

        Directory.Delete(_workerRoot, recursive: true);
    }

    private Dictionary<string, string?> CreateCompleteValues()
    {
        return new Dictionary<string, string?>
        {
            [TargetSqlAcceptanceEnvironment.ConnectionVariable] =
                "Server=synthetic-target;User ID=synthetic-login;Password=synthetic-password;Encrypt=true;TrustServerCertificate=false",
            [TargetSqlAcceptanceEnvironment.DatabaseVariable] =
                $"{TargetSqlAcceptanceEnvironment.TargetDatabasePrefix}Synthetic",
            [TargetSqlAcceptanceEnvironment.SqlBackupRootVariable] = Path.Combine(
                Path.GetTempPath(),
                $"{TargetSqlAcceptanceEnvironment.ControlledRootPrefix}SqlSynthetic"),
            [TargetSqlAcceptanceEnvironment.WorkerBackupRootVariable] = _workerRoot,
            [TargetSqlAcceptanceEnvironment.ExpectedProfileVariable] = "SqlServer2019",
        };
    }
}
