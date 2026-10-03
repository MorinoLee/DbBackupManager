using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupTasks;

public sealed class BackupFileRetentionPolicyTests
{
    private static readonly DateTimeOffset VerifiedAt = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewestAvailableIsNeverACandidateEvenWhenExpired()
    {
        var older = Local("a", VerifiedAt, 7);
        var newest = Local("b", VerifiedAt.AddDays(1), 7);
        var now = VerifiedAt.AddDays(10);

        Assert.True(older.IsRetentionDue(now));
        Assert.True(newest.IsRetentionDue(now));
        Assert.Equal(older.Id, BackupFileRetentionPolicy.SelectDeletionCandidate([older, newest], now)!.Id);
        Assert.Null(BackupFileRetentionPolicy.SelectDeletionCandidate([newest], now));
    }

    [Fact]
    public void ExpiryBoundaryUsesValidatedAtPlusRetentionDays()
    {
        var older = Local("a", VerifiedAt, 7);
        var newest = Local("b", VerifiedAt.AddHours(1), 7);
        var dueAt = VerifiedAt.AddDays(7);

        Assert.False(older.IsRetentionDue(dueAt.AddTicks(-1)));
        Assert.True(older.IsRetentionDue(dueAt));
        Assert.Null(BackupFileRetentionPolicy.SelectDeletionCandidate([older, newest], dueAt.AddTicks(-1)));
        Assert.Equal(older.Id, BackupFileRetentionPolicy.SelectDeletionCandidate([older, newest], dueAt)!.Id);
    }

    [Fact]
    public void TiedValidationTimeUsesSmallerFileIdAsOlderCandidate()
    {
        var first = Local("a", VerifiedAt, 7);
        var second = Local("b", VerifiedAt, 7);
        var older = first.Id.CompareTo(second.Id) < 0 ? first : second;
        var newer = older.Id == first.Id ? second : first;

        Assert.Equal(
            older.Id,
            BackupFileRetentionPolicy.SelectDeletionCandidate([first, second], VerifiedAt.AddDays(8))!.Id);
        Assert.True(BackupFileRetentionPolicy.HasProtectedAvailableCopy([first, second], older.Id));
        Assert.False(BackupFileRetentionPolicy.HasProtectedAvailableCopy([older], older.Id));
        Assert.Equal(newer.RetentionGroup, older.RetentionGroup);
    }

    [Fact]
    public void RemoteGroupsAreSeparatedByStorageTarget()
    {
        var leftTarget = Guid.NewGuid();
        var rightTarget = Guid.NewGuid();
        var left = Remote("a", leftTarget, VerifiedAt, 30);
        var right = Remote("b", rightTarget, VerifiedAt.AddDays(-10), 30);

        Assert.NotEqual(left.RetentionGroup, right.RetentionGroup);
        Assert.Throws<ArgumentException>(() =>
            BackupFileRetentionPolicy.SelectDeletionCandidate([left, right], VerifiedAt.AddDays(40)));
        Assert.Null(BackupFileRetentionPolicy.SelectDeletionCandidate([left], VerifiedAt.AddDays(40)));
    }

    [Fact]
    public void DeletePendingAndMissingDoNotCountAsProtectedCopies()
    {
        var available = Local("keep", VerifiedAt.AddDays(2), 7);
        var expired = Local("old", VerifiedAt, 7);
        var pending = Local("busy", VerifiedAt.AddDays(-1), 7);
        pending.ClaimDeletion(
            Guid.NewGuid(),
            "worker-a",
            VerifiedAt.AddDays(8),
            VerifiedAt.AddDays(8).AddMinutes(5));
        var missing = Local("gone", VerifiedAt.AddDays(-2), 7);
        missing.RecordExternalMissing(VerifiedAt.AddDays(8));

        Assert.Equal(
            expired.Id,
            BackupFileRetentionPolicy.SelectDeletionCandidate(
                [available, expired, pending, missing],
                VerifiedAt.AddDays(8))!.Id);
        Assert.False(BackupFileRetentionPolicy.HasProtectedAvailableCopy([pending, missing, expired], expired.Id));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(36500)]
    public void RetentionDayBoundariesKeepNewestEvenWhenDue(int days)
    {
        var older = Local("old", VerifiedAt, days);
        var newest = Local("new", VerifiedAt.AddHours(1), days);
        var dueAt = VerifiedAt.AddDays(days);

        Assert.True(older.IsRetentionDue(dueAt));
        Assert.True(newest.IsRetentionDue(dueAt.AddHours(1)));
        Assert.Equal(older.Id, BackupFileRetentionPolicy.SelectDeletionCandidate([older, newest], dueAt)!.Id);
        Assert.Null(BackupFileRetentionPolicy.SelectDeletionCandidate([newest], dueAt.AddHours(1)));
    }

    [Fact]
    public void ThreeAvailableCopiesSelectOldestDueAndNeverNewest()
    {
        var oldest = Local("oldest", VerifiedAt, 7);
        var middle = Local("middle", VerifiedAt.AddDays(1), 7);
        var newest = Local("newest", VerifiedAt.AddDays(2), 7);
        var now = VerifiedAt.AddDays(10);

        Assert.Equal(
            oldest.Id,
            BackupFileRetentionPolicy.SelectDeletionCandidate([newest, middle, oldest], now)!.Id);
        Assert.True(BackupFileRetentionPolicy.HasProtectedAvailableCopy(
            [oldest, middle, newest],
            oldest.Id));
        Assert.NotEqual(
            newest.Id,
            BackupFileRetentionPolicy.SelectDeletionCandidate([newest, middle, oldest], now)!.Id);
    }

    [Fact]
    public void DeletionBackoffCapsAtOneHour()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), BackupFileRetentionPolicy.DeletionBackoff(1));
        Assert.Equal(TimeSpan.FromMinutes(5), BackupFileRetentionPolicy.DeletionBackoff(2));
        Assert.Equal(TimeSpan.FromMinutes(15), BackupFileRetentionPolicy.DeletionBackoff(3));
        Assert.Equal(TimeSpan.FromHours(1), BackupFileRetentionPolicy.DeletionBackoff(4));
        Assert.Equal(TimeSpan.FromHours(1), BackupFileRetentionPolicy.DeletionBackoff(20));
        Assert.Throws<ArgumentOutOfRangeException>(() => BackupFileRetentionPolicy.DeletionBackoff(0));
    }

    [Fact]
    public void LocalGroupRejectsStorageTargetAndRemoteGroupRequiresIt()
    {
        var databaseId = Guid.NewGuid();
        _ = new BackupFileRetentionGroup(databaseId, BackupFileLocation.Local, null);
        Assert.Throws<ArgumentException>(() =>
            new BackupFileRetentionGroup(databaseId, BackupFileLocation.Local, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() =>
            new BackupFileRetentionGroup(databaseId, BackupFileLocation.Remote, null));
    }

    private static BackupFile Local(string name, DateTimeOffset validatedAt, int days) =>
        BackupFile.CreateLocal(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            ParseDatabaseId(),
            Guid.NewGuid(),
            FileTransferProtocol.Smb,
            $@"\\synthetic-host\share\{name}.bak",
            4096,
            validatedAt,
            days);

    private static BackupFile Remote(
        string name,
        Guid storageTargetId,
        DateTimeOffset validatedAt,
        int days) =>
        BackupFile.CreateRemote(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            ParseDatabaseId(),
            storageTargetId,
            FileTransferProtocol.Sftp,
            $"/synthetic/{name}.bak",
            8192,
            validatedAt,
            days);

    private static Guid ParseDatabaseId() => Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
}
