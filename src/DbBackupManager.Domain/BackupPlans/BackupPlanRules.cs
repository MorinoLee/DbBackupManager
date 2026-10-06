using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.BackupPlans;

public enum BackupRunPurpose
{
    PlanFull = 1,
    PlanDifferential = 2,
    PlanLog = 3,
    AdHocCopyOnlyFull = 4
}

public enum BackupPlanConfigurationNotice
{
    None = 0,
    LogRecoveryModelUnsupported = 1
}

public enum BackupAdmissionStatus
{
    Allowed = 1,
    PlanPaused = 2,
    TypeNotInPlan = 3,
    RecoveryModelUnsupported = 4,
    BaselineRequired = 5,
    DatabaseBackupBusy = 6,
    LogBackupBusy = 7,
    DatabaseAndLogMustQueue = 8
}

public readonly record struct BackupPlanProtectionOptions
{
    public BackupPlanProtectionOptions(bool useChecksum, bool useCompression)
    {
        UseChecksum = useChecksum;
        UseCompression = useCompression;
    }

    public static BackupPlanProtectionOptions Default { get; } = new(useChecksum: true, useCompression: false);

    public bool UseChecksum { get; }

    public bool UseCompression { get; }
}

public readonly record struct BackupAdmissionRequest
{
    public BackupAdmissionRequest(
        BackupPlanMode mode,
        BackupRunPurpose purpose,
        BackupTaskTriggerType trigger,
        bool planIsPaused,
        DatabaseRecoveryModel recoveryModel,
        bool hasManagedBaseline,
        bool hasValidLogSequence,
        BackupPlanProtectionOptions protection,
        BackupRunPurpose? activePurpose,
        bool allowDatabaseBackupAndLogOverlap)
    {
        BackupPlanRules.RequireDefined(mode, nameof(mode));
        BackupPlanRules.RequireDefined(purpose, nameof(purpose));
        BackupPlanRules.RequireDefined(trigger, nameof(trigger));
        BackupPlanRules.RequireDefined(recoveryModel, nameof(recoveryModel));
        if (purpose == BackupRunPurpose.AdHocCopyOnlyFull && trigger != BackupTaskTriggerType.Manual)
        {
            throw new ArgumentException("临时完整备份只能由管理员手动发起。", nameof(purpose));
        }

        if (activePurpose is { } active)
        {
            BackupPlanRules.RequireDefined(active, nameof(activePurpose));
        }

        Mode = mode;
        Purpose = purpose;
        Trigger = trigger;
        PlanIsPaused = planIsPaused;
        RecoveryModel = recoveryModel;
        HasManagedBaseline = hasManagedBaseline;
        HasValidLogSequence = hasValidLogSequence;
        Protection = protection;
        ActivePurpose = activePurpose;
        AllowDatabaseBackupAndLogOverlap = allowDatabaseBackupAndLogOverlap;
    }

    public BackupPlanMode Mode { get; }

    public BackupRunPurpose Purpose { get; }

    public BackupTaskTriggerType Trigger { get; }

    public bool PlanIsPaused { get; }

    public DatabaseRecoveryModel RecoveryModel { get; }

    public bool HasManagedBaseline { get; }

    public bool HasValidLogSequence { get; }

    public BackupPlanProtectionOptions Protection { get; }

    public BackupRunPurpose? ActivePurpose { get; }

    public bool AllowDatabaseBackupAndLogOverlap { get; }
}

public readonly record struct BackupAdmissionDecision
{
    private BackupAdmissionDecision(
        BackupAdmissionStatus status,
        bool useCopyOnly,
        bool useChecksum,
        bool useCompression,
        bool changesDifferentialBase)
    {
        Status = status;
        UseCopyOnly = useCopyOnly;
        UseChecksum = useChecksum;
        UseCompression = useCompression;
        ChangesDifferentialBase = changesDifferentialBase;
    }

    public BackupAdmissionStatus Status { get; }

    public bool UseCopyOnly { get; }

    public bool UseChecksum { get; }

    public bool UseCompression { get; }

    public bool ChangesDifferentialBase { get; }

    public bool IsAllowed => Status == BackupAdmissionStatus.Allowed;

    public static BackupAdmissionDecision Deny(BackupAdmissionStatus status)
    {
        if (status == BackupAdmissionStatus.Allowed)
        {
            throw new ArgumentException("拒绝结果不能使用允许状态。", nameof(status));
        }

        BackupPlanRules.RequireDefined(status, nameof(status));
        return new(status, false, false, false, false);
    }

    public static BackupAdmissionDecision Allow(
        bool useCopyOnly,
        bool useChecksum,
        bool useCompression,
        bool changesDifferentialBase)
    {
        return new(BackupAdmissionStatus.Allowed, useCopyOnly, useChecksum, useCompression, changesDifferentialBase);
    }
}

public static class BackupPlanRules
{
    public static bool Includes(BackupPlanMode mode, BackupType backupType)
    {
        RequireDefined(mode, nameof(mode));
        RequireDefined(backupType, nameof(backupType));
        return mode switch
        {
            BackupPlanMode.Full => backupType == BackupType.Full,
            BackupPlanMode.FullAndDifferential => backupType is BackupType.Full or BackupType.Differential,
            BackupPlanMode.FullAndDifferentialAndLog =>
                backupType is BackupType.Full or BackupType.Differential or BackupType.Log,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "枚举值无效。"),
        };
    }

    public static BackupType ToBackupType(BackupRunPurpose purpose)
    {
        RequireDefined(purpose, nameof(purpose));
        return purpose switch
        {
            BackupRunPurpose.PlanFull or BackupRunPurpose.AdHocCopyOnlyFull => BackupType.Full,
            BackupRunPurpose.PlanDifferential => BackupType.Differential,
            BackupRunPurpose.PlanLog => BackupType.Log,
            _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "枚举值无效。"),
        };
    }

    public static bool UseCopyOnly(BackupRunPurpose purpose)
    {
        RequireDefined(purpose, nameof(purpose));
        return purpose == BackupRunPurpose.AdHocCopyOnlyFull;
    }

    public static bool ChangesDifferentialBase(BackupRunPurpose purpose)
    {
        RequireDefined(purpose, nameof(purpose));
        return purpose == BackupRunPurpose.PlanFull;
    }

    public static bool UsesDatabaseBackupSlot(BackupRunPurpose purpose)
    {
        RequireDefined(purpose, nameof(purpose));
        return purpose != BackupRunPurpose.PlanLog;
    }

    public static bool RequiresImmediatePlanFull(BackupPlanMode mode, bool hasManagedBaseline)
    {
        return (Includes(mode, BackupType.Differential) || Includes(mode, BackupType.Log)) && !hasManagedBaseline;
    }

    public static BackupPlanConfigurationNotice ConfigurationNotice(
        BackupPlanMode mode,
        DatabaseRecoveryModel recoveryModel)
    {
        RequireDefined(recoveryModel, nameof(recoveryModel));
        if (!Includes(mode, BackupType.Log))
        {
            return BackupPlanConfigurationNotice.None;
        }

        return recoveryModel == DatabaseRecoveryModel.Full
            ? BackupPlanConfigurationNotice.None
            : BackupPlanConfigurationNotice.LogRecoveryModelUnsupported;
    }

    public static BackupAdmissionDecision Evaluate(BackupAdmissionRequest request)
    {
        if (request.Trigger == BackupTaskTriggerType.Scheduled && request.PlanIsPaused)
        {
            return BackupAdmissionDecision.Deny(BackupAdmissionStatus.PlanPaused);
        }

        if (!IsPurposeInPlan(request.Mode, request.Purpose))
        {
            return BackupAdmissionDecision.Deny(BackupAdmissionStatus.TypeNotInPlan);
        }

        if (request.Purpose == BackupRunPurpose.PlanLog
            && request.RecoveryModel != DatabaseRecoveryModel.Full)
        {
            return BackupAdmissionDecision.Deny(BackupAdmissionStatus.RecoveryModelUnsupported);
        }

        if (RequiresEstablishedBaseline(request.Purpose)
            && !request.HasManagedBaseline
            && !LogSequenceAllowsBackup(request))
        {
            return BackupAdmissionDecision.Deny(BackupAdmissionStatus.BaselineRequired);
        }

        if (ResourceConflict(request.Purpose, request.ActivePurpose, request.AllowDatabaseBackupAndLogOverlap) is { } conflict)
        {
            return BackupAdmissionDecision.Deny(conflict);
        }

        return BackupAdmissionDecision.Allow(
            UseCopyOnly(request.Purpose),
            request.Protection.UseChecksum,
            request.Protection.UseCompression,
            ChangesDifferentialBase(request.Purpose));
    }

    internal static void RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "枚举值无效。");
        }
    }

    private static bool IsPurposeInPlan(BackupPlanMode mode, BackupRunPurpose purpose)
    {
        return purpose == BackupRunPurpose.AdHocCopyOnlyFull || Includes(mode, ToBackupType(purpose));
    }

    private static bool RequiresEstablishedBaseline(BackupRunPurpose purpose)
    {
        return purpose is BackupRunPurpose.PlanDifferential or BackupRunPurpose.PlanLog;
    }

    private static bool LogSequenceAllowsBackup(BackupAdmissionRequest request)
    {
        return request.Purpose == BackupRunPurpose.PlanLog && request.HasValidLogSequence;
    }

    private static BackupAdmissionStatus? ResourceConflict(
        BackupRunPurpose incoming,
        BackupRunPurpose? active,
        bool allowDatabaseBackupAndLogOverlap)
    {
        if (active is null)
        {
            return null;
        }

        var incomingUsesDatabase = UsesDatabaseBackupSlot(incoming);
        var activeUsesDatabase = UsesDatabaseBackupSlot(active.Value);
        if (incomingUsesDatabase == activeUsesDatabase)
        {
            return incomingUsesDatabase
                ? BackupAdmissionStatus.DatabaseBackupBusy
                : BackupAdmissionStatus.LogBackupBusy;
        }

        return allowDatabaseBackupAndLogOverlap
            ? null
            : BackupAdmissionStatus.DatabaseAndLogMustQueue;
    }
}
