using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.BackupTasks;

/// <summary>调度、执行及阶段提交共用的完整备份事实；活动结论不替代历史依赖。</summary>
public static class BackupPlanExecutionFactRules
{
    public static bool MetadataPassed(BackupSet? set, IReadOnlyList<BackupSetEvidence> evidence, BackupType type = BackupType.Full)
    {
        if (set is null || set.HasMetadataConflict || !set.SqlSuccessObserved || !CompleteBackup(set.Metadata, type)
            || !Passed(set.Assessment) || evidence.Count == 0) return false;
        var latest = evidence.Max(x => x.ReconciliationNumber);
        var round = evidence.Where(x => x.ReconciliationNumber == latest).ToArray();
        // FULL 的差异依赖是不适用；不能将活动基线 Verified 当成文件核对通过。
        foreach (var source in new[] { BackupSetEvidenceSource.Comparison, BackupSetEvidenceSource.BackupHeader, BackupSetEvidenceSource.Msdb })
        {
            var rows = round.Where(x => x.Source == source && x.Kind == BackupSetEvidenceKind.Backup).ToArray();
            if (rows.Length != 1 || rows[0].BackupSetId != set.Id || !rows[0].SqlSuccessObserved
                || rows[0].TaskId != set.TaskId || rows[0].AttemptId != set.AttemptId
                || !Passed(rows[0].Assessment) || !CompleteBackup(rows[0].Metadata, type)) return false;
            _ = set.Metadata.Merge(rows[0].Metadata, out var conflict);
            if (conflict) return false;
        }
        return true;

        bool Passed(BackupSetAssessment assessment) => type == BackupType.Full
            ? assessment == BackupSetAssessment.NotApplicable
            : type == BackupType.Differential && assessment.State == BackupMetadataState.Known
                && assessment.Conclusion == DifferentialBaselineConclusion.Verified && set?.BaseBackupSetId is not null;
    }

    public static bool CompleteBackup(BackupSetMetadata m, BackupType type) =>
        m.Type.Value == type && m.IsCopyOnly.Value == false
        && m.BackupSetGuid.Value is { } guid && guid != Guid.Empty
        && m.DatabaseGuid.Value is { } database && database != Guid.Empty
        && m.FamilyGuid.Value is { } family && family != Guid.Empty
        && m.FirstLsn.Value is not null && m.LastLsn.Value is not null && m.CheckpointLsn.Value is not null
        && m.DatabaseBackupLsn.Value is not null
        && (type == BackupType.Full
            ? m.DifferentialBaseGuid.State == BackupMetadataState.NotApplicable && m.DifferentialBaseLsn.State == BackupMetadataState.NotApplicable
            : type == BackupType.Differential && m.DifferentialBaseGuid.Value is { } baseGuid && baseGuid != Guid.Empty
                && m.DifferentialBaseLsn.Value is not null && m.DatabaseBackupLsn.Value == m.DifferentialBaseLsn.Value)
        && m.FirstRecoveryForkId.Value is { } fork && fork != Guid.Empty && m.RecoveryForkId.Value == fork
        && m.ForkPointLsn.State != BackupMetadataState.Unknown
        && m.HasBackupChecksums.Value is not null && m.IsCompressed.Value is not null
        && m.IsDamaged.Value == false && m.IsSnapshot.Value == false && m.HasIncompleteMetadata.Value == false
        && m.SqlStartedLocal.Value is not null && m.SqlFinishedLocal.Value is not null;
}
