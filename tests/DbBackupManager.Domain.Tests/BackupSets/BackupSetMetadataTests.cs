using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupSets;

public sealed class BackupSetMetadataTests
{
    [Fact]
    public void B01LogFieldsDefaultUnknownAndKnownFalseIsNotMissing()
    {
        var missing = new BackupSetMetadata();
        Assert.Equal(BackupMetadataState.Unknown, missing.RecoveryModel.State);
        Assert.Equal(BackupMetadataState.Unknown, missing.BeginsLogChain.State);
        Assert.Equal(BackupMetadataState.Unknown, missing.HasBulkLoggedData.State);
        Assert.Equal(BackupMetadataState.Unknown, missing.IsForceOffline.State);

        var known = new BackupSetMetadata
        {
            RecoveryModel = BackupMetadata.Known(DatabaseRecoveryModel.Simple),
            BeginsLogChain = BackupMetadata.Known(false),
            HasBulkLoggedData = BackupMetadata.Known(false),
            IsForceOffline = BackupMetadata.Known(false),
        };
        known.Validate();
        Assert.False(known.BeginsLogChain.Value);
        Assert.Equal(BackupMetadataState.Known, known.BeginsLogChain.State);
    }

    [Fact]
    public void B01LogFieldsRejectNotApplicableAndOutOfRangeEnum()
    {
        var notApplicable = new BackupSetMetadata
        {
            BeginsLogChain = BackupMetadata.NotApplicable<bool>(),
        };
        Assert.Throws<ArgumentException>(() => notApplicable.Validate());

        var invalidRecovery = new BackupSetMetadata
        {
            RecoveryModel = BackupMetadata.Known((DatabaseRecoveryModel)99),
        };
        Assert.Throws<ArgumentException>(() => invalidRecovery.Validate());
    }

    [Fact]
    public void B13MergeCompletesMissingFieldsButRejectsKnownConflicts()
    {
        var left = new BackupSetMetadata();
        var right = new BackupSetMetadata
        {
            RecoveryModel = BackupMetadata.Known(DatabaseRecoveryModel.Full),
            BeginsLogChain = BackupMetadata.Known(false),
            HasBulkLoggedData = BackupMetadata.Known(false),
            IsForceOffline = BackupMetadata.Known(false),
        };

        var merged = left.Merge(right, out var conflict);

        Assert.False(conflict);
        Assert.Equal(DatabaseRecoveryModel.Full, merged.RecoveryModel.Value);
        Assert.False(merged.BeginsLogChain.Value);

        var conflicting = right with { BeginsLogChain = BackupMetadata.Known(true) };
        var retained = right.Merge(conflicting, out var hasConflict);
        Assert.True(hasConflict);
        Assert.Equal(right, retained);
    }

    [Fact]
    public void B11OldMetadataWithoutLogFieldsStillValidatesAndCompares()
    {
        var old = new BackupSetMetadata
        {
            BackupSetGuid = BackupMetadata.Known(Guid.NewGuid()),
            DatabaseGuid = BackupMetadata.Known(Guid.NewGuid()),
            FamilyGuid = BackupMetadata.Known(Guid.NewGuid()),
            Type = BackupMetadata.Known(BackupType.Full),
            IsCopyOnly = BackupMetadata.Known(false),
            FirstRecoveryForkId = BackupMetadata.Known(Guid.NewGuid()),
            RecoveryForkId = BackupMetadata.Known(Guid.NewGuid()),
            DifferentialBaseGuid = BackupMetadata.NotApplicable<Guid>(),
            DifferentialBaseLsn = BackupMetadata.NotApplicable<BackupLsn>(),
        };

        old.Validate();
        Assert.Equal(old, old with { });
        Assert.All(
            new[]
            {
                old.RecoveryModel.State,
                old.BeginsLogChain.State,
                old.HasBulkLoggedData.State,
                old.IsForceOffline.State,
            },
            state => Assert.Equal(BackupMetadataState.Unknown, state));
    }
}
