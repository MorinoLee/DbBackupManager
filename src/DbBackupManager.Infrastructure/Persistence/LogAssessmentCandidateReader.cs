using DbBackupManager.Application.BackupLogs;
using DbBackupManager.Domain.BackupLogs;
using DbBackupManager.Domain.BackupSets;
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
        var candidates = rows.Select(x => ToCandidate(x, request.Scope)).ToArray();
        return new(true, candidates, []);
    }

    private static LogAssessmentCandidate ToCandidate(BackupSet set, LogLocationScope scope)
    {
        var metadata = set.Metadata;
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
            Database = metadata.DatabaseGuid.Value is { } databaseGuid && metadata.FamilyGuid.Value is { } familyGuid
                ? new(databaseGuid, familyGuid) : null,
            Branch = metadata.FirstRecoveryForkId.Value is { } firstFork
                && metadata.RecoveryForkId.Value is { } recoveryFork
                ? new(firstFork, recoveryFork) : null,
            CompletedAtUtc = set.Completion.CompletedAtUtc,
            Source = LogObservationSource.Platform,
            Status = set.HasMetadataConflict ? LogEvidenceStatus.SourceConflict
                : metadata.HasIncompleteMetadata.Value == true ? LogEvidenceStatus.MissingFields
                : LogEvidenceStatus.Complete,
        };
        var hasRequired = metadata.Type.Value is not BackupType.Log
            || metadata.RecoveryModel.State == BackupMetadataState.Known
                && metadata.BeginsLogChain.State == BackupMetadataState.Known
                && metadata.HasBulkLoggedData.State == BackupMetadataState.Known
                && metadata.IsForceOffline.State == BackupMetadataState.Known;
        return new(set.Id, actual, scope, LogLocationCopyState.Available, true,
            metadata.HasBackupChecksums.Value is { } checksum && metadata.IsCompressed.Value is { } compression
                ? new(checksum, compression) : null,
            set.Metadata.Type.Value == BackupType.Differential ? set.Assessment.Conclusion : null,
            set.BaseBackupSetId,
            LogAssessmentCanonicalizer.DeterministicId("candidate", set.Id),
            actual.Status, hasRequired);
    }
}
