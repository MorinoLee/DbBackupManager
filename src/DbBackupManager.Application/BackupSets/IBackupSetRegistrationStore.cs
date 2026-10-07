using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.BackupSets;

public sealed record BackupSetObservation(
    BackupSetEvidenceSource Source, BackupSetEvidenceKind Kind,
    BackupSetMetadata Metadata, BackupCompletionTimeDecision Completion, BackupSetAssessment Assessment);

public sealed record RegisterBackupSetCommand(
    LeaseHandle Lease, Guid BackupSetId, Guid DatabaseId, Guid MutationId, DateTimeOffset ObservedAtUtc,
    BackupSetMetadata Metadata, BackupCompletionTimeDecision Completion, BackupSetAssessment Assessment,
    Guid? BaseBackupSetId, bool SqlSuccessObserved, IReadOnlyList<BackupSetObservation> Observations);

public sealed record BackupSetRegistrationResult(
    Guid? BackupSetId, int ReconciliationNumber, bool HasConflict, BackupSetAssessment Assessment);

public interface IBackupSetRegistrationStore
{
    Task<BackupTaskStoreResult<BackupSetRegistrationResult>> RegisterAsync(
        RegisterBackupSetCommand command, CancellationToken cancellationToken = default);

    Task<BackupTaskStoreResult<BackupFile>> RegisterCopyAsync(
        LeaseHandle lease, BackupFile file, CancellationToken cancellationToken = default);
}

/// <summary>计划文件使用此入口，必须先登记备份集；旧 Worker 的文件登记入口仍保留空关联。</summary>
public sealed class BackupSetRegistrationService(IBackupSetRegistrationStore store)
{
    public Task<BackupTaskStoreResult<BackupFile>> RegisterCopyAsync(
        LeaseHandle lease, BackupFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.BackupSetId is null || file.BackupSetId == Guid.Empty)
            throw new ArgumentException("新备份副本必须关联已登记的备份集。", nameof(file));
        if (file.TaskId != lease.TaskId || file.AttemptId != lease.BackupAttemptId)
            throw new ArgumentException("副本与租约必须属于同一任务和 Attempt。", nameof(file));
        return store.RegisterCopyAsync(lease, file, cancellationToken);
    }
}
