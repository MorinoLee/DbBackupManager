namespace DbBackupManager.Application.TargetSql;

public interface ITargetSqlReadOnlyProbe
{
    Task<TargetSqlResult<TargetSqlServerInfo>> ProbeServerAsync(
        TargetSqlConnectionInput connection,
        CancellationToken cancellationToken = default);

    Task<TargetSqlResult<TargetSqlDatabaseCatalog>> DiscoverDatabasesAsync(
        TargetSqlConnectionInput connection,
        CancellationToken cancellationToken = default);

    Task<TargetSqlResult<TargetSqlBackupVerification>> VerifyBackupAsync(
        TargetSqlConnectionInput connection,
        TargetSqlBackupVerificationRequest request,
        CancellationToken cancellationToken = default);
}

public interface ITargetSqlBackupEvidenceProbe
{
    Task<TargetSqlResult<TargetSqlBackupIdentity>> InspectBackupIdentityAsync(
        TargetSqlConnectionInput connection,
        TargetSqlBackupIdentityRequest request,
        CancellationToken cancellationToken = default);
}
