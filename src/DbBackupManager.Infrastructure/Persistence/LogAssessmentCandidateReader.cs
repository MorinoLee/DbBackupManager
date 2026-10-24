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
        var sourceTimes = await db.BackupSetEvidence.AsNoTracking()
            .Where(x => x.BackupSetId != null && ids.Contains(x.BackupSetId.Value)
                && x.SourceObservedAtUtc != null)
            .GroupBy(x => x.BackupSetId!.Value)
            .Select(x => new { Id = x.Key, At = x.Max(y => y.SourceObservedAtUtc) })
            .ToDictionaryAsync(x => x.Id, x => x.At, cancellationToken);
        var candidates = rows.Select(x => ToCandidate(x, request.Scope,
            files.Where(file => file.BackupSetId == x.Id).ToArray(),
            snapshots, sourceTimes.GetValueOrDefault(x.Id))).ToArray();
        return new(true, candidates, []);
    }

    private static LogAssessmentCandidate ToCandidate(BackupSet set, LogLocationScope scope,
        IReadOnlyList<Domain.BackupTasks.BackupFile> files,
        IReadOnlyDictionary<Guid, Domain.BackupTasks.BackupTaskSnapshot> snapshots,
        DateTimeOffset? sourceObservedAt)
    {
        var actual = new LogBackupActualMetadata
        {
            Type = set.Metadata.Type.Value,
            IsCopyOnly = set.Metadata.IsCopyOnly.Value,
            RecoveryModel = set.Metadata.RecoveryModel.Value,
            BeginsLogChain = set.Metadata.BeginsLogChain.Value,
            HasBulkLoggedData = set.Metadata.HasBulkLoggedData.Value,
            IsForceOffline = set.Metadata.IsForceOffline.Value,
            UseChecksum = set.Metadata.HasBackupChecksums.Value,
            UseCompression = set.Metadata.IsCompressed.Value,
            IsDamaged = set.Metadata.IsDamaged.Value,
            IsSnapshot = set.Metadata.IsSnapshot.Value,
            IsIncomplete = set.Metadata.HasIncompleteMetadata.Value,
            FirstLsn = set.Metadata.FirstLsn.Value,
            LastLsn = set.Metadata.LastLsn.Value,
            DatabaseBackupLsn = set.Metadata.DatabaseBackupLsn.Value,
            ForkPointLsn = set.Metadata.ForkPointLsn.Value,
            Database = set.Metadata.DatabaseGuid.Value is { } databaseGuid
                && set.Metadata.FamilyGuid.Value is { } familyGuid ? new(databaseGuid, familyGuid) : null,
            Branch = set.Metadata.FirstRecoveryForkId.Value is { } firstFork
                && set.Metadata.RecoveryForkId.Value is { } recoveryFork ? new(firstFork, recoveryFork) : null,
            CompletedAtUtc = set.Completion.CompletedAtUtc,
            ObservedAtUtc = sourceObservedAt,
            Source = LogObservationSource.Platform,
            Status = set.HasMetadataConflict ? LogEvidenceStatus.SourceConflict
                : set.Metadata.HasIncompleteMetadata.Value == true ? LogEvidenceStatus.MissingFields
                : LogEvidenceStatus.Complete,
        };
        var matching = files.Where(file => file.Location == Domain.BackupTasks.BackupFileLocation.Local
            ? scope.Kind == Domain.BackupLogs.LogStorageLocation.Local
            : file.StorageTargetId == scope.StorageTargetId).ToArray();
        var available = matching.FirstOrDefault(file => file.Status == Domain.BackupTasks.BackupFileStatus.Available);
        var snapshot = available is null ? null : snapshots.GetValueOrDefault(available.TaskId);
        var storageMode = snapshot?.StorageMode;
        var copyState = available is null
            ? LogLocationCopyState.Missing
            : storageMode == BackupStorageMode.RemoteOnly && scope.Kind == LogStorageLocation.Local
                ? LogLocationCopyState.Unknown
                : LogLocationCopyState.Available;
        var protection = copyState == LogLocationCopyState.Available && snapshot is not null
            ? new LogProtectionExpectation(snapshot.UseChecksum, snapshot.UseCompression)
            : null;
        var hasRequired = set.Metadata.Type.Value is not BackupType.Log
            || set.Metadata.RecoveryModel.State == BackupMetadataState.Known
                && set.Metadata.BeginsLogChain.State == BackupMetadataState.Known
                && set.Metadata.HasBulkLoggedData.State == BackupMetadataState.Known
                && set.Metadata.IsForceOffline.State == BackupMetadataState.Known;
        return new(set.Id, actual, scope, copyState, true, protection,
            set.Metadata.Type.Value == BackupType.Differential ? set.Assessment.Conclusion : null,
            set.BaseBackupSetId,
            LogAssessmentCanonicalizer.DeterministicId("candidate", set.Id,
                available?.RowVersion ?? Array.Empty<byte>()),
            actual.Status, hasRequired);
    }
}
