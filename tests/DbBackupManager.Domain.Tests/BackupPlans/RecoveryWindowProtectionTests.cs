using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupPlans;

public sealed class RecoveryWindowProtectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SundayFullIsKeptBecauseWednesdayDifferentialDependsOnIt()
    {
        var sunday = new DateTimeOffset(2026, 9, 27, 2, 0, 0, TimeSpan.Zero);
        var wednesday = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
        var assessment = RecoveryWindowProtection.Evaluate(
            7,
            Now,
            [
                Full(1, sunday),
                Differential(2, wednesday, baseId: 1),
            ],
            ActiveBaseline.None);

        Assert.Equal(RecoveryProtectionReason.DifferentialBase, Reason(assessment, 1));
        Assert.Equal(RecoveryWindowVerdict.Protected, Decision(assessment, 2).Verdict);
        Assert.Equal(RecoveryProtectionReason.LatestRestorePoint, Reason(assessment, 2));
    }

    [Fact]
    public void DifferentialImmediatelyBeforeTheWindowIsTheAnchor()
    {
        var sunday = new DateTimeOffset(2026, 9, 27, 2, 0, 0, TimeSpan.Zero);
        var tuesday = new DateTimeOffset(2026, 9, 29, 2, 0, 0, TimeSpan.Zero);
        var wednesday = new DateTimeOffset(2026, 9, 30, 2, 0, 0, TimeSpan.Zero);
        var assessment = RecoveryWindowProtection.Evaluate(
            7,
            Now,
            [
                Full(1, sunday),
                Differential(2, tuesday, baseId: 1),
                Full(3, wednesday),
            ],
            ActiveBaseline.None);

        Assert.Equal(RecoveryProtectionReason.WindowStartAnchor, Reason(assessment, 2));
        Assert.Equal(RecoveryProtectionReason.DifferentialBase, Reason(assessment, 1));
        Assert.Equal(RecoveryWindowVerdict.Protected, Decision(assessment, 3).Verdict);
    }

    [Fact]
    public void PausedMonthKeepsTheLatestDifferentialAndItsBase()
    {
        var olderDifferential = new DateTimeOffset(2026, 7, 15, 2, 0, 0, TimeSpan.Zero);
        var full = new DateTimeOffset(2026, 8, 2, 2, 0, 0, TimeSpan.Zero);
        var latestDifferential = new DateTimeOffset(2026, 8, 3, 2, 0, 0, TimeSpan.Zero);
        var assessment = RecoveryWindowProtection.Evaluate(
            7,
            Now,
            [
                Full(1, full),
                Differential(2, olderDifferential, baseId: 1),
                Differential(3, latestDifferential, baseId: 1),
            ],
            ActiveBaseline.None);

        Assert.Equal(RecoveryProtectionReason.LatestRestorePoint, Reason(assessment, 3));
        Assert.Equal(RecoveryProtectionReason.DifferentialBase, Reason(assessment, 1));
        Assert.Equal(RecoveryWindowVerdict.Deletable, Decision(assessment, 2).Verdict);
    }

    [Fact]
    public void LeftoverDifferentialExpiresWithoutAPlanMode()
    {
        var oldBase = new DateTimeOffset(2026, 7, 31, 2, 0, 0, TimeSpan.Zero);
        var leftover = new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero);
        var anchor = new DateTimeOffset(2026, 9, 29, 2, 0, 0, TimeSpan.Zero);
        var current = new DateTimeOffset(2026, 10, 6, 2, 0, 0, TimeSpan.Zero);
        var assessment = RecoveryWindowProtection.Evaluate(
            7,
            Now,
            [
                Full(1, oldBase),
                Differential(2, leftover, baseId: 1),
                Full(3, anchor),
                Full(4, current),
            ],
            ActiveBaseline.None);

        Assert.Equal(RecoveryWindowVerdict.Deletable, Decision(assessment, 2).Verdict);
        Assert.Equal(RecoveryProtectionReason.OutsideWindowUnneeded, Reason(assessment, 2));
    }

    [Fact]
    public void TiedCompletionTimesAreAllProtected()
    {
        var tied = new DateTimeOffset(2026, 10, 6, 2, 0, 0, TimeSpan.Zero);
        var older = new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero);
        var anchor = new DateTimeOffset(2026, 9, 29, 2, 0, 0, TimeSpan.Zero);
        var assessment = RecoveryWindowProtection.Evaluate(
            7,
            Now,
            [
                Full(1, older),
                Full(2, anchor),
                Full(3, tied),
                Full(4, tied, copyOnly: true),
            ],
            ActiveBaseline.None);

        Assert.Equal(RecoveryProtectionReason.LatestAvailableFull, Reason(assessment, 3));
        Assert.Equal(RecoveryProtectionReason.LatestAvailableFull, Reason(assessment, 4));
        Assert.Equal(RecoveryWindowVerdict.Deletable, Decision(assessment, 1).Verdict);
    }

    [Fact]
    public void RemoteLocationWithOnlyADifferentialIsNotRestorable()
    {
        var assessment = RecoveryWindowProtection.Evaluate(
            7,
            Now,
            [Differential(1, Now.AddDays(-1), baseId: 99)],
            ActiveBaseline.None);

        Assert.Equal(RecoveryProtectionReason.BaseUnavailableAtLocation, Reason(assessment, 1));
        Assert.Equal(LocationRecoveryConclusion.NotRestorable, assessment.Conclusion);
    }

    [Fact]
    public void NonUtcNowIsRejected()
    {
        var local = new DateTimeOffset(2026, 10, 7, 8, 0, 0, TimeSpan.FromHours(8));

        Assert.Throws<ArgumentException>(() => RecoveryWindowProtection.Evaluate(
            7,
            local,
            [Full(1, Now.AddDays(-1))],
            ActiveBaseline.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(36501)]
    public void UnknownWindowProtectsEveryBackupSet(int? windowDays)
    {
        var assessment = RecoveryWindowProtection.Evaluate(
            windowDays,
            Now,
            [Full(1, Now.AddDays(-30)), Differential(2, Now.AddDays(-29), baseId: 1)],
            ActiveBaseline.None);

        Assert.All(assessment.Decisions, decision =>
        {
            Assert.Equal(RecoveryWindowVerdict.Protected, decision.Verdict);
            Assert.Equal(RecoveryProtectionReason.WindowUnknown, decision.Reason);
        });
        Assert.Equal(LocationRecoveryConclusion.NotRestorable, assessment.Conclusion);
    }

    [Fact]
    public void CompletionExactlyAtTheWindowStartIsInsideTheWindow()
    {
        var start = Now - TimeSpan.FromDays(7);
        var assessment = RecoveryWindowProtection.Evaluate(
            7,
            Now,
            [
                Full(1, start.AddHours(-1)),
                Full(2, start),
                Full(3, start.AddDays(1)),
            ],
            ActiveBaseline.None);

        Assert.Equal(RecoveryProtectionReason.WindowStartAnchor, Reason(assessment, 1));
        Assert.Equal(RecoveryProtectionReason.InsideWindow, Reason(assessment, 2));
        Assert.NotEqual(RecoveryProtectionReason.WindowStartAnchor, Reason(assessment, 2));
    }

    [Fact]
    public void ActiveBaselineIsKeptWhenNothingReferencesIt()
    {
        var baseline = new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 10, 6, 2, 0, 0, TimeSpan.Zero);
        var assessment = RecoveryWindowProtection.Evaluate(
            7,
            Now,
            [Full(1, baseline), Full(2, newer)],
            ActiveBaseline.Known(Id(1)));

        Assert.Equal(RecoveryProtectionReason.ActiveDifferentialBaseline, Reason(assessment, 1));
    }

    [Fact]
    public void UncertainDifferentialKeepsItsBaseFull()
    {
        var assessment = RecoveryWindowProtection.Evaluate(
            7,
            Now,
            [
                Full(1, Now.AddDays(-20)),
                Full(2, Now.AddDays(-9), copyOnly: true),
                Set(3, BackupType.Differential, Now.AddDays(-1), baseId: 1, certainty: BackupCertainty.Uncertain),
            ],
            ActiveBaseline.None);

        Assert.Equal(RecoveryProtectionReason.Uncertain, Reason(assessment, 3));
        Assert.Equal(RecoveryProtectionReason.DifferentialBase, Reason(assessment, 1));
        Assert.Equal(RecoveryWindowVerdict.Protected, Decision(assessment, 1).Verdict);
    }

    [Fact]
    public void UnresolvedBaselineStillKeepsTheAnchorDifferential()
    {
        var assessment = RecoveryWindowProtection.Evaluate(
            7,
            Now,
            [
                Full(1, Now.AddDays(-10)),
                Differential(2, Now.AddDays(-8), baseId: 1),
                Full(3, Now.AddDays(-1)),
            ],
            ActiveBaseline.Unresolved);

        Assert.Equal(RecoveryProtectionReason.BaselineUnresolved, Reason(assessment, 1));
        Assert.Equal(RecoveryProtectionReason.BaselineUnresolved, Reason(assessment, 3));
        Assert.Equal(RecoveryWindowVerdict.Protected, Decision(assessment, 2).Verdict);
        Assert.Equal(LocationRecoveryConclusion.RestorableWindowIncomplete, assessment.Conclusion);
    }

    [Fact]
    public void LogBackupSetsStayProtected()
    {
        var assessment = Evaluate(Set(1, BackupType.Log, Now.AddDays(-30)));

        Assert.Equal(RecoveryProtectionReason.LogRetentionDeferred, Reason(assessment, 1));
    }

    [Fact]
    public void UncertainBackupSetsStayProtected()
    {
        var assessment = Evaluate(Set(1, BackupType.Full, Now.AddDays(-30), certainty: BackupCertainty.Uncertain));

        Assert.Equal(RecoveryProtectionReason.Uncertain, Reason(assessment, 1));
    }

    [Fact]
    public void ActiveTaskReferencesStayProtected()
    {
        var assessment = Evaluate(Set(1, BackupType.Full, Now.AddDays(-30), activeTask: true));

        Assert.Equal(RecoveryProtectionReason.ActiveTaskDependency, Reason(assessment, 1));
    }

    [Fact]
    public void UnknownCompletionTimeStaysProtected()
    {
        var assessment = Evaluate(Set(1, BackupType.Full, completedAt: null));

        Assert.Equal(RecoveryProtectionReason.CompletionUnknown, Reason(assessment, 1));
    }

    [Theory]
    [InlineData(BackupCopyPresence.Missing)]
    [InlineData(BackupCopyPresence.Unknown)]
    public void UnavailableCopiesStayProtected(BackupCopyPresence presence)
    {
        var assessment = Evaluate(Set(1, BackupType.Full, Now.AddDays(-1), presence: presence));

        Assert.Equal(RecoveryProtectionReason.PresenceNotDeletable, Reason(assessment, 1));
    }

    [Fact]
    public void MissingDifferentialDoesNotKeepItsBase()
    {
        var assessment = RecoveryWindowProtection.Evaluate(
            7,
            Now,
            [
                Full(1, Now.AddDays(-20)),
                Full(2, Now.AddDays(-8)),
                Full(3, Now.AddDays(-1)),
                Set(4, BackupType.Differential, Now.AddDays(-19), baseId: 1, presence: BackupCopyPresence.Missing),
            ],
            ActiveBaseline.None);

        Assert.Equal(RecoveryWindowVerdict.Deletable, Decision(assessment, 1).Verdict);
        Assert.Equal(RecoveryProtectionReason.PresenceNotDeletable, Reason(assessment, 4));
    }

    [Fact]
    public void UnknownDifferentialKeepsItsBase()
    {
        var assessment = RecoveryWindowProtection.Evaluate(
            7,
            Now,
            [
                Full(1, Now.AddDays(-20)),
                Full(2, Now.AddDays(-8)),
                Full(3, Now.AddDays(-1)),
                Set(4, BackupType.Differential, Now.AddDays(-19), baseId: 1, presence: BackupCopyPresence.Unknown),
            ],
            ActiveBaseline.None);

        Assert.Equal(RecoveryProtectionReason.DifferentialBase, Reason(assessment, 1));
        Assert.Equal(RecoveryProtectionReason.PresenceNotDeletable, Reason(assessment, 4));
    }

    [Fact]
    public void EarlierGateWinsWhenSeveralApply()
    {
        var assessment = RecoveryWindowProtection.Evaluate(
            7,
            Now,
            [
                Set(1, BackupType.Log, Now.AddDays(-1), certainty: BackupCertainty.Uncertain),
                Set(2, BackupType.Full, Now.AddDays(-1), certainty: BackupCertainty.Uncertain, activeTask: true),
                Set(3, BackupType.Full, completedAt: null, activeTask: true),
                Set(4, BackupType.Full, completedAt: null, presence: BackupCopyPresence.Missing),
                Set(5, BackupType.Full, Now.AddDays(-1), presence: BackupCopyPresence.Missing),
            ],
            ActiveBaseline.Unresolved);

        Assert.Equal(RecoveryProtectionReason.LogRetentionDeferred, Reason(assessment, 1));
        Assert.Equal(RecoveryProtectionReason.Uncertain, Reason(assessment, 2));
        Assert.Equal(RecoveryProtectionReason.ActiveTaskDependency, Reason(assessment, 3));
        Assert.Equal(RecoveryProtectionReason.CompletionUnknown, Reason(assessment, 4));
        Assert.Equal(RecoveryProtectionReason.PresenceNotDeletable, Reason(assessment, 5));
    }

    [Fact]
    public void DifferentialThatReferencesCopyOnlyFullIsNotARestorePoint()
    {
        var completedAt = Now.AddDays(-1);
        var assessment = RecoveryWindowProtection.Evaluate(
            7,
            Now,
            [
                Full(1, completedAt, copyOnly: true),
                Differential(2, completedAt, baseId: 1),
            ],
            ActiveBaseline.None);

        Assert.Equal(RecoveryProtectionReason.DifferentialBase, Reason(assessment, 1));
        Assert.Equal(RecoveryProtectionReason.BaseUnavailableAtLocation, Reason(assessment, 2));
        Assert.Equal(LocationRecoveryConclusion.NotRestorable, assessment.Conclusion);
    }

    private static RecoveryWindowAssessment Evaluate(RecoveryBackupSet backupSet)
    {
        return RecoveryWindowProtection.Evaluate(7, Now, [backupSet], ActiveBaseline.None);
    }

    private static RecoveryBackupSet Set(
        int number,
        BackupType type,
        DateTimeOffset? completedAt,
        int? baseId = null,
        bool copyOnly = false,
        BackupCopyPresence presence = BackupCopyPresence.Available,
        BackupCertainty certainty = BackupCertainty.Determined,
        bool activeTask = false)
    {
        return new RecoveryBackupSet(
            Id(number),
            type,
            completedAt,
            baseId is null ? null : Id(baseId.Value),
            copyOnly,
            presence,
            certainty,
            activeTask);
    }

    private static RecoveryBackupSet Full(int number, DateTimeOffset completedAt, bool copyOnly = false)
    {
        return new RecoveryBackupSet(
            Id(number),
            BackupType.Full,
            completedAt,
            DifferentialBaseId: null,
            IsCopyOnly: copyOnly,
            Presence: BackupCopyPresence.Available,
            Certainty: BackupCertainty.Determined,
            ReferencedByActiveTask: false);
    }

    private static RecoveryBackupSet Differential(int number, DateTimeOffset completedAt, int baseId)
    {
        return new RecoveryBackupSet(
            Id(number),
            BackupType.Differential,
            completedAt,
            DifferentialBaseId: Id(baseId),
            IsCopyOnly: false,
            Presence: BackupCopyPresence.Available,
            Certainty: BackupCertainty.Determined,
            ReferencedByActiveTask: false);
    }

    private static Guid Id(int number)
    {
        return Guid.Parse($"00000000-0000-0000-0000-{number:000000000000}");
    }

    private static RecoveryProtectionDecision Decision(RecoveryWindowAssessment assessment, int number)
    {
        return Assert.Single(assessment.Decisions, decision => decision.BackupSetId == Id(number));
    }

    private static string Reason(RecoveryWindowAssessment assessment, int number)
    {
        return Decision(assessment, number).Reason;
    }
}
