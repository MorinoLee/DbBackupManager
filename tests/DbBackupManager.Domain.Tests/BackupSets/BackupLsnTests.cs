using DbBackupManager.Domain.BackupSets;

namespace DbBackupManager.Domain.Tests.BackupSets;

public sealed class BackupLsnTests
{
    public static TheoryData<decimal> ValidValues => new()
    {
        0m,
        1m,
        18446744073709551616m,
        BackupLsn.MaximumValue,
    };

    public static TheoryData<decimal> InvalidValues => new()
    {
        -1m,
        -0.1m,
        0.1m,
        18446744073709551616.5m,
        BackupLsn.MaximumValue + 1m,
        decimal.MaxValue,
        decimal.MinValue,
    };

    [Theory]
    [MemberData(nameof(ValidValues))]
    public void Numeric25IntegerIsPreservedWithoutNarrowing(decimal value)
    {
        var lsn = new BackupLsn(value);
        Assert.Equal(value, lsn.Value);
        Assert.Equal(lsn, new BackupLsn(value));
    }

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void FractionNegativeOrOutOfRangeIsRejected(decimal value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackupLsn(value));
    }

    [Fact]
    public void NeighboringLargeLsnsAreDistinctAndMissingIsNotZero()
    {
        var maximum = new BackupLsn(BackupLsn.MaximumValue);
        Assert.NotEqual(maximum, new BackupLsn(BackupLsn.MaximumValue - 1m));
        BackupLsn? missing = null;
        Assert.NotEqual(missing, new BackupLsn(0m));
    }
}
