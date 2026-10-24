using DbBackupManager.Application.BackupLogs;
using DbBackupManager.Domain.BackupLogs;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

/// <summary>短 Context 读取已持久候选；不在目标 I/O 期间持有上下文。</summary>
internal sealed class LogAssessmentCandidateReader(IDbContextFactory<PlatformDbContext> factory)
    : ILogAssessmentCandidateReader
{
    public async Task<LogCandidateSnapshot> ReadAsync(LogAssessmentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Scope.Kind == LogStorageLocation.Remote)
            return new(false, [], ["log.candidate_reader_remote_not_supported"]);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.BackupSets.AsNoTracking()
            .Where(x => x.DatabaseId == request.DatabaseId)
            .OrderBy(x => x.Id)
            .ToArrayAsync(cancellationToken);
        var ids = rows.Select(x => x.Id).ToArray();
        var files = await db.BackupFiles.AsNoTracking()
            .Where(x => x.BackupSetId != null && ids.Contains(x.BackupSetId.Value))
            .ToArrayAsync(cancellationToken);
        var taskIds = files.Select(x => x.TaskId).Distinct().ToArray();
        var snapshots = await db.BackupTaskSnapshots.AsNoTracking()
            .Where(x => taskIds.Contains(x.TaskId))
            .ToDictionaryAsync(x => x.TaskId, cancellationToken);
        var evidenceRows = await db.BackupSetEvidence.AsNoTracking()
            .Where(x => x.BackupSetId != null && ids.Contains(x.BackupSetId.Value))
            .ToArrayAsync(cancellationToken);
        var candidates = new List<LogAssessmentCandidate>();
        foreach (var set in rows)
        {
            var setFiles = files.Where(file => file.BackupSetId == set.Id).ToArray();
            var observed = evidenceRows.Where(x => x.BackupSetId == set.Id)
                .GroupBy(x => x.Source)
                .Select(group => group.OrderByDescending(x => x.ObservedAtUtc)
                    .ThenByDescending(x => x.EntryNumber).First())
                .ToArray();
            if (observed.Length == 0)
            {
                candidates.Add(ToCandidate(set.Id, set.Metadata, set.Completion, request.Scope,
                    setFiles, snapshots, LogObservationSource.Platform, null, null,
                    set.BaseBackupSetId, set.Assessment));
                continue;
            }
            foreach (var evidence in observed)
                candidates.Add(ToCandidate(set.Id, evidence.Metadata, evidence.Completion, request.Scope,
                    setFiles, snapshots, MapSource(evidence.Source), evidence.SourceObservedAtUtc, evidence.Id,
                    set.BaseBackupSetId, set.Assessment));
        }
        return new(true, candidates, []);
    }

    private static LogObservationSource MapSource(Domain.BackupSets.BackupSetEvidenceSource source) => source switch
    {
        Domain.BackupSets.BackupSetEvidenceSource.BackupHeader => LogObservationSource.BackupHeader,
        Domain.BackupSets.BackupSetEvidenceSource.Msdb => LogObservationSource.Msdb,
        _ => LogObservationSource.Platform,
    };

    private static LogAssessmentCandidate ToCandidate(Guid backupSetId, BackupSetMetadata metadata,
        BackupCompletionTimeDecision completion, LogLocationScope scope,
        IReadOnlyList<Domain.BackupTasks.BackupFile> files,
        IReadOnlyDictionary<Guid, Domain.BackupTasks.BackupTaskSnapshot> snapshots,
        LogObservationSource source, DateTimeOffset? sourceObservedAt, Guid? evidenceId,
        Guid? baseBackupSetId, BackupSetAssessment assessment)
    {
        var actual = new LogBackupActualMetadata
        {
            Type = metadata.Type.Value,
            IsCopyOnly = metadata.IsCopyOnly.Value,
            RecoveryModel = metadata.RecoveryModel.Value,
            BeginsLogChain = metadata.BeginsLogChain.Value,
            HasBulkLoggedData = metadata.HasBulkLoggedData.Value,
            IsForceOffline = metadata.IsForceOffline.Value,
            UseChecksum = metadata.HasBackupChecksums.Value,
            UseCompression = metadata.IsCompressed.Value,
            IsDamaged = metadata.IsDamaged.Value,
            IsSnapshot = metadata.IsSnapshot.Value,
            IsIncomplete = metadata.HasIncompleteMetadata.Value,
            FirstLsn = metadata.FirstLsn.Value,
            LastLsn = metadata.LastLsn.Value,
            DatabaseBackupLsn = metadata.DatabaseBackupLsn.Value,
            ForkPointLsn = metadata.ForkPointLsn.Value,
            Database = metadata.DatabaseGuid.Value is { } databaseGuid
                && metadata.FamilyGuid.Value is { } familyGuid ? new(databaseGuid, familyGuid) : null,
            Branch = metadata.FirstRecoveryForkId.Value is { } firstFork
                && metadata.RecoveryForkId.Value is { } recoveryFork ? new(firstFork, recoveryFork) : null,
            CompletedAtUtc = completion.CompletedAtUtc,
            ObservedAtUtc = sourceObservedAt,
            Source = source,
            Status = metadata.HasIncompleteMetadata.Value == true ? LogEvidenceStatus.MissingFields
                : LogEvidenceStatus.Complete,
        };
        var matching = files.Where(file => file.Location == Domain.BackupTasks.BackupFileLocation.Local
            ? scope.Kind == Domain.BackupLogs.LogStorageLocation.Local
            : file.StorageTargetId == scope.StorageTargetId).ToArray();
        var available = matching.FirstOrDefault(file => file.Status == Domain.BackupTasks.BackupFileStatus.Available);
        var snapshot = available is null ? null : snapshots.GetValueOrDefault(available.TaskId);
        var storageMode = snapshot?.StorageMode;
        var copyState = storageMode == BackupStorageMode.RemoteOnly
            && scope.Kind == LogStorageLocation.Local
                ? LogLocationCopyState.Unknown
            : matching.Length == 0
                ? LogLocationCopyState.Missing
            : available is not null
                ? LogLocationCopyState.Available
            : matching.Any(file => file.Status is Domain.BackupTasks.BackupFileStatus.DeletePending
                or Domain.BackupTasks.BackupFileStatus.DeleteFailed)
                ? LogLocationCopyState.Unknown
                : LogLocationCopyState.Missing;
        var protection = copyState == LogLocationCopyState.Available && snapshot is not null
            ? new LogProtectionExpectation(snapshot.UseChecksum, snapshot.UseCompression)
            : null;
        var hasRequired = metadata.Type.Value is not BackupType.Log
            || metadata.RecoveryModel.State == BackupMetadataState.Known
                && metadata.BeginsLogChain.State == BackupMetadataState.Known
                && metadata.HasBulkLoggedData.State == BackupMetadataState.Known
                && metadata.IsForceOffline.State == BackupMetadataState.Known;
        var sourceRevision = LogAssessmentCanonicalizer.CandidateSourceRevision(backupSetId,
            available?.RowVersion ?? Array.Empty<byte>(), actual, scope, copyState, protection, sourceObservedAt,
            evidenceId);
        return new(backupSetId, actual, scope, copyState, true, protection,
            metadata.Type.Value == BackupType.Differential ? assessment.Conclusion : null,
            baseBackupSetId,
            sourceRevision,
            actual.Status, hasRequired);
    }
}
