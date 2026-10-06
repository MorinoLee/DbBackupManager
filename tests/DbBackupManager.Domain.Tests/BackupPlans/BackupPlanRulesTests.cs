using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupPlans;

public sealed class BackupPlanRulesTests
{
    [Theory]
    [InlineData(BackupPlanMode.Full, BackupType.Full, true)]
    [InlineData(BackupPlanMode.Full, BackupType.Differential, false)]
    [InlineData(BackupPlanMode.Full, BackupType.Log, false)]
    [InlineData(BackupPlanMode.FullAndDifferential, BackupType.Full, true)]
    [InlineData(BackupPlanMode.FullAndDifferential, BackupType.Differential, true)]
    [InlineData(BackupPlanMode.FullAndDifferential, BackupType.Log, false)]
    [InlineData(BackupPlanMode.FullAndDifferentialAndLog, BackupType.Full, true)]
    [InlineData(BackupPlanMode.FullAndDifferentialAndLog, BackupType.Differential, true)]
    [InlineData(BackupPlanMode.FullAndDifferentialAndLog, BackupType.Log, true)]
    public void PlanModeIncludesOnlyItsBackupTypes(BackupPlanMode mode, BackupType backupType, bool included)
    {
        Assert.Equal(included, BackupPlanRules.Includes(mode, backupType));
    }

    [Theory]
    [InlineData(BackupRunPurpose.PlanFull, BackupType.Full, false, true, true)]
    [InlineData(BackupRunPurpose.PlanDifferential, BackupType.Differential, false, false, true)]
    [InlineData(BackupRunPurpose.PlanLog, BackupType.Log, false, false, false)]
    [InlineData(BackupRunPurpose.AdHocCopyOnlyFull, BackupType.Full, true, false, true)]
    public void PurposeFixesBackupTypeCopyOnlyBaselineAndDatabaseSlot(
        BackupRunPurpose purpose,
        BackupType backupType,
        bool useCopyOnly,
        bool changesDifferentialBase,
        bool usesDatabaseBackupSlot)
    {
        Assert.Equal(backupType, BackupPlanRules.ToBackupType(purpose));
        Assert.Equal(useCopyOnly, BackupPlanRules.UseCopyOnly(purpose));
        Assert.Equal(changesDifferentialBase, BackupPlanRules.ChangesDifferentialBase(purpose));
        Assert.Equal(usesDatabaseBackupSlot, BackupPlanRules.UsesDatabaseBackupSlot(purpose));
    }

    [Theory]
    [InlineData(BackupPlanMode.Full, false, false)]
    [InlineData(BackupPlanMode.Full, true, false)]
    [InlineData(BackupPlanMode.FullAndDifferential, false, true)]
    [InlineData(BackupPlanMode.FullAndDifferential, true, false)]
    [InlineData(BackupPlanMode.FullAndDifferentialAndLog, false, true)]
    [InlineData(BackupPlanMode.FullAndDifferentialAndLog, true, false)]
    public void ImmediatePlanFullIsRequiredOnlyForChainModesWithoutBaseline(
        BackupPlanMode mode,
        bool hasManagedBaseline,
        bool requiresImmediatePlanFull)
    {
        Assert.Equal(requiresImmediatePlanFull, BackupPlanRules.RequiresImmediatePlanFull(mode, hasManagedBaseline));
    }

    [Theory]
    [InlineData(BackupPlanMode.Full, DatabaseRecoveryModel.Full, BackupPlanConfigurationNotice.None)]
    [InlineData(BackupPlanMode.Full, DatabaseRecoveryModel.Simple, BackupPlanConfigurationNotice.None)]
    [InlineData(BackupPlanMode.FullAndDifferential, DatabaseRecoveryModel.Full, BackupPlanConfigurationNotice.None)]
    [InlineData(BackupPlanMode.FullAndDifferential, DatabaseRecoveryModel.BulkLogged, BackupPlanConfigurationNotice.None)]
    [InlineData(BackupPlanMode.FullAndDifferentialAndLog, DatabaseRecoveryModel.Full, BackupPlanConfigurationNotice.None)]
    [InlineData(BackupPlanMode.FullAndDifferentialAndLog, DatabaseRecoveryModel.Simple, BackupPlanConfigurationNotice.LogRecoveryModelUnsupported)]
    [InlineData(BackupPlanMode.FullAndDifferentialAndLog, DatabaseRecoveryModel.BulkLogged, BackupPlanConfigurationNotice.LogRecoveryModelUnsupported)]
    [InlineData(BackupPlanMode.FullAndDifferentialAndLog, DatabaseRecoveryModel.Unknown, BackupPlanConfigurationNotice.LogRecoveryModelUnsupported)]
    public void ConfigurationNoticeStaysSilentUnlessLogIsEnabledOnUnsupportedRecovery(
        BackupPlanMode mode,
        DatabaseRecoveryModel recoveryModel,
        BackupPlanConfigurationNotice notice)
    {
        Assert.Equal(notice, BackupPlanRules.ConfigurationNotice(mode, recoveryModel));
    }

    [Fact]
    public void ConfigurationNoticesDoNotSuggestEnablingLog()
    {
        Assert.DoesNotContain(
            Enum.GetNames<BackupPlanConfigurationNotice>(),
            name => name.Contains("Suggest", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Recommend", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AdmittedRunUsesPlanChecksumAndCompression()
    {
        var decision = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.FullAndDifferential,
            BackupRunPurpose.PlanDifferential,
            hasManagedBaseline: true,
            protection: new BackupPlanProtectionOptions(useChecksum: false, useCompression: true)));

        Assert.True(decision.IsAllowed);
        Assert.False(decision.UseCopyOnly);
        Assert.False(decision.UseChecksum);
        Assert.True(decision.UseCompression);
        Assert.False(decision.ChangesDifferentialBase);
    }

    [Fact]
    public void PlanFullChangesDifferentialBaseAndAdHocFullDoesNot()
    {
        var planFull = BackupPlanRules.Evaluate(Request(BackupPlanMode.Full, BackupRunPurpose.PlanFull));
        var adHoc = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.Full,
            BackupRunPurpose.AdHocCopyOnlyFull,
            trigger: BackupTaskTriggerType.Manual));

        Assert.True(planFull.IsAllowed);
        Assert.True(planFull.ChangesDifferentialBase);
        Assert.False(planFull.UseCopyOnly);
        Assert.True(adHoc.IsAllowed);
        Assert.False(adHoc.ChangesDifferentialBase);
        Assert.True(adHoc.UseCopyOnly);
        Assert.True(adHoc.UseChecksum);
        Assert.False(adHoc.UseCompression);
    }

    [Fact]
    public void DifferentialWithoutManagedBaselineIsRejected()
    {
        var decision = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.FullAndDifferential,
            BackupRunPurpose.PlanDifferential));

        Assert.Equal(BackupAdmissionStatus.BaselineRequired, decision.Status);
    }

    [Fact]
    public void LogOnPlanWithoutLogIsTypeMismatchRatherThanRecoveryWarning()
    {
        var decision = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.Full,
            BackupRunPurpose.PlanLog,
            recoveryModel: DatabaseRecoveryModel.Simple));

        Assert.Equal(BackupAdmissionStatus.TypeNotInPlan, decision.Status);
    }

    [Fact]
    public void LogRequiresFullRecoveryEvenWhenBaselineExists()
    {
        var decision = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.FullAndDifferentialAndLog,
            BackupRunPurpose.PlanLog,
            recoveryModel: DatabaseRecoveryModel.BulkLogged,
            hasManagedBaseline: true));

        Assert.Equal(BackupAdmissionStatus.RecoveryModelUnsupported, decision.Status);
    }

    [Fact]
    public void LogCanContinueFromValidSequenceAfterPeriodicFullFailure()
    {
        var decision = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.FullAndDifferentialAndLog,
            BackupRunPurpose.PlanLog,
            hasValidLogSequence: true));

        Assert.True(decision.IsAllowed);
        Assert.False(decision.ChangesDifferentialBase);
    }

    [Fact]
    public void LogWithoutBaselineOrSequenceIsRejected()
    {
        var decision = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.FullAndDifferentialAndLog,
            BackupRunPurpose.PlanLog));

        Assert.Equal(BackupAdmissionStatus.BaselineRequired, decision.Status);
    }

    [Fact]
    public void PausedPlanBlocksScheduledRunsAndAllowsManualRuns()
    {
        var scheduled = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.FullAndDifferential,
            BackupRunPurpose.PlanDifferential,
            planIsPaused: true,
            hasManagedBaseline: true));
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
    public void DatabaseBackupsShareOneSlotIncludingAdHocFull()
    {
        var differential = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.FullAndDifferential,
            BackupRunPurpose.PlanDifferential,
            hasManagedBaseline: true,
            activePurpose: BackupRunPurpose.PlanFull));
        var adHoc = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.Full,
            BackupRunPurpose.AdHocCopyOnlyFull,
            trigger: BackupTaskTriggerType.Manual,
            activePurpose: BackupRunPurpose.PlanDifferential));

        Assert.Equal(BackupAdmissionStatus.DatabaseBackupBusy, differential.Status);
        Assert.Equal(BackupAdmissionStatus.DatabaseBackupBusy, adHoc.Status);
    }

    [Fact]
    public void LogOverlapsDatabaseBackupOnlyWhenThatOverlapIsEnabled()
    {
        var queued = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.FullAndDifferentialAndLog,
            BackupRunPurpose.PlanLog,
            hasManagedBaseline: true,
            activePurpose: BackupRunPurpose.PlanFull));
        var overlapped = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.FullAndDifferentialAndLog,
            BackupRunPurpose.PlanLog,
            hasManagedBaseline: true,
            activePurpose: BackupRunPurpose.PlanFull,
            allowDatabaseBackupAndLogOverlap: true));
        var secondLog = BackupPlanRules.Evaluate(Request(
            BackupPlanMode.FullAndDifferentialAndLog,
            BackupRunPurpose.PlanLog,
            hasManagedBaseline: true,
            activePurpose: BackupRunPurpose.PlanLog,
            allowDatabaseBackupAndLogOverlap: true));

        Assert.Equal(BackupAdmissionStatus.DatabaseAndLogMustQueue, queued.Status);
        Assert.True(overlapped.IsAllowed);
        Assert.Equal(BackupAdmissionStatus.LogBackupBusy, secondLog.Status);
    }

    [Fact]
    public void ScheduledAdHocFullIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Request(
            BackupPlanMode.Full,
            BackupRunPurpose.AdHocCopyOnlyFull,
            trigger: BackupTaskTriggerType.Scheduled));
    }

    [Fact]
    public void UndefinedPlanModeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BackupPlanRules.Includes((BackupPlanMode)0, BackupType.Full));
    }

    private static BackupAdmissionRequest Request(
        BackupPlanMode mode,
        BackupRunPurpose purpose,
        BackupTaskTriggerType trigger = BackupTaskTriggerType.Scheduled,
        bool planIsPaused = false,
        DatabaseRecoveryModel recoveryModel = DatabaseRecoveryModel.Full,
        bool hasManagedBaseline = false,
        bool hasValidLogSequence = false,
        BackupPlanProtectionOptions? protection = null,
        BackupRunPurpose? activePurpose = null,
        bool allowDatabaseBackupAndLogOverlap = false)
    {
        return new BackupAdmissionRequest(
            mode,
            purpose,
            trigger,
            planIsPaused,
            recoveryModel,
            hasManagedBaseline,
            hasValidLogSequence,
            protection ?? BackupPlanProtectionOptions.Default,
            activePurpose,
            allowDatabaseBackupAndLogOverlap);
    }
}
