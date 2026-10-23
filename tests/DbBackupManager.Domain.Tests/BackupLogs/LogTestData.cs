using DbBackupManager.Domain.BackupLogs;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupLogs;

internal static class LogTestData
{
    public static readonly Guid DatabaseId = Id(1);
    public static readonly BackupDatabaseIdentity Database = new(Id(2), Id(3));
    public static readonly BackupRecoveryBranch Branch = new(Id(4), Id(4));
    public static readonly LogChainGeneration Generation = new(Id(5), LogGenerationEvidenceKind.BeginsLogChain);
    public static readonly Guid FullId = Id(10);

    public static Guid Id(int value) => new($"00000000-0000-0000-0000-{value:D12}");

    public static LogBackupActualMetadata Metadata(
        decimal? first = 200,
        decimal? last = 300,
        BackupType? type = BackupType.Log,
        bool? copyOnly = false,
        DatabaseRecoveryModel? recoveryModel = DatabaseRecoveryModel.Full,
        bool? beginsLogChain = false,
        bool? bulk = false,
        bool? forceOffline = false,
        bool? checksum = true,
        bool? compression = false,
        bool? damaged = false,
        bool? snapshot = false,
        bool? incomplete = false,
        BackupDatabaseIdentity? database = null,
        BackupRecoveryBranch? branch = null,
        LogChainGeneration? generation = null,
        LogEvidenceStatus status = LogEvidenceStatus.Complete,
        LogObservationSource source = LogObservationSource.BackupHeader,
        DateTimeOffset? observedAtUtc = null,
        decimal? databaseBackupLsn = 150,
        decimal? forkPointLsn = 220,
        DateTimeOffset? completedAtUtc = null)
    {
        return new LogBackupActualMetadata
        {
            Type = type,
            IsCopyOnly = copyOnly,
            RecoveryModel = recoveryModel,
            BeginsLogChain = beginsLogChain,
            HasBulkLoggedData = bulk,
            IsForceOffline = forceOffline,
            UseChecksum = checksum,
            UseCompression = compression,
            IsDamaged = damaged,
            IsSnapshot = snapshot,
            IsIncomplete = incomplete,
            FirstLsn = first is { } firstValue ? new BackupLsn(firstValue) : null,
            LastLsn = last is { } lastValue ? new BackupLsn(lastValue) : null,
            DatabaseBackupLsn = databaseBackupLsn is { } databaseBackupValue
                ? new BackupLsn(databaseBackupValue)
                : null,
            ForkPointLsn = forkPointLsn is { } forkPointValue ? new BackupLsn(forkPointValue) : null,
            Database = database ?? Database,
            Branch = branch ?? Branch,
            Generation = generation ?? Generation,
            CompletedAtUtc = completedAtUtc,
            ObservedAtUtc = observedAtUtc,
            Source = source,
            Status = status,
        };
    }

    public static LogCoverageCandidate LogCandidate(
        Guid id,
        decimal? first = 200,
        decimal? last = 300,
        LogBackupActualMetadata? actual = null,
        LogStorageLocation location = LogStorageLocation.Local,
        LogLocationCopyState copy = LogLocationCopyState.Available,
        bool managed = true)
    {
        return new LogCoverageCandidate
        {
            BackupSetId = id,
            Actual = actual ?? Metadata(first, last),
            Location = location,
            LocationCopy = copy,
            IsManaged = managed,
        };
    }

    public static LogDataStartCandidate FullCandidate(
        Guid? id = null,
        decimal last = 200,
        decimal first = 100,
        decimal databaseBackupLsn = 150,
        LogStorageLocation location = LogStorageLocation.Local,
        LogLocationCopyState copy = LogLocationCopyState.Available,
        bool managed = true,
        bool copyOnly = false,
        LogEvidenceStatus status = LogEvidenceStatus.Complete,
        LogBackupActualMetadata? actual = null)
    {
        return new LogDataStartCandidate
        {
            BackupSetId = id ?? FullId,
            Actual = actual ?? Metadata(
                first,
                last,
                type: BackupType.Full,
                copyOnly: copyOnly,
                databaseBackupLsn: databaseBackupLsn),
            Location = location,
            LocationCopy = copy,
            IsManaged = managed,
        };
    }

    public static LogDataStartCandidate DifferentialCandidate(
        Guid id,
        Guid matchesFullId,
        decimal last,
        DifferentialBaselineConclusion dependency = DifferentialBaselineConclusion.Verified,
        decimal first = 201,
        LogStorageLocation location = LogStorageLocation.Local,
        bool managed = true)
    {
        return new LogDataStartCandidate
        {
            BackupSetId = id,
            Actual = Metadata(
                first,
                last,
                type: BackupType.Differential,
                copyOnly: false),
            Location = location,
            IsManaged = managed,
            DifferentialDependency = dependency,
            MatchesFullBackupSetId = matchesFullId,
        };
    }

    public static LogDataStartDecision AvailableStart(decimal restorePoint = 200)
    {
        return LogDataStartRules.Select(new LogDataStartRequest(
            LogStorageLocation.Local,
            [FullCandidate(last: restorePoint)]));
    }

    public static LogCoverageRequest CoverageRequest(
        LogDataStartDecision start,
        bool enumerationComplete,
        params LogCoverageCandidate[] candidates)
    {
        return new LogCoverageRequest(
            start,
            enumerationComplete,
            candidates,
            expectedUseChecksum: true,
            expectedUseCompression: false);
    }

    public static LogTechnicalReadinessFacts ReadyFacts()
    {
        return new LogTechnicalReadinessFacts
        {
            DatabaseId = DatabaseId,
            IsOnline = true,
            RecoveryModel = DatabaseRecoveryModel.Full,
            ManagedDatabase = Database,
            SourceDatabase = Database,
            CurrentBranch = Branch,
            LastLogBackupLsn = new BackupLsn(500),
            Capability = LogCapabilityState.Supported,
            ResourceAvailability = LogResourceAvailability.Available,
            ObservationStatus = LogEvidenceStatus.Complete,
            Source = LogObservationSource.RecoveryStatus,
            ObservedAtUtc = Utc(2026, 10, 23, 10, 0),
        };
    }

    public static DateTimeOffset Utc(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second = 0,
        int millisecond = 0)
    {
        return new DateTimeOffset(year, month, day, hour, minute, second, millisecond, TimeSpan.Zero);
    }
}
