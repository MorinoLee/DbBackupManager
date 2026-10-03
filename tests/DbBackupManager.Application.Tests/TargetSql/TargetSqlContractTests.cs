using DbBackupManager.Application.TargetSql;

namespace DbBackupManager.Application.Tests.TargetSql;

public sealed class TargetSqlContractTests
{
    [Fact]
    public void ReadOnlyProbeAndBackupExecutorHaveSeparateCapabilities()
    {
        var probeMethods = typeof(ITargetSqlReadOnlyProbe)
            .GetMethods()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var executorMethods = typeof(ITargetSqlBackupExecutor)
            .GetMethods()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["DiscoverDatabasesAsync", "ProbeServerAsync", "VerifyBackupAsync"],
            probeMethods);
        Assert.Equal(["ExecuteFullBackupAsync"], executorMethods);
    }

    [Fact]
    public void TargetSqlContractsDoNotExposeSecrets()
    {
        var contractTypes = typeof(TargetSqlConnectionInput)
            .Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(TargetSqlConnectionInput).Namespace)
            .ToArray();
        var publicPropertyNames = contractTypes
            .SelectMany(type => type.GetProperties().Select(property => property.Name))
            .ToArray();
        string[] forbiddenPropertyFragments =
        [
            "Password",
            "Secret",
            "ConnectionString",
            "UserName",
            "Protected",
        ];

        Assert.DoesNotContain(
            publicPropertyNames,
            propertyName => forbiddenPropertyFragments.Any(
                fragment => propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ConnectionInputRequiresEncryptionAndExplicitTrustExceptionReason()
    {
        var credentialId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => new TargetSqlConnectionInput(
            "synthetic-target",
            credentialId,
            encryptConnection: false,
            trustServerCertificate: false,
            certificateTrustReason: null,
            connectionTimeoutSeconds: 30));
        Assert.Throws<ArgumentException>(() => new TargetSqlConnectionInput(
            "synthetic-target",
            credentialId,
            encryptConnection: true,
            trustServerCertificate: true,
            certificateTrustReason: null,
            connectionTimeoutSeconds: 30));
        Assert.Throws<ArgumentException>(() => new TargetSqlConnectionInput(
            "synthetic-target",
            credentialId,
            encryptConnection: true,
            trustServerCertificate: false,
            certificateTrustReason: "不应存在的例外原因",
            connectionTimeoutSeconds: 30));
        Assert.Throws<ArgumentException>(() => new TargetSqlConnectionInput(
            "synthetic-target;Application Name=unexpected",
            credentialId,
            encryptConnection: true,
            trustServerCertificate: false,
            certificateTrustReason: null,
            connectionTimeoutSeconds: 30));
        Assert.Throws<ArgumentException>(() => new TargetSqlConnectionInput(
            "synthetic-target",
            credentialId,
            encryptConnection: true,
            trustServerCertificate: true,
            certificateTrustReason: "不允许换行\n原因",
            connectionTimeoutSeconds: 30));

        var strict = new TargetSqlConnectionInput(
            " synthetic-target ",
            credentialId,
            encryptConnection: true,
            trustServerCertificate: false,
            certificateTrustReason: null,
            connectionTimeoutSeconds: 30);
        var exception = new TargetSqlConnectionInput(
            "synthetic-target",
            credentialId,
            encryptConnection: true,
            trustServerCertificate: true,
            certificateTrustReason: " 隔离环境的受控证书信任 ",
            connectionTimeoutSeconds: 30);

        Assert.Equal("synthetic-target", strict.ConnectionAddress);
        Assert.True(strict.EncryptConnection);
        Assert.Equal("隔离环境的受控证书信任", exception.CertificateTrustReason);
    }

    [Fact]
    public void BackupAndVerificationInputsRejectInvalidPathsOrTimeouts()
    {
        Assert.Throws<ArgumentException>(() => new TargetSqlFullBackupRequest(
            "SyntheticDatabase",
            "synthetic-file.tmp",
            useCopyOnly: true,
            useChecksum: true,
            useCompression: false,
            commandTimeoutSeconds: 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TargetSqlBackupVerificationRequest(
            "synthetic-file.bak",
            useChecksum: true,
            commandTimeoutSeconds: 0));
        Assert.Throws<ArgumentException>(() => new TargetSqlFullBackupRequest(
            "Synthetic\nDatabase",
            "synthetic-file.bak",
            useCopyOnly: true,
            useChecksum: true,
            useCompression: false,
            commandTimeoutSeconds: 60));
        Assert.Throws<ArgumentException>(() => new TargetSqlBackupVerificationRequest(
            "synthetic\nfile.bak",
            useChecksum: true,
            commandTimeoutSeconds: 60));

        var backup = new TargetSqlFullBackupRequest(
            " Synthetic]Database ",
            " synthetic-file.BAK ",
            useCopyOnly: true,
            useChecksum: true,
            useCompression: false,
            commandTimeoutSeconds: 60);

        Assert.Equal("Synthetic]Database", backup.DatabaseName);
        Assert.Equal("synthetic-file.BAK", backup.LocalSqlFilePath);
    }

    [Fact]
    public void ResultsSeparateConfirmedFailureFromIndeterminateOutcome()
    {
        var succeeded = TargetSqlResult.Succeeded(
            new TargetSqlBackupVerification(UsedChecksum: true));
        var confirmed = TargetSqlResult.ConfirmedFailure<TargetSqlBackupVerification>(
            TargetSqlFailureCode.AuthorizationDenied,
            TargetSqlFailurePhase.BackupVerification);
        var indeterminate = TargetSqlResult.Indeterminate<TargetSqlFullBackupCompletion>(
            TargetSqlFailureCode.ConnectionInterrupted,
            TargetSqlFailurePhase.BackupExecution);

        Assert.True(succeeded.IsSucceeded);
        Assert.True(succeeded.Value!.UsedChecksum);
        Assert.Null(succeeded.Failure);
        Assert.Equal(TargetSqlOutcome.ConfirmedFailed, confirmed.Outcome);
        Assert.Equal(TargetSqlFailureCode.AuthorizationDenied, confirmed.Failure!.Code);
        Assert.Null(confirmed.Value);
        Assert.Equal(TargetSqlOutcome.Indeterminate, indeterminate.Outcome);
        Assert.Equal(TargetSqlFailurePhase.BackupExecution, indeterminate.Failure!.Phase);
    }

    [Fact]
    public void DatabaseCatalogDoesNotExposeItsMutableSource()
    {
        var database = new TargetSqlDatabaseInfo(
            "SyntheticDatabase",
            TargetSqlDatabaseState.Online,
            TargetSqlRecoveryModel.Full,
            IsSystemDatabase: false,
            IsSnapshot: false,
            CanBeManaged: true);
        var source = new List<TargetSqlDatabaseInfo> { database };
        var catalog = new TargetSqlDatabaseCatalog(source);

        source.Clear();
        var firstRead = Assert.IsAssignableFrom<IList<TargetSqlDatabaseInfo>>(catalog.Databases);
        var secondRead = catalog.Databases;

        Assert.Throws<NotSupportedException>(firstRead.Clear);
        Assert.NotSame(firstRead, secondRead);
        Assert.Equal([database], catalog.Databases);
    }
}
