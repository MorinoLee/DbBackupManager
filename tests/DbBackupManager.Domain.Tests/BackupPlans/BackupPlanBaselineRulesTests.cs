using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;

namespace DbBackupManager.Domain.Tests.BackupPlans;

public sealed class BackupPlanBaselineRulesTests
{
    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    public void EnablementIsOnlyTheFirstUnpausedDifferentialVersion(
        bool paused, bool currentIncludes, bool previousIncludes, bool enablement)
    {
        Assert.Equal(enablement, BackupPlanBaselineRules.IsEnablement(paused, currentIncludes, previousIncludes));
    }

    [Fact]
    public void FirstDifferentialWithoutBaselineQueuesOneOrdinaryFull()
    {
        Assert.Equal(
            BackupPlanBaselineEnablementDecision.Queued,
            BackupPlanBaselineRules.Decide(false, true, false, false, null, false));
    }

    [Fact]
    public void VerifiedManagedBaselineOnlyExpandsTiers()
    {
        Assert.Equal(
            BackupPlanBaselineEnablementDecision.SkippedManagedBaseline,
            BackupPlanBaselineRules.Decide(false, true, false, true, DifferentialBaselineConclusion.Verified, false));
    }

    [Theory]
    [InlineData(DifferentialBaselineConclusion.Unknown)]
    [InlineData(DifferentialBaselineConclusion.Unmanaged)]
    [InlineData(DifferentialBaselineConclusion.Mismatch)]
    public void UncertainBaselineDoesNotQueueOrDowngrade(DifferentialBaselineConclusion conclusion)
    {
        Assert.Equal(
            BackupPlanBaselineEnablementDecision.SkippedUncertainBaseline,
            BackupPlanBaselineRules.Decide(false, true, false, false, conclusion, false));
        Assert.False(BackupPlanBaselineRules.AllowsDifferentialExecution(false, conclusion));
        Assert.False(BackupPlanBaselineRules.AllowsDifferentialExecution(true, conclusion));
    }

    [Fact]
    public void OutstandingOrdinaryFullIsNotSupplementedAgain()
    {
        Assert.Equal(
            BackupPlanBaselineEnablementDecision.SkippedOutstandingFull,
            BackupPlanBaselineRules.Decide(false, true, false, false, null, true));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public void FullOnlyPausedAndExpandedDifferentialVersionsAreNotEvents(
        bool paused, bool currentIncludes, bool previousIncludes)
    {
        Assert.Null(BackupPlanBaselineRules.Decide(paused, currentIncludes, previousIncludes, false, null, false));
    }

    [Fact]
    public void ResumingAPausedDifferentialPlanIsAnEnablementEvenWhenThePreviousVersionAlreadyIncludesIt()
    {
        Assert.True(BackupPlanBaselineRules.IsEnablement(false, true, true, resumedFromPause: true));
        Assert.Equal(
            BackupPlanBaselineEnablementDecision.Queued,
            BackupPlanBaselineRules.Decide(false, true, true, false, null, false, resumedFromPause: true));
        Assert.Null(BackupPlanBaselineRules.Decide(false, true, true, false, null, false));
        Assert.False(BackupPlanBaselineRules.IsEnablement(true, true, false, resumedFromPause: true));
    }

    [Fact]
    public void DifferentialRunsOnlyAfterAVerifiedNonUncertainBaseline()
    {
        Assert.True(BackupPlanBaselineRules.AllowsDifferentialExecution(true, DifferentialBaselineConclusion.Verified));
        Assert.True(BackupPlanBaselineRules.AllowsDifferentialExecution(true, null));
        Assert.False(BackupPlanBaselineRules.AllowsDifferentialExecution(false, null));
        Assert.False(BackupPlanBaselineRules.AllowsDifferentialExecution(false, DifferentialBaselineConclusion.Verified));
    }
}

public sealed class BackupPlanBaselineEnablementTests
{
    [Fact]
    public void QueuedEnablementRequiresTheTaskAndSkippedEnablementForbidsIt()
    {
        var queued = new BackupPlanBaselineEnablement(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(),
            BackupPlanBaselineEnablementDecision.Queued);
        Assert.NotNull(queued.TaskId);
        Assert.Equal(1, queued.EnablementSequence);
        var skipped = new BackupPlanBaselineEnablement(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, Guid.NewGuid(), null,
            BackupPlanBaselineEnablementDecision.SkippedUncertainBaseline);
        Assert.Null(skipped.TaskId);
        Assert.Throws<ArgumentException>(() => new BackupPlanBaselineEnablement(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), null,
            BackupPlanBaselineEnablementDecision.Queued));
        Assert.Throws<ArgumentException>(() => new BackupPlanBaselineEnablement(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(),
            BackupPlanBaselineEnablementDecision.SkippedManagedBaseline));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackupPlanBaselineEnablement(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, Guid.NewGuid(), null,
            BackupPlanBaselineEnablementDecision.SkippedManagedBaseline));
    }
}
