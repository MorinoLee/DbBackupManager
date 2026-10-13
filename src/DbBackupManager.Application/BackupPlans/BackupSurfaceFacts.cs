using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.BackupPlans;

public readonly record struct BackupOperationFact(
    BackupExecutionOperationKind Kind,
    int Sequence,
    BackupExecutionOperationState State,
    BackupExecutionOutcome Outcome,
    bool SqlSuccessObserved);

public readonly record struct BackupDependencyFact(
    Guid AttemptId,
    BackupMetadataState TypeState,
    BackupType? Type,
    Guid? BaseBackupSetId,
    BackupMetadataState AssessmentState,
    DifferentialBaselineConclusion? Conclusion,
    bool HasMetadataConflict);

public sealed record BackupTaskSurface(
    string BackupType,
    Guid? PlanId,
    Guid? PlanVersionId,
    int? PlanVersionNumber,
    string SqlStatus,
    string FileVerifyStatus,
    string TransferStatus,
    string DependencyStatus);

public static class BackupSurfaceFacts
{
    public const string Unknown = "Unknown";

    public const string Succeeded = "Succeeded";

    public const string Failed = "Failed";

    public const string Indeterminate = "Indeterminate";

    public const string Cancelled = "Cancelled";

    public const string NotApplicable = "NotApplicable";

    public const string Verified = "Verified";

    public const string Mismatch = "Mismatch";

    public static BackupTaskSurface Describe(
        BackupType backupType,
        Guid? planId,
        Guid? planVersionId,
        int? planVersionNumber,
        BackupStorageMode? storageMode,
        Guid? currentAttemptId,
        IReadOnlyList<BackupOperationFact> operations,
        IReadOnlyList<BackupDependencyFact> sets)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(sets);
        if (!Enum.IsDefined(backupType))
        {
            throw new ArgumentOutOfRangeException(nameof(backupType), backupType, "枚举值无效。");
        }

        if (storageMode is { } mode && !Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(storageMode), storageMode, "枚举值无效。");
        }

        var transfer = Latest(operations, BackupExecutionOperationKind.Transfer);
        return new BackupTaskSurface(
            backupType.ToString(),
            planId,
            planVersionId,
            planVersionNumber,
            Status(Latest(operations, BackupExecutionOperationKind.SqlResult), requireSqlSuccess: true),
            Status(Latest(operations, BackupExecutionOperationKind.VerifyLocal), requireSqlSuccess: false),
            transfer is null && storageMode == BackupStorageMode.LocalOnly
                ? NotApplicable
                : Status(transfer, requireSqlSuccess: false),
            Dependency(sets, currentAttemptId));
    }

    public static Guid? DifferentialBase(BackupMetadataState typeState, BackupType? type, Guid? baseBackupSetId) =>
        typeState == BackupMetadataState.Known && type == BackupType.Differential ? baseBackupSetId : null;

    private static BackupOperationFact? Latest(
        IReadOnlyList<BackupOperationFact> operations,
        BackupExecutionOperationKind kind)
    {
        BackupOperationFact? latest = null;
        foreach (var operation in operations)
        {
            if (operation.Kind != kind || operation.State == BackupExecutionOperationState.Abandoned)
            {
                continue;
            }

            if (latest is null || operation.Sequence > latest.Value.Sequence)
            {
                latest = operation;
            }
        }

        return latest;
    }

    private static string Status(BackupOperationFact? operation, bool requireSqlSuccess)
    {
        if (operation is null)
        {
            return Unknown;
        }

        if (operation.Value.State != BackupExecutionOperationState.Applied)
        {
            return Indeterminate;
        }

        return operation.Value.Outcome switch
        {
            BackupExecutionOutcome.Succeeded when !requireSqlSuccess || operation.Value.SqlSuccessObserved => Succeeded,
            BackupExecutionOutcome.ConfirmedFailed => Failed,
            BackupExecutionOutcome.Cancelled => Cancelled,
            BackupExecutionOutcome.Succeeded or BackupExecutionOutcome.Indeterminate or BackupExecutionOutcome.Unknown => Indeterminate,
            _ => Unknown,
        };
    }

    private static string Dependency(IReadOnlyList<BackupDependencyFact> sets, Guid? currentAttemptId)
    {
        if (sets.Count == 0)
        {
            return Unknown;
        }

        var chosen = currentAttemptId is null
            ? []
            : sets.Where(set => set.AttemptId == currentAttemptId).ToArray();
        if (chosen.Length == 0)
        {
            if (sets.Count != 1)
            {
                return Unknown;
            }

            chosen = [sets[0]];
        }
        else if (chosen.Length != 1)
        {
            return Unknown;
        }

        var set = chosen[0];
        if (set.HasMetadataConflict)
        {
            return Mismatch;
        }

        if (set.TypeState != BackupMetadataState.Known || set.Type is null)
        {
            return Unknown;
        }

        if (set.Type == BackupType.Full
            && set.AssessmentState == BackupMetadataState.NotApplicable
            && set.BaseBackupSetId is null)
        {
            return NotApplicable;
        }

        if (set.Type != BackupType.Differential)
        {
            return Unknown;
        }

        if (set.Conclusion == DifferentialBaselineConclusion.Verified && set.BaseBackupSetId is not null)
        {
            return Verified;
        }

        return set.Conclusion == DifferentialBaselineConclusion.Mismatch ? Mismatch : Unknown;
    }
}

public static class BackupRecoverySurface
{
    public const string BaselineNone = "None";

    public const string BaselineKnown = "Known";

    public const string BaselineUnknown = "Unknown";

    private static readonly HashSet<string> GapReasons = new(StringComparer.Ordinal)
    {
        PlanLocationRecovery.RemoteBaseUnconfirmed,
        PlanLocationRecovery.RemoteDependentUnconfirmed,
        RecoveryProtectionReason.BaseUnavailableAtLocation,
    };

    /// <summary>
    /// 这些原因只说明副本为何留在窗口里，不表示位置健康。未列入缺口或此处的原因一律视为未知。
    /// </summary>
    private static readonly HashSet<string> BenignRetentionReasons = new(StringComparer.Ordinal)
    {
        RecoveryProtectionReason.InsideWindow,
        RecoveryProtectionReason.OutsideWindowUnneeded,
        RecoveryProtectionReason.LatestAvailableFull,
        RecoveryProtectionReason.LatestRestorePoint,
        RecoveryProtectionReason.WindowStartAnchor,
        RecoveryProtectionReason.DifferentialBase,
        RecoveryProtectionReason.ActiveDifferentialBaseline,
        PlanLocationRecovery.RemoteStagingReleasable,
    };

    public static BackupLocationRecovery Project(
        BackupFileLocation location,
        Guid? storageTargetId,
        int? windowDays,
        LocationRecoveryConclusion conclusion,
        ActiveBaseline baseline,
        IReadOnlyList<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        if (!Enum.IsDefined(location))
        {
            throw new ArgumentOutOfRangeException(nameof(location), location, "枚举值无效。");
        }

        if (!Enum.IsDefined(conclusion))
        {
            throw new ArgumentOutOfRangeException(nameof(conclusion), conclusion, "枚举值无效。");
        }

        var distinct = reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var gapReasons = distinct.Where(GapReasons.Contains).ToArray();
        var unknownReasons = distinct
            .Where(reason => !GapReasons.Contains(reason) && !BenignRetentionReasons.Contains(reason))
            .ToArray();
        var unknown = baseline.IsUnresolved || unknownReasons.Length > 0;
        var remoteUnrecoverable = location == BackupFileLocation.Remote
            && (gapReasons.Length > 0 || conclusion == LocationRecoveryConclusion.NotRestorable && !unknown);
        var baselineName = baseline.IsUnresolved
            ? BaselineUnknown
            : baseline.FullId is null ? BaselineNone : BaselineKnown;
        return new BackupLocationRecovery(
            location,
            storageTargetId,
            windowDays,
            conclusion,
            baselineName,
            baseline.IsUnresolved ? null : baseline.FullId,
            unknown,
            remoteUnrecoverable,
            gapReasons,
            unknownReasons);
    }
}
