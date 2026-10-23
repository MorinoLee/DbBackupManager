using DbBackupManager.Domain.BackupSets;

namespace DbBackupManager.Application.TargetSql;

public sealed class TargetSqlLogReadinessRequest
{
    public TargetSqlLogReadinessRequest(
        Guid platformDatabaseId,
        string databaseName,
        BackupDatabaseIdentity expectedDatabase,
        int timeoutSeconds)
    {
        if (platformDatabaseId == Guid.Empty
            || string.IsNullOrWhiteSpace(databaseName)
            || databaseName.Length > 128
            || databaseName.Any(char.IsControl)
            || timeoutSeconds is < 1 or > 3600)
        {
            throw new ArgumentException("LOG 就绪请求必须包含平台数据库、定位数据库名和有限超时。");
        }

        ArgumentNullException.ThrowIfNull(expectedDatabase);
        if (expectedDatabase.DatabaseGuid is not { } databaseGuid
            || databaseGuid == Guid.Empty
            || expectedDatabase.FamilyGuid is not { } familyGuid
            || familyGuid == Guid.Empty)
        {
            throw new ArgumentException("LOG 就绪请求必须绑定已知的期望源身份。", nameof(expectedDatabase));
        }

        PlatformDatabaseId = platformDatabaseId;
        DatabaseName = databaseName;
        ExpectedDatabase = expectedDatabase;
        TimeoutSeconds = timeoutSeconds;
    }

    public Guid PlatformDatabaseId { get; }

    public string DatabaseName { get; }

    public BackupDatabaseIdentity ExpectedDatabase { get; }

    public int TimeoutSeconds { get; }
}

public enum TargetSqlLogLsnState
{
    Unknown = 1,
    ObservedNull = 2,
    Known = 3,
}

public sealed record TargetSqlDatabaseReadinessObservation(
    int? SqlDatabaseId,
    string? Name,
    TargetSqlDatabaseState? State,
    TargetSqlRecoveryModel? RecoveryModel,
    bool QueryComplete,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<BackupMetadataReadIssue> Issues,
    TargetSqlFailureCode? FailureCode = null,
    TargetSqlFailurePhase? FailurePhase = null)
{
    public bool IsOnline => State == TargetSqlDatabaseState.Online;
}

public sealed record TargetSqlRecoveryStatusObservation(
    bool WasAttempted,
    BackupDatabaseIdentity? Database,
    Guid? CurrentRecoveryForkId,
    Guid? DiagnosticFirstRecoveryForkId,
    TargetSqlLogLsnState LastLogBackupLsnState,
    BackupLsn? LastLogBackupLsn,
    bool QueryComplete,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<BackupMetadataReadIssue> Issues,
    TargetSqlFailureCode? FailureCode = null,
    TargetSqlFailurePhase? FailurePhase = null);

/// <summary>
/// 固定目标的只读事实；不推导 Capability/Resource/世代，也不授予 LOG 调用资格。
/// </summary>
public sealed record TargetSqlLogReadinessEvidence(
    Guid PlatformDatabaseId,
    string DatabaseName,
    BackupDatabaseIdentity ExpectedDatabase,
    TargetSqlServerInfo? Server,
    TargetSqlDatabaseReadinessObservation Database,
    TargetSqlRecoveryStatusObservation Recovery,
    TargetSqlDatabaseReadinessObservation? Recheck,
    bool? ExpectedIdentityMatches,
    bool? StableAcrossRead);

public interface ITargetSqlLogReadinessReader
{
    Task<TargetSqlLogReadinessEvidence> ReadAsync(
        TargetSqlConnectionInput connection,
        TargetSqlLogReadinessRequest request,
        CancellationToken cancellationToken = default);
}
