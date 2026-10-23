using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupLogs;

public sealed class LogRegressionTests
{
    [Fact]
    public void A14ExistingBackupTypePlanModePurposeAndAdmissionValuesDoNotDrift()
    {
        Assert.Equal(1, (int)BackupType.Full);
        Assert.Equal(2, (int)BackupType.Differential);
        Assert.Equal(3, (int)BackupType.Log);
        Assert.Equal(1, (int)BackupPlanMode.Full);
        Assert.Equal(2, (int)BackupPlanMode.FullAndDifferential);
        Assert.Equal(3, (int)BackupPlanMode.FullAndDifferentialAndLog);
        Assert.Equal(1, (int)BackupRunPurpose.PlanFull);
        Assert.Equal(2, (int)BackupRunPurpose.PlanDifferential);
        Assert.Equal(3, (int)BackupRunPurpose.PlanLog);
        Assert.Equal(6, (int)BackupAdmissionStatus.DatabaseBackupBusy);
        Assert.Equal(7, (int)BackupAdmissionStatus.LogBackupBusy);
        Assert.Equal(8, (int)BackupAdmissionStatus.DatabaseAndLogMustQueue);
    }

    [Fact]
    public void A14FullAndDifferentialAdmissionKeepsItsOriginalBehavior()
    {
        var full = BackupPlanRules.Evaluate(Request(BackupPlanMode.Full, BackupRunPurpose.PlanFull));
        var differential = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.FullAndDifferential,
            BackupRunPurpose.PlanDifferential,
            hasManagedBaseline: true));
        var missingBaseline = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.FullAndDifferential,
            BackupRunPurpose.PlanDifferential));

        Assert.True(full.IsAllowed);
        Assert.True(full.ChangesDifferentialBase);
        Assert.True(differential.IsAllowed);
        Assert.Equal(BackupAdmissionStatus.BaselineRequired, missingBaseline.Status);
    }

    [Fact]
    public void A14PausedAndManualBehaviorForDatabaseBackupsIsUnchanged()
    {
        var scheduled = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.Full,
            BackupRunPurpose.PlanFull,
            planIsPaused: true));
        var manual = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.FullAndDifferential,
            BackupRunPurpose.PlanDifferential,
            trigger: BackupTaskTriggerType.Manual,
            planIsPaused: true,
            hasManagedBaseline: true));

        Assert.Equal(BackupAdmissionStatus.PlanPaused, scheduled.Status);
        Assert.True(manual.IsAllowed);
    }

    [Fact]
    public void A14DatabaseAndLogResourceSlotsRemainDistinct()
    {
        var database = BackupSlotOccupancy.ForPurpose(BackupRunPurpose.PlanFull);
        var differential = BackupSlotOccupancy.ForPurpose(BackupRunPurpose.PlanDifferential);
        var log = BackupSlotOccupancy.ForPurpose(BackupRunPurpose.PlanLog);

        Assert.True(database.DatabaseBackupOccupied);
        Assert.False(database.LogBackupOccupied);
        Assert.True(differential.DatabaseBackupOccupied);
        Assert.False(differential.LogBackupOccupied);
        Assert.False(log.DatabaseBackupOccupied);
        Assert.True(log.LogBackupOccupied);
    }

    [Fact]
    public void A14DatabaseBackupResourceConflictsAreUnchanged()
    {
        var databaseBusy = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.Full,
            BackupRunPurpose.PlanFull,
            slotOccupancy: BackupSlotOccupancy.ForPurpose(BackupRunPurpose.PlanDifferential)));
        var logBusyWithoutOverlap = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.Full,
            BackupRunPurpose.PlanFull,
            slotOccupancy: BackupSlotOccupancy.ForPurpose(BackupRunPurpose.PlanLog)));
        var logBusyWithOverlap = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.Full,
            BackupRunPurpose.PlanFull,
            slotOccupancy: BackupSlotOccupancy.ForPurpose(BackupRunPurpose.PlanLog),
            allowDatabaseBackupAndLogOverlap: true));

        Assert.Equal(BackupAdmissionStatus.DatabaseBackupBusy, databaseBusy.Status);
        Assert.Equal(BackupAdmissionStatus.DatabaseAndLogMustQueue, logBusyWithoutOverlap.Status);
        Assert.True(logBusyWithOverlap.IsAllowed);
    }

    [Fact]
    public void A14NoNewLogSuggestionNoticeAppearsForNonLogPlans()
    {
        Assert.Equal(2, Enum.GetValues<BackupPlanConfigurationNotice>().Length);
        Assert.DoesNotContain(
            Enum.GetNames<BackupPlanConfigurationNotice>(),
            name => name.Contains("Suggest", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Recommend", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            BackupPlanConfigurationNotice.None,
            BackupPlanRules.ConfigurationNotice(BackupPlanMode.FullAndDifferential, DatabaseRecoveryModel.Full));
        Assert.Equal(
            BackupPlanConfigurationNotice.LogRecoveryModelUnsupported,
            BackupPlanRules.ConfigurationNotice(
                BackupPlanMode.FullAndDifferentialAndLog,
                DatabaseRecoveryModel.Simple));
    }

    [Fact]
    public void A14ImmediatePlanFullAndTypeInclusionRemainUnchanged()
    {
        Assert.False(BackupPlanRules.RequiresImmediatePlanFull(BackupPlanMode.FullAndDifferential, hasManagedBaseline: true));
        Assert.True(BackupPlanRules.RequiresImmediatePlanFull(BackupPlanMode.FullAndDifferential, hasManagedBaseline: false));
        Assert.False(BackupPlanRules.RequiresImmediatePlanFull(BackupPlanMode.Full, hasManagedBaseline: false));
        Assert.True(BackupPlanRules.Includes(BackupPlanMode.FullAndDifferentialAndLog, BackupType.Log));
        Assert.False(BackupPlanRules.Includes(BackupPlanMode.FullAndDifferential, BackupType.Log));
    }

    private static BackupAdmissionRequest Request(
        BackupPlanMode mode,
        BackupRunPurpose purpose,
        BackupTaskTriggerType trigger = BackupTaskTriggerType.Scheduled,
        bool planIsPaused = false,
        bool hasManagedBaseline = false,
        BackupSlotOccupancy? slotOccupancy = null,
        bool allowDatabaseBackupAndLogOverlap = false)
    {
        return new BackupAdmissionRequest(
            mode,
            purpose,
            trigger,
            planIsPaused,
            DatabaseRecoveryModel.Full,
            hasManagedBaseline,
            hasValidLogSequence: false,
            BackupPlanProtectionOptions.Default,
            slotOccupancy ?? BackupSlotOccupancy.None,
            allowDatabaseBackupAndLogOverlap);
    }
}
