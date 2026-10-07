using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupSets;

public sealed class BackupSetTests
{
    private static readonly BackupCompletionTimeDecision UnknownTime =
        new(null, BackupCompletionTimeSource.Unknown, BackupCompletionTimeReason.ServerTimeZoneUnknown);

    [Fact]
    public void ConflictingMetadataDoesNotLoseTheIndependentPlatformCompletionFact()
    {
        var guid = Guid.NewGuid();
        var initial = Full(guid);
        var set = new BackupSet(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            initial, UnknownTime, BackupSetAssessment.NotApplicable, null, false);
        var completed = new BackupCompletionTimeDecision(DateTimeOffset.UtcNow,
            BackupCompletionTimeSource.PlatformObserved, BackupCompletionTimeReason.PlatformObserved);
        Assert.True(set.Reconcile(initial with { BackupSetGuid = BackupMetadata.Known(Guid.NewGuid()) },
            completed, BackupSetAssessment.NotApplicable, null, true));
        Assert.Equal(guid, set.Metadata.BackupSetGuid.Value);
        Assert.Equal(completed, set.Completion);
        Assert.True(set.SqlSuccessObserved);
        Assert.True(set.HasMetadataConflict);
        Assert.Equal(DifferentialBaselineConclusion.Mismatch, set.Assessment.Conclusion);
        Assert.Equal(DifferentialBaselineReason.SourceConflict, set.Assessment.ReasonCode);
        set.Reconcile(initial, UnknownTime, BackupSetAssessment.NotApplicable, null, false);
        Assert.True(set.SqlSuccessObserved);
        Assert.True(set.HasMetadataConflict);
        Assert.Equal(completed, set.Completion);
    }

    [Fact]
    public void UnknownRefinesToKnownAndCannotEraseConfirmedFacts()
    {
        var initial = new BackupSetMetadata();
        var facts = Full(Guid.NewGuid());
        Assert.Equal(facts, initial.Merge(facts, out var initialConflict));
        Assert.False(initialConflict);
        Assert.Equal(facts, facts.Merge(initial, out var missingConflict));
        Assert.False(missingConflict);
        var conflicting = facts with { CheckpointLsn = BackupMetadata.Known(new BackupLsn(1234567890123456789012346m)) };
        Assert.Equal(facts, facts.Merge(conflicting, out var conflict));
        Assert.True(conflict);
    }

    [Fact]
    public void FullAndUnknownTypeCannotConfuseMissingAndNotApplicableBaselines()
    {
        Assert.Throws<ArgumentException>(() => new BackupSetMetadata
        { Type = BackupMetadata.Known(BackupType.Full) }.Validate());
        Assert.Throws<ArgumentException>(() => new BackupSetMetadata
        { DifferentialBaseLsn = BackupMetadata.NotApplicable<BackupLsn>() }.Validate());
        Assert.Throws<ArgumentException>(() => (Full(Guid.NewGuid()) with
        { DatabaseGuid = BackupMetadata.NotApplicable<Guid>() }).Validate());
    }

    [Fact]
    public void RawSqlTimesMustStayUnspecifiedAndValueStatePairsMustMatch()
    {
        Assert.Throws<ArgumentException>(() => (Full(Guid.NewGuid()) with
        { SqlFinishedLocal = BackupMetadata.Known(DateTime.UtcNow) }).Validate());
        Assert.Throws<ArgumentException>(() => (Full(Guid.NewGuid()) with
        { FirstLsn = new() { State = BackupMetadataState.Known } }).Validate());
        Assert.Throws<ArgumentException>(() => (Full(Guid.NewGuid()) with
        { BackupSetGuid = BackupMetadata.Known(Guid.Empty) }).Validate());
    }

    [Fact]
    public void CompletionSourcesAndBaselineReasonsAreStableCodes()
    {
        Assert.Throws<ArgumentException>(() => BackupSetCodes.ValidateCompletion(
            new(null, BackupCompletionTimeSource.PlatformObserved, BackupCompletionTimeReason.PlatformObserved)));
        Assert.Throws<ArgumentException>(() => BackupSetCodes.ValidateCompletion(
            new(DateTimeOffset.UtcNow, BackupCompletionTimeSource.Unknown, BackupCompletionTimeReason.SqlFinishMissing)));
        Assert.Throws<ArgumentException>(() => new BackupSetAssessment(BackupMetadataState.Known,
            DifferentialBaselineConclusion.Verified, "baseline.unrecognized").Validate());
    }

    private static BackupSetMetadata Full(Guid guid) => new()
    {
        Type = BackupMetadata.Known(BackupType.Full),
        BackupSetGuid = BackupMetadata.Known(guid),
        CheckpointLsn = BackupMetadata.Known(new BackupLsn(1234567890123456789012345m)),
        DifferentialBaseLsn = BackupMetadata.NotApplicable<BackupLsn>(),
        DifferentialBaseGuid = BackupMetadata.NotApplicable<Guid>()
    };
}
