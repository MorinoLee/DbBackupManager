using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;

namespace DbBackupManager.Application.BackupSets;

public sealed record BackupMetadataAttemptContext(Guid DatabaseId, string DatabaseName, string SqlFilePath,
    TargetSqlConnectionInput Connection);
public sealed record BackupMetadataRegistrationReplay(RegisterBackupSetCommand Command,
    BackupSetAssessment HistoricalDependency, BackupSetAssessment ActiveBaseline);

public interface IBackupMetadataReconciliationLookup
{
    Task<BackupMetadataAttemptContext?> ReadAttemptAsync(Guid taskId, Guid attemptId,
        CancellationToken cancellationToken = default);
    Task<BackupMetadataRegistrationReplay?> ReadReplayAsync(LeaseHandle lease, Guid databaseId, Guid backupSetId,
        Guid mutationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ManagedFullBackupCandidate>> FindAsync(Guid databaseId, Guid backupSetGuid,
        CancellationToken cancellationToken = default);
}
