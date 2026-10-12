using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupPlans;

public sealed class PlanLocationRecoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LocalAndRemoteWindowsDoNotShareCopies()
    {
        var older = Now.AddDays(-40);
        var anchor = Now.AddDays(-10);
        var newest = Now.AddDays(-1);
        var remote = Guid.NewGuid();
        var results = PlanLocationRecovery.Evaluate(
            Now,
            ActiveBaseline.None,
            protectAllFullsBecauseDependencyUnproven: false,
            databaseActivityBlocksDeletion: false,
            [
                new(BackupFileLocation.Local, null, 7),
                new(BackupFileLocation.Remote, remote, 30),
            ],
            [
                Set(1, BackupType.Full, older),
                Set(2, BackupType.Full, anchor),
                Set(3, BackupType.Full, newest),
            ],
            [
                Copy(11, 1, BackupFileLocation.Local, null),
                Copy(12, 2, BackupFileLocation.Local, null),
                Copy(13, 3, BackupFileLocation.Local, null),
                Copy(21, 1, BackupFileLocation.Remote, remote),
                Copy(22, 2, BackupFileLocation.Remote, remote),
                Copy(23, 3, BackupFileLocation.Remote, remote),
            ]);

        var local = results.Single(result => result.Location == BackupFileLocation.Local);
        var remoteResult = results.Single(result => result.StorageTargetId == remote);
        Assert.Equal(LocationRecoveryConclusion.RestorableWindowSatisfied, local.Conclusion);
        Assert.Equal(RecoveryProtectionReason.OutsideWindowUnneeded, Reason(local, 11));
        Assert.Equal(RecoveryProtectionReason.WindowStartAnchor, Reason(local, 12));
        Assert.Equal(RecoveryProtectionReason.LatestAvailableFull, Reason(local, 13));
        Assert.Equal(LocationRecoveryConclusion.RestorableWindowSatisfied, remoteResult.Conclusion);
        Assert.Equal(RecoveryProtectionReason.WindowStartAnchor, Reason(remoteResult, 21));
        Assert.Equal(RecoveryWindowVerdict.Protected, Decision(remoteResult, 22).Verdict);
        Assert.Equal(RecoveryWindowVerdict.Protected, Decision(remoteResult, 23).Verdict);
    }

    [Fact]
    public void DifferentialKeepsItsBaseEvenWhenTheBaseIsOlderThanTheWindow()
    {
        var sunday = new DateTimeOffset(2026, 9, 27, 2, 0, 0, TimeSpan.Zero);
        var wednesday = new DateTimeOffset(2026, 9, 30, 2, 0, 0, TimeSpan.Zero);
        var result = Single(ActiveBaseline.None, [
            Set(1, BackupType.Full, sunday),
            Set(2, BackupType.Differential, wednesday, differentialBaseId: 1),
        ], [
            Copy(11, 1),
            Copy(12, 2),
        ]);

        Assert.Equal(RecoveryProtectionReason.DifferentialBase, Reason(result, 11));
        Assert.Equal(RecoveryWindowVerdict.Protected, Decision(result, 12).Verdict);
        Assert.Equal(LocationRecoveryConclusion.RestorableWindowSatisfied, result.Conclusion);
    }

    [Fact]
    public void RemoteDifferentialWithoutItsBaseIsNotRestorableAndIsNotDeleted()
    {
        var remote = Guid.NewGuid();
        var results = PlanLocationRecovery.Evaluate(
            Now,
            ActiveBaseline.None,
            false,
            false,
            [new(BackupFileLocation.Local, null, 7), new(BackupFileLocation.Remote, remote, 7)],
            [Set(1, BackupType.Full, Now.AddDays(-1)), Set(2, BackupType.Differential, Now.AddHours(-2), 1)],
            [Copy(11, 1), Copy(22, 2, BackupFileLocation.Remote, remote)]);

        var remoteResult = results.Single(result => result.Location == BackupFileLocation.Remote);
        Assert.Equal(LocationRecoveryConclusion.NotRestorable, remoteResult.Conclusion);
        Assert.Equal(RecoveryProtectionReason.BaseUnavailableAtLocation, Reason(remoteResult, 22));
        Assert.Equal(RecoveryWindowVerdict.Protected, Decision(results.Single(result => result.Location == BackupFileLocation.Local), 11).Verdict);
    }

    [Fact]
    public void UnknownCompletionUnprovenDependencyAndActiveWorkStayProtected()
    {
        var result = PlanLocationRecovery.Evaluate(
            Now,
            ActiveBaseline.Unresolved,
            protectAllFullsBecauseDependencyUnproven: true,
            databaseActivityBlocksDeletion: true,
            [new(BackupFileLocation.Local, null, 7)],
            [
                Set(1, BackupType.Full, null, proven: false),
                Set(2, BackupType.Differential, Now.AddDays(-1), differentialBaseId: null, proven: false),
            ],
            [Copy(11, 1), Copy(12, 2)]);

        Assert.Equal(RecoveryWindowVerdict.Protected, Decision(result.Single(), 11).Verdict);
        Assert.Equal(RecoveryWindowVerdict.Protected, Decision(result.Single(), 12).Verdict);
        Assert.NotEqual(LocationRecoveryConclusion.RestorableWindowSatisfied, result.Single().Conclusion);
        Assert.DoesNotContain(result.Single().Copies, copy => PlanLocationRecovery.IsDeletable(copy));
    }

    [Fact]
    public void ActiveBaselineIsKeptWithoutADifferential()
    {
        var id = Guid.NewGuid();
        var result = Single(
            ActiveBaseline.Known(id),
            [Set(id, BackupType.Full, Now.AddDays(-40))],
            [Copy(11, id)]);

        Assert.Equal(RecoveryProtectionReason.ActiveDifferentialBaseline, Reason(result, 11));
        Assert.Equal(RecoveryWindowVerdict.Protected, Decision(result, 11).Verdict);
    }

    [Fact]
    public void UnregisteredCopyRemoteStagingAndUnknownOwnerAreNeverDeletable()
    {
        var remote = Guid.NewGuid();
        var result = PlanLocationRecovery.Evaluate(
            Now,
            ActiveBaseline.None,
            false,
            false,
            [new(BackupFileLocation.Local, null, 7), new(BackupFileLocation.Remote, remote, 30)],
            [Set(1, BackupType.Full, Now.AddDays(-40)), Set(2, BackupType.Full, Now.AddDays(-1))],
            [
                Copy(11, (int?)null),
                Copy(12, 1, ownershipProven: false),
                Copy(13, 1, remoteOnlyStaging: true),
                Copy(21, 1, BackupFileLocation.Remote, remote),
            ]).Single(item => item.Location == BackupFileLocation.Local);

        Assert.Equal(PlanLocationRecovery.DependencyUnproven, Reason(result, 11));
        Assert.Equal(PlanLocationRecovery.OwnershipUnproven, Reason(result, 12));
        Assert.Equal(PlanLocationRecovery.RemoteStagingDeferred, Reason(result, 13));
        Assert.DoesNotContain(result.Copies, PlanLocationRecovery.IsDeletable);
    }

    [Fact]
    public void RegisteredDifferentialWithoutACopyStillProtectsItsBase()
    {
        var result = Single(
            ActiveBaseline.None,
            [
                Set(1, BackupType.Full, Now.AddDays(-40)),
                Set(2, BackupType.Full, Now.AddDays(-1)),
                Set(3, BackupType.Differential, Now.AddHours(-1), 1),
            ],
            [Copy(11, 1), Copy(12, 2)]);

        Assert.Equal(RecoveryProtectionReason.DifferentialBase, Reason(result, 11));
        Assert.False(PlanLocationRecovery.IsDeletable(Decision(result, 11)));
    }

    [Fact]
    public void RemoteDifferentialWithoutItsBaseKeepsNecessaryStagingVisible()
    {
        var remote = Guid.NewGuid();
        var results = PlanLocationRecovery.Evaluate(
            Now,
            ActiveBaseline.Unresolved,
            false,
            false,
            [new(BackupFileLocation.Local, null, null), new(BackupFileLocation.Remote, remote, 7)],
            [Set(1, BackupType.Full, Now.AddDays(-1)), Set(2, BackupType.Differential, Now.AddHours(-2), 1)],
            [
                Staging(11, 1, remote),
                Staging(12, 2, remote),
                Copy(22, 2, BackupFileLocation.Remote, remote),
            ]);

        var remoteResult = results.Single(result => result.Location == BackupFileLocation.Remote);
        var local = results.Single(result => result.Location == BackupFileLocation.Local);
        Assert.Equal(LocationRecoveryConclusion.NotRestorable, remoteResult.Conclusion);
        Assert.Equal(RecoveryProtectionReason.BaseUnavailableAtLocation, Reason(remoteResult, 22));
        Assert.Equal(RecoveryProtectionReason.BaselineUnresolved, Reason(local, 11));
        Assert.Equal(PlanLocationRecovery.RemoteBaseUnconfirmed, Reason(local, 12));
        Assert.All(local.Copies, copy => Assert.False(PlanLocationRecovery.IsStagingReleasable(copy)));
        Assert.All(results.SelectMany(result => result.Copies), copy => Assert.False(PlanLocationRecovery.IsDeletable(copy)));
    }

    [Fact]
    public void ConfirmedRemoteFullCanReleaseStagingWhenItIsNotADifferentialBaseline()
    {
        var remote = Guid.NewGuid();
        var local = EvaluateStaging(ActiveBaseline.None, false, [
            Set(1, BackupType.Full, Now.AddDays(-40)),
            Set(2, BackupType.Full, Now.AddDays(-1)),
        ], [
            Staging(11, 1, remote),
            Staging(12, 2, remote),
            Copy(21, 1, BackupFileLocation.Remote, remote),
            Copy(22, 2, BackupFileLocation.Remote, remote),
        ]);

        Assert.Equal(PlanLocationRecovery.RemoteStagingReleasable, Reason(local, 11));
        Assert.Equal(PlanLocationRecovery.RemoteStagingReleasable, Reason(local, 12));
        Assert.True(PlanLocationRecovery.IsStagingReleasable(Decision(local, 11)));
        Assert.False(PlanLocationRecovery.IsDeletable(Decision(local, 11)));
    }

    [Fact]
    public void ActiveBaselineAndUnconfirmedDependentKeepFullStaging()
    {
        var remote = Guid.NewGuid();
        var fullId = GuidFrom(1);
        var kept = EvaluateStaging(ActiveBaseline.Known(fullId), false, [
            Set(fullId, BackupType.Full, Now.AddDays(-2)),
            Set(GuidFrom(2), BackupType.Differential, Now.AddHours(-1), fullId),
        ], [
            Staging(11, fullId, remote),
            Staging(12, 2, remote),
            Copy(21, fullId, BackupFileLocation.Remote, remote),
            Copy(22, 2, BackupFileLocation.Remote, remote),
        ]);
        Assert.Equal(RecoveryProtectionReason.ActiveDifferentialBaseline, Reason(kept, 11));
        Assert.Equal(PlanLocationRecovery.RemoteStagingReleasable, Reason(kept, 12));

        var waiting = EvaluateStaging(ActiveBaseline.None, false, [
            Set(1, BackupType.Full, Now.AddDays(-2)),
            Set(2, BackupType.Differential, Now.AddHours(-1), 1, proven: false),
        ], [
            Staging(11, 1, remote),
            Staging(12, 2, remote),
            Copy(21, 1, BackupFileLocation.Remote, remote),
        ]);
        Assert.Equal(PlanLocationRecovery.DependencyUnproven, Reason(waiting, 11));
        Assert.Equal(PlanLocationRecovery.DependencyUnproven, Reason(waiting, 12));
        Assert.False(PlanLocationRecovery.IsStagingReleasable(Decision(waiting, 11)));
    }

    [Fact]
    public void OtherActivityAmbiguousRemoteAndLogStagingStayProtected()
    {
        var remote = Guid.NewGuid();
        var local = EvaluateStaging(ActiveBaseline.None, false, [
            Set(1, BackupType.Full, Now.AddDays(-1)),
            Set(2, BackupType.Log, Now.AddHours(-1)),
        ], [
            Staging(11, 1, remote, otherActivity: true),
            Staging(12, 2, remote),
            Copy(21, 1, BackupFileLocation.Remote, remote),
            Copy(31, 1, BackupFileLocation.Remote, remote),
        ]);

        Assert.Equal(RecoveryProtectionReason.ActiveTaskDependency, Reason(local, 11));
        Assert.Equal(RecoveryProtectionReason.LogRetentionDeferred, Reason(local, 12));
        Assert.False(PlanLocationRecovery.IsStagingReleasable(Decision(local, 11)));
    }

    [Fact]
    public void LogCopiesStayDeferred()
    {
        var result = Single(
            ActiveBaseline.None,
            [Set(1, BackupType.Full, Now.AddDays(-1)), Set(2, BackupType.Log, Now.AddDays(-40))],
            [Copy(11, 1), Copy(12, 2)]);

        Assert.Equal(RecoveryProtectionReason.LogRetentionDeferred, Reason(result, 12));
        Assert.Equal(RecoveryWindowVerdict.Protected, Decision(result, 12).Verdict);
        Assert.Equal(LocationRecoveryConclusion.RestorableWindowIncomplete, result.Conclusion);
    }

    [Fact]
    public void CancellingADeletionLeaseRestoresTheAvailableCopy()
    {
        var now = DateTimeOffset.UtcNow;
        var file = BackupFile.CreateLocal(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            FileTransferProtocol.Smb, @"\\synthetic-host\synthetic-share\old.bak", 4096, now.AddDays(-40), 7);
        var token = Guid.NewGuid();
        file.ClaimDeletion(token, "合成删除", now, now.AddMinutes(5));
        file.CancelDeletionLease(now.AddMinutes(1));

        Assert.Equal(BackupFileStatus.Available, file.Status);
        Assert.Null(file.DeletionLeaseToken);
        Assert.Equal(0, file.DeletionAttemptCount);
        file.ClaimDeletion(Guid.NewGuid(), "合成再次删除", now.AddMinutes(2), now.AddMinutes(10));
        Assert.Throws<InvalidOperationException>(() => file.RecordDeleted(token, now.AddMinutes(3)));
    }

    private static PlanLocationRecoveryResult EvaluateStaging(
        ActiveBaseline baseline,
        bool protectAllFulls,
        IReadOnlyList<PlanRecoverySetInput> sets,
        IReadOnlyList<PlanRecoveryCopyInput> copies)
    {
        var remote = copies.Select(copy => copy.StorageTargetId ?? copy.StagingRemoteTargetId).First(id => id is not null)!.Value;
        return PlanLocationRecovery.Evaluate(
            Now,
            baseline,
            protectAllFulls,
            false,
            [new(BackupFileLocation.Local, null, null), new(BackupFileLocation.Remote, remote, 7)],
            sets,
            copies).Single(result => result.Location == BackupFileLocation.Local);
    }

    private static PlanRecoveryCopyInput Staging(int fileId, int setId, Guid target, bool otherActivity = false) =>
        new(GuidFrom(fileId), GuidFrom(setId), BackupFileLocation.Local, null, BackupFileStatus.Available, true, true, target, otherActivity);

    private static PlanRecoveryCopyInput Staging(int fileId, Guid setId, Guid target) =>
        new(GuidFrom(fileId), setId, BackupFileLocation.Local, null, BackupFileStatus.Available, true, true, target, false);

    private static PlanLocationRecoveryResult Single(
        ActiveBaseline baseline,
        IReadOnlyList<PlanRecoverySetInput> sets,
        IReadOnlyList<PlanRecoveryCopyInput> copies) =>
        PlanLocationRecovery.Evaluate(Now, baseline, false, false, [new(BackupFileLocation.Local, null, 7)], sets, copies).Single();

    private static PlanRecoverySetInput Set(
        int id,
        BackupType type,
        DateTimeOffset? completedAt,
        int? differentialBaseId = null,
        bool proven = true) =>
        Set(GuidFrom(id), type, completedAt, differentialBaseId is null ? null : GuidFrom(differentialBaseId.Value), proven);

    private static PlanRecoverySetInput Set(
        Guid id,
        BackupType type,
        DateTimeOffset? completedAt,
        Guid? differentialBaseId = null,
        bool proven = true) =>
        new(id, type, completedAt, differentialBaseId, false, proven, false);

    private static PlanRecoveryCopyInput Copy(
        int fileId,
        int? setId,
        BackupFileLocation location = BackupFileLocation.Local,
        Guid? storageTargetId = null,
        bool remoteOnlyStaging = false,
        bool ownershipProven = true) =>
        new(GuidFrom(fileId), setId is null ? null : GuidFrom(setId.Value), location, storageTargetId, BackupFileStatus.Available, remoteOnlyStaging, ownershipProven);

    private static PlanRecoveryCopyInput Copy(
        int fileId,
        Guid setId,
        BackupFileLocation location = BackupFileLocation.Local,
        Guid? storageTargetId = null,
        bool remoteOnlyStaging = false,
        bool ownershipProven = true) =>
        new(GuidFrom(fileId), setId, location, storageTargetId, BackupFileStatus.Available, remoteOnlyStaging, ownershipProven);

    private static string Reason(PlanLocationRecoveryResult result, int fileId) =>
        Decision(result, GuidFrom(fileId)).Reason;

    private static PlanCopyRetentionDecision Decision(PlanLocationRecoveryResult result, int fileId) =>
        Decision(result, GuidFrom(fileId));

    private static PlanCopyRetentionDecision Decision(PlanLocationRecoveryResult result, Guid fileId) =>
        result.Copies.Single(copy => copy.FileId == fileId);

    private static Guid GuidFrom(int value) =>
        Guid.Parse($"00000000-0000-0000-0000-{value:000000000000}");
}
