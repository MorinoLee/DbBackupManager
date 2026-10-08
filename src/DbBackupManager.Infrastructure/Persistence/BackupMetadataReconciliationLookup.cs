using DbBackupManager.Application.BackupSets;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupMetadataReconciliationLookup(IDbContextFactory<PlatformDbContext> factory) : IBackupMetadataReconciliationLookup
{
    public async Task<BackupMetadataAttemptContext?> ReadAttemptAsync(Guid taskId, Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var snapshot = await context.BackupTaskSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.TaskId == taskId, cancellationToken);
        var attempt = await context.BackupAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.TaskId == taskId && x.Id == attemptId, cancellationToken);
        return snapshot is null || attempt is null ? null : new(snapshot.DatabaseId, snapshot.DatabaseName, attempt.LocalSqlFilePath,
            new(snapshot.ConnectionAddress, snapshot.SqlCredentialReferenceId, snapshot.EncryptConnection,
                snapshot.TrustServerCertificate, snapshot.CertificateTrustReason, snapshot.ConnectionTimeoutSeconds,
                snapshot.AllowLegacyTls, snapshot.LegacyTlsReason));
    }

    public async Task<BackupMetadataRegistrationReplay?> ReadReplayAsync(LeaseHandle lease, Guid databaseId, Guid backupSetId,
        Guid mutationId, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await context.BackupSetEvidence.AsNoTracking().Where(x => x.TaskId == lease.TaskId
            && x.AttemptId == lease.BackupAttemptId && x.MutationId == mutationId).OrderBy(x => x.EntryNumber).ToArrayAsync(cancellationToken);
        if (rows.Length == 0) return null;
        if (rows.Length < 2 || rows[0].Source != BackupSetEvidenceSource.Registration
            || rows[1].Source != BackupSetEvidenceSource.Comparison
            || rows.Where((row, index) => row.EntryNumber != index).Any())
            throw new InvalidOperationException("已提交核对证据不完整，不能重放。");
        var request = rows[0];
        var observations = rows.Skip(2).Select(x => new BackupSetObservation(x.Source, x.Kind, x.Metadata, x.Completion, x.Assessment)).ToArray();
        var activeAssessments = observations.Where(x => x.Kind == BackupSetEvidenceKind.ActiveBaseline)
            .Select(x => x.Assessment).Distinct().ToArray();
        var active = activeAssessments.Length == 1 ? activeAssessments[0]
            : new(BackupMetadataState.Known, activeAssessments.Length == 0 ? DifferentialBaselineConclusion.Unknown : DifferentialBaselineConclusion.Mismatch,
                activeAssessments.Length == 0 ? DifferentialBaselineReason.MissingFields : DifferentialBaselineReason.SourceConflict);
        return new(new(lease, request.BackupSetId ?? backupSetId, databaseId, mutationId, request.ObservedAtUtc,
            request.Metadata, request.Completion, request.Assessment, request.RequestedBaseBackupSetId,
            request.SqlSuccessObserved, Array.AsReadOnly(observations)), rows[1].Assessment, active);
    }

    public async Task<IReadOnlyList<ManagedFullBackupCandidate>> FindAsync(Guid databaseId, Guid backupSetGuid,
        CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var sets = await context.BackupSets.AsNoTracking().Where(x => x.DatabaseId == databaseId
            && x.Metadata.BackupSetGuid.Value == backupSetGuid && x.Metadata.Type.Value == BackupType.Full)
            .ToListAsync(cancellationToken);
        return sets.Select(x => new ManagedFullBackupCandidate(x.Id, BackupMetadataReconciliationService.Full(x.Metadata,
            x.HasMetadataConflict ? BaselineEvidenceStatus.SourceConflict : BaselineEvidenceStatus.Complete))).ToArray();
    }
}
