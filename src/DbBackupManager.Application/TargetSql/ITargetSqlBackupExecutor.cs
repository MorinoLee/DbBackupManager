namespace DbBackupManager.Application.TargetSql;

public interface ITargetSqlBackupExecutor
{
    Task<TargetSqlResult<TargetSqlFullBackupCompletion>> ExecuteFullBackupAsync(
        TargetSqlConnectionInput connection,
        TargetSqlFullBackupRequest request,
        CancellationToken cancellationToken = default);
}
