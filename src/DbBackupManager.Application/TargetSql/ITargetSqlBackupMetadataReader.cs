using DbBackupManager.Domain.BackupSets;

namespace DbBackupManager.Application.TargetSql;

public sealed record TargetSqlBackupMetadataRequest
{
    public TargetSqlBackupMetadataRequest(Guid attemptId, string databaseName, string attemptFilePath, int timeoutSeconds)
    {
        if (attemptId == Guid.Empty || string.IsNullOrWhiteSpace(databaseName) || databaseName.Length > 128
            || databaseName.Any(char.IsControl) || string.IsNullOrWhiteSpace(attemptFilePath)
            || attemptFilePath.Length > 4000 || attemptFilePath.Any(char.IsControl) || timeoutSeconds is < 1 or > 3600)
            throw new ArgumentException("元数据请求必须包含 Attempt、数据库、专属文件和有效超时。");
        AttemptId = attemptId;
        DatabaseName = databaseName;
        AttemptFilePath = attemptFilePath;
        TimeoutSeconds = timeoutSeconds;
    }
    public Guid AttemptId { get; }
    public string DatabaseName { get; }
    public string AttemptFilePath { get; }
    public int TimeoutSeconds { get; }
}

public enum BackupMetadataReadProblem { MissingColumn, NullValue, InvalidValue, NotFound, NotUnique, ReadFailed }
public sealed record BackupMetadataReadIssue(string Field, BackupMetadataReadProblem Problem);

public sealed record BackupMetadataSourceEvidence(
    BackupSetEvidenceSource Source, BackupSetMetadata Metadata, BaselineEvidenceStatus Status,
    IReadOnlyList<BackupMetadataReadIssue> Issues, TargetSqlFailureCode? FailureCode = null,
    Guid? RequestedBackupSetGuid = null, IReadOnlyList<BackupSetMetadata>? AmbiguousRecords = null);

public sealed record BackupActiveMetadataEvidence(
    ActiveDifferentialBaselineEvidence Baseline, Guid? DiagnosticFirstRecoveryForkId,
    IReadOnlyList<BackupMetadataReadIssue> Issues, TargetSqlFailureCode? FailureCode = null)
{
    public BackupSetEvidenceSource Source { get; } = BackupSetEvidenceSource.Database;
}

/// <summary>来源独立失败；当前基线与历史依赖互不反填。没有备份头自由文本。</summary>
public sealed record TargetSqlBackupMetadataEvidence(
    BackupMetadataSourceEvidence Header, BackupMetadataSourceEvidence History,
    BackupActiveMetadataEvidence Active, IReadOnlyList<BackupMetadataSourceEvidence> ReferencedFulls,
    TimeSpan? CurrentServerOffset, TimeZoneInfo? ServerTimeZone);

public interface ITargetSqlBackupMetadataReader
{
    Task<TargetSqlBackupMetadataEvidence> ReadAsync(TargetSqlConnectionInput connection,
        TargetSqlBackupMetadataRequest request, CancellationToken cancellationToken = default);
}
