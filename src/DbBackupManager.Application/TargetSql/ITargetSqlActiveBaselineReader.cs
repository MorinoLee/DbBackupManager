namespace DbBackupManager.Application.TargetSql;

public sealed record TargetSqlActiveBaselineRequest(string DatabaseName, int TimeoutSeconds);
public interface ITargetSqlActiveBaselineReader
{
    Task<BackupActiveMetadataEvidence> ReadAsync(TargetSqlConnectionInput connection,
        TargetSqlActiveBaselineRequest request, CancellationToken cancellationToken = default);
}
