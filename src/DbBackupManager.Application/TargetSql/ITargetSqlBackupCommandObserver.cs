namespace DbBackupManager.Application.TargetSql;

public interface ITargetSqlBackupCommandObserver
{
    ValueTask OnCommandStartedAsync(CancellationToken cancellationToken);
}
