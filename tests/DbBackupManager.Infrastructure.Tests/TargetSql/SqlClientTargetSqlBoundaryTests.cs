using DbBackupManager.Application.TargetSql;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.Data.SqlClient;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class SqlClientTargetSqlBoundaryTests
{
    [Fact]
    public void ConnectionUsesSqlCredentialWithoutEmbeddingIdentityOrPassword()
    {
        var input = CreateConnectionInput(trustServerCertificate: false);
        var password = "synthetic-password".ToCharArray();
        using var credential = TargetSqlCredentialLease.CreateAndClear(
            "synthetic-login",
            password);
        using var connection = SqlClientTargetSqlConnectionFactory.Create(input, credential);
        var builder = new SqlConnectionStringBuilder(connection.ConnectionString);

        Assert.All(password, character => Assert.Equal('\0', character));
        Assert.Equal("tcp:synthetic-target,1433", builder.DataSource);
        Assert.Equal("master", builder.InitialCatalog);
        Assert.False(builder.IntegratedSecurity);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.False(builder.TrustServerCertificate);
        Assert.False(builder.Pooling);
        Assert.False(builder.PersistSecurityInfo);
        Assert.False(builder.Enlist);
        Assert.Equal(17, builder.ConnectTimeout);
        Assert.Equal(0, builder.ConnectRetryCount);
        Assert.False(builder.MultipleActiveResultSets);
        Assert.Equal(SqlClientTargetSqlConnectionFactory.ApplicationName, builder.ApplicationName);
        Assert.True(string.IsNullOrEmpty(builder.UserID));
        Assert.True(string.IsNullOrEmpty(builder.Password));
        Assert.NotNull(connection.Credential);
        Assert.DoesNotContain("synthetic-login", connection.ConnectionString, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-password", connection.ConnectionString, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitCertificateTrustExceptionIsAppliedWithoutDisablingEncryption()
    {
        var input = CreateConnectionInput(trustServerCertificate: true);
        using var credential = TargetSqlCredentialLease.CreateAndClear(
            "synthetic-login",
            "synthetic-password".ToCharArray());
        using var connection = SqlClientTargetSqlConnectionFactory.Create(input, credential);
        var builder = new SqlConnectionStringBuilder(connection.ConnectionString);

        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.True(builder.TrustServerCertificate);
    }

    [Fact]
    public void CredentialLeaseRejectsReuseAfterDisposal()
    {
        var credential = TargetSqlCredentialLease.CreateAndClear(
            "synthetic-login",
            "synthetic-password".ToCharArray());

        credential.Dispose();

        Assert.Throws<ObjectDisposedException>(credential.CreateSqlCredential);
    }

    [Theory]
    [InlineData("10.0.6000.29", 3, false, false)]
    [InlineData("10.50.6000.34", 2, false, true)]
    [InlineData("10.50.6542.0", 2, true, true)]
    [InlineData("10.50.6542.0", 4, true, false)]
    [InlineData("15.0.4382.1", 3, true, true)]
    public void ServerVersionMappingSeparatesCompatibilityFromCompressionCapability(
        string productVersion,
        int engineEdition,
        bool isSupported,
        bool supportsCompression)
    {
        var info = TargetSqlServerInfoMapper.Map(
            productVersion,
            " SP ",
            " Synthetic Edition ",
            engineEdition);

        Assert.Equal(isSupported, TargetSqlServerInfoMapper.IsSupported(info));
        Assert.Equal(supportsCompression, info.SupportsBackupCompression);
        Assert.Equal("SP", info.ProductLevel);
        Assert.Equal("Synthetic Edition", info.Edition);
    }

    [Fact]
    public void InvalidServerMetadataBecomesStableInvalidResponse()
    {
        var exception = Assert.Throws<TargetSqlClientException>(() =>
            TargetSqlServerInfoMapper.Map("not-a-version", null, null, 3));

        Assert.Equal(TargetSqlFailureCode.InvalidResponse, exception.FailureCode);
        Assert.DoesNotContain("not-a-version", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ONLINE", TargetSqlDatabaseState.Online)]
    [InlineData("RECOVERY_PENDING", TargetSqlDatabaseState.RecoveryPending)]
    [InlineData("OFFLINE_SECONDARY", TargetSqlDatabaseState.OfflineSecondary)]
    [InlineData("future_state", TargetSqlDatabaseState.Unknown)]
    public void DatabaseStateMappingIsStable(string databaseState, TargetSqlDatabaseState expected)
    {
        Assert.Equal(expected, TargetSqlDatabaseInfoMapper.MapState(databaseState));
    }

    [Theory]
    [InlineData("FULL", TargetSqlRecoveryModel.Full)]
    [InlineData("BULK_LOGGED", TargetSqlRecoveryModel.BulkLogged)]
    [InlineData("SIMPLE", TargetSqlRecoveryModel.Simple)]
    [InlineData("future_model", TargetSqlRecoveryModel.Unknown)]
    public void RecoveryModelMappingIsStable(string recoveryModel, TargetSqlRecoveryModel expected)
    {
        Assert.Equal(expected, TargetSqlDatabaseInfoMapper.MapRecoveryModel(recoveryModel));
    }

    [Theory]
    [InlineData(-2, true, TargetSqlFailureCode.TimedOut)]
    [InlineData(18456, true, TargetSqlFailureCode.AuthenticationFailed)]
    [InlineData(229, false, TargetSqlFailureCode.AuthorizationDenied)]
    [InlineData(942, false, TargetSqlFailureCode.DatabaseUnavailable)]
    [InlineData(10054, false, TargetSqlFailureCode.ConnectionInterrupted)]
    [InlineData(50000, false, TargetSqlFailureCode.CommandRejected)]
    [InlineData(10054, true, TargetSqlFailureCode.ConnectionFailed)]
    public void SqlErrorNumbersMapWithoutDependingOnLocalizedMessages(
        int errorNumber,
        bool isConnectionOpen,
        TargetSqlFailureCode expected)
    {
        var operation = isConnectionOpen
            ? TargetSqlClientOperation.OpenConnection
            : TargetSqlClientOperation.ReadOnlyCommand;
        var actual = TargetSqlClientException.ClassifySqlErrorNumbers([errorNumber], operation);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(262, TargetSqlFailureCode.AuthorizationDenied)]
    [InlineData(911, TargetSqlFailureCode.DatabaseNotFound)]
    [InlineData(924, TargetSqlFailureCode.DatabaseUnavailable)]
    [InlineData(3201, TargetSqlFailureCode.BackupDestinationUnavailable)]
    [InlineData(3202, TargetSqlFailureCode.BackupDestinationUnavailable)]
    [InlineData(3013, TargetSqlFailureCode.CommandRejected)]
    public void BackupServerErrorsAreConfirmedByStableNumber(
        int errorNumber,
        TargetSqlFailureCode expected)
    {
        var actual = TargetSqlClientException.ClassifySqlErrorNumbers(
            [errorNumber],
            TargetSqlClientOperation.BackupCommand);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PostDispatchTimeoutAndCancellationAreIndeterminateWithoutRawDetails()
    {
        var timeout = TargetSqlClientException.FromBackupExecution(
            new TimeoutException("synthetic protected target detail"));
        var cancellation = TargetSqlClientException.FromBackupExecution(
            new OperationCanceledException("synthetic protected target detail"));

        Assert.Equal(TargetSqlFailureCode.TimedOut, timeout.FailureCode);
        Assert.Equal(TargetSqlClientFailureCertainty.Indeterminate, timeout.Certainty);
        Assert.Equal(TargetSqlFailureCode.Cancelled, cancellation.FailureCode);
        Assert.Equal(TargetSqlClientFailureCertainty.Indeterminate, cancellation.Certainty);
        Assert.DoesNotContain("protected target detail", timeout.Message, StringComparison.Ordinal);
        Assert.Null(timeout.InnerException);
    }

    [Fact]
    public void TransportSecurityFailureDoesNotRetainRawExceptionText()
    {
        var classified = TargetSqlClientException.From(
            new System.Security.Authentication.AuthenticationException(
                "synthetic protected endpoint detail"),
            TargetSqlClientOperation.OpenConnection);

        Assert.Equal(TargetSqlFailureCode.TransportSecurityFailed, classified.FailureCode);
        Assert.DoesNotContain("protected endpoint detail", classified.Message, StringComparison.Ordinal);
        Assert.Null(classified.InnerException);
    }

    private static TargetSqlConnectionInput CreateConnectionInput(bool trustServerCertificate)
    {
        return new TargetSqlConnectionInput(
            "tcp:synthetic-target,1433",
            Guid.NewGuid(),
            encryptConnection: true,
            trustServerCertificate,
            trustServerCertificate ? "隔离环境受控例外" : null,
            connectionTimeoutSeconds: 17);
    }
}
