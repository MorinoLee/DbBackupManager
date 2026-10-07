namespace DbBackupManager.Application.TargetSql;

public interface ITargetSqlPlanBackupExecutor
{
    Task<TargetSqlResult<TargetSqlBackupCompletion>> ExecuteBackupAsync(
        TargetSqlConnectionInput connection,
        TargetSqlBackupRequest request,
        CancellationToken cancellationToken = default);
}
