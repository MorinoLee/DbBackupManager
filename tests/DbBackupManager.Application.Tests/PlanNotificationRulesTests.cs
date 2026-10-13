using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Application.Notifications;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Notifications;

namespace DbBackupManager.Application.Tests;

public sealed class PlanNotificationRulesTests
{
    [Theory]
    [InlineData(BackupTaskStatus.Failed, NotificationType.TaskFailed)]
    [InlineData(BackupTaskStatus.NeedsAttention, NotificationType.TaskIndeterminate)]
    public void CommittedFailureAndUnknownMapToDistinctNotices(BackupTaskStatus status, NotificationType type)
    {
        Assert.Equal(type, PlanNotificationRules.ForCommittedTask(status));
    }

    [Theory]
    [InlineData(BackupTaskStatus.Pending)]
    [InlineData(BackupTaskStatus.Running)]
    [InlineData(BackupTaskStatus.Succeeded)]
    [InlineData(BackupTaskStatus.Cancelled)]
    public void SuccessfulOrIdleTasksDoNotNotify(BackupTaskStatus status)
    {
        Assert.Null(PlanNotificationRules.ForCommittedTask(status));
    }

    [Theory]
    [InlineData(BackupReconciliationOutcome.Succeeded)]
    [InlineData(BackupReconciliationOutcome.SafeToRetry)]
    [InlineData(BackupReconciliationOutcome.Cancelled)]
    public void ConfirmedRecoveryDoesNotSendAnotherNotice(BackupReconciliationOutcome outcome)
    {
        Assert.Null(PlanNotificationRules.ForReconciliation(outcome));
    }

    [Fact]
    public void FinishedTaskWithoutStageStillProducesARemoteNoticeStage()
    {
        Assert.Equal(BackupTaskStage.Cleanup, PlanNotificationRules.RemoteNoticeStage(null));
        Assert.Equal(BackupTaskStage.ValidateCopy, PlanNotificationRules.RemoteNoticeStage(BackupTaskStage.ValidateCopy));
    }

    [Fact]
    public void EachRemoteGapIsASeparateNoticeKey()
    {
        var target = Guid.NewGuid();
        var gaps = Location(
            BackupFileLocation.Remote,
            LocationRecoveryConclusion.NotRestorable,
            unknown: false,
            remoteUnrecoverable: true,
            [],
            ["remote_dependent_unconfirmed", "remote_base_unconfirmed"],
            target);
        var generic = Location(
            BackupFileLocation.Remote,
            LocationRecoveryConclusion.NotRestorable,
            unknown: false,
            remoteUnrecoverable: true,
            [],
            [],
            target);
        var unresolved = Location(
            BackupFileLocation.Remote,
            LocationRecoveryConclusion.NotRestorable,
            unknown: true,
            remoteUnrecoverable: false,
            ["baseline_unresolved"],
            [],
            target);

        var opened = PlanNotificationRules.RemoteGapCodes(gaps);
        Assert.True(opened.UpdatesOpenEpisodes);
        Assert.Equal(["remote_dependent_unconfirmed", "remote_base_unconfirmed"], opened.Codes);
        var genericCodes = PlanNotificationRules.RemoteGapCodes(generic);
        Assert.True(genericCodes.UpdatesOpenEpisodes);
        Assert.Equal([PlanNotificationRules.LocationNotRestorable], genericCodes.Codes);
        Assert.False(PlanNotificationRules.RemoteGapCodes(unresolved).UpdatesOpenEpisodes);
    }

    [Fact]
    public void UnknownOrUnprovenLocationDoesNotOpenOrCloseARemoteGap()
    {
        var target = Guid.NewGuid();
        var unknownWithGap = Location(
            BackupFileLocation.Remote,
            LocationRecoveryConclusion.NotRestorable,
            unknown: true,
            remoteUnrecoverable: true,
            ["baseline_unresolved"],
            ["remote_base_unconfirmed"],
            target);
        var restorable = Location(
            BackupFileLocation.Remote,
            LocationRecoveryConclusion.RestorableWindowSatisfied,
            unknown: false,
            remoteUnrecoverable: false,
            [],
            [],
            target);
        var notRestorableButNotRemoteFailure = Location(
            BackupFileLocation.Remote,
            LocationRecoveryConclusion.NotRestorable,
            unknown: false,
            remoteUnrecoverable: false,
            [],
            [],
            target);

        Assert.False(PlanNotificationRules.RemoteGapCodes(unknownWithGap).UpdatesOpenEpisodes);
        Assert.Empty(PlanNotificationRules.RemoteGapCodes(unknownWithGap).Codes);
        var recovered = PlanNotificationRules.RemoteGapCodes(restorable);
        Assert.True(recovered.UpdatesOpenEpisodes);
        Assert.Empty(recovered.Codes);
        Assert.False(PlanNotificationRules.RemoteGapCodes(notRestorableButNotRemoteFailure).UpdatesOpenEpisodes);
    }

    [Fact]
    public void UnresolvedBaselineStaysUnknownAndDoesNotAdviseLogBackup()
    {
        var location = Location(
            BackupFileLocation.Remote,
            LocationRecoveryConclusion.NotRestorable,
            unknown: true,
            remoteUnrecoverable: false,
            ["baseline_unresolved"],
            []);

        Assert.Null(PlanNotificationRules.RemoteErrorCode(location));
        Assert.False(PlanNotificationRules.IsRemoteGapReason("baseline_unresolved"));
        Assert.False(PlanNotificationRules.IsRemoteGapReason(RecoveryProtectionReason.BaselineUnresolved));
        Assert.DoesNotContain(
            "enable_log",
            PlanNotificationRules.Fallback(NotificationType.TaskIndeterminate),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoteGapUsesTheProjectedReasonAndGenericCodeIsSecondary()
    {
        var gap = Location(
            BackupFileLocation.Remote,
            LocationRecoveryConclusion.NotRestorable,
            unknown: true,
            remoteUnrecoverable: true,
            [],
            ["remote_base_unconfirmed"]);
        var generic = Location(
            BackupFileLocation.Remote,
            LocationRecoveryConclusion.NotRestorable,
            unknown: false,
            remoteUnrecoverable: true,
            [],
            []);
        var local = Location(
            BackupFileLocation.Local,
            LocationRecoveryConclusion.NotRestorable,
            unknown: false,
            remoteUnrecoverable: true,
            [],
            ["remote_base_unconfirmed"]);

        Assert.Equal("remote_base_unconfirmed", PlanNotificationRules.RemoteErrorCode(gap));
        Assert.Equal(PlanNotificationRules.LocationNotRestorable, PlanNotificationRules.RemoteErrorCode(generic));
        Assert.Null(PlanNotificationRules.RemoteErrorCode(local));
        Assert.True(PlanNotificationRules.IsRemoteGapReason(PlanLocationRecovery.RemoteBaseUnconfirmed));
        Assert.True(PlanNotificationRules.IsRemoteGapReason(PlanLocationRecovery.RemoteDependentUnconfirmed));
        Assert.True(PlanNotificationRules.IsRemoteGapReason(RecoveryProtectionReason.BaseUnavailableAtLocation));
        Assert.False(PlanNotificationRules.IsRemoteGapReason("remote.staging_retained"));
        Assert.Equal(
            "remote_base_unconfirmed",
            PlanNotificationRules.PreferRemoteCode(PlanNotificationRules.LocationNotRestorable, "remote_base_unconfirmed"));
    }

    private static BackupLocationRecovery Location(
        BackupFileLocation place,
        LocationRecoveryConclusion conclusion,
        bool unknown,
        bool remoteUnrecoverable,
        string[] unknownReasons,
        string[] gapReasons,
        Guid? storageTargetId = null) =>
        new(place, storageTargetId ?? Guid.NewGuid(), 7, conclusion, "Unknown", null, unknown, remoteUnrecoverable, gapReasons, unknownReasons);
}
