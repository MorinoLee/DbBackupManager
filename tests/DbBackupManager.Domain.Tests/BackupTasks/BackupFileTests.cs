using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupTasks;

public sealed class BackupFileTests
{
    private static readonly DateTimeOffset VerifiedAt = new(
        2026,
        9,
        14,
        8,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void LocalFileFreezesIdentityAndNormalizesSmbPath()
    {
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var databaseId = Guid.NewGuid();
        var serverId = Guid.NewGuid();

        var file = BackupFile.CreateLocal(
            Guid.NewGuid(),
            taskId,
            attemptId,
            databaseId,
            serverId,
            FileTransferProtocol.Smb,
            @"//synthetic-host/share/folder/backup.bak",
            4096,
            VerifiedAt,
            7);

        Assert.Equal(taskId, file.TaskId);
        Assert.Equal(attemptId, file.AttemptId);
        Assert.Equal(databaseId, file.DatabaseId);
        Assert.Equal(serverId, file.DatabaseServerId);
        Assert.Null(file.StorageTargetId);
        Assert.Equal(BackupFileLocation.Local, file.Location);
        Assert.Equal(BackupFileStatus.Available, file.Status);
        Assert.Equal(@"\\SYNTHETIC-HOST\SHARE\FOLDER\BACKUP.BAK", file.Path);
        Assert.Equal(0, file.DeletionAttemptCount);
    }

    [Fact]
    public void RemoteFileRequiresRemoteIdentityAndAbsoluteSafePath()
    {
        var targetId = Guid.NewGuid();
        var file = BackupFile.CreateRemote(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            targetId,
            FileTransferProtocol.Sftp,
            @"/synthetic//folder/backup.bak",
            8192,
            VerifiedAt,
            30);

        Assert.Equal(targetId, file.StorageTargetId);
        Assert.Null(file.DatabaseServerId);
        Assert.Equal(BackupFileLocation.Remote, file.Location);
        Assert.Equal("/synthetic/folder/backup.bak", file.Path);
        Assert.Throws<ArgumentException>(() => BackupFile.CreateRemote(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            targetId,
            FileTransferProtocol.Sftp,
            "/synthetic/../outside.bak",
            8192,
            VerifiedAt,
            30));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(36501)]
    public void CreateLocalRejectsRetentionDaysOutsideAllowedRange(int days)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BackupFile.CreateLocal(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            FileTransferProtocol.Smb,
            @"\\synthetic-host\share\backup.bak",
            4096,
            VerifiedAt,
            days));
    }

    [Fact]
    public void DeletionLeaseFailureBackoffAndSuccessMoveOnlyForward()
    {
        var file = CreateLocal();
        var firstToken = Guid.NewGuid();
        file.ClaimDeletion(firstToken, "worker-a", VerifiedAt.AddDays(8), VerifiedAt.AddDays(8).AddMinutes(5));

        Assert.Equal(BackupFileStatus.DeletePending, file.Status);
        Assert.Equal(1, file.DeletionAttemptCount);
        Assert.Throws<InvalidOperationException>(() => file.RecordDeleted(
            Guid.NewGuid(),
            VerifiedAt.AddDays(8).AddMinutes(1)));

        var failedAt = VerifiedAt.AddDays(8).AddMinutes(1);
        var nextAttempt = failedAt.AddMinutes(5);
        file.RecordDeletionFailure(firstToken, failedAt, "file.delete_access_denied", nextAttempt);

        Assert.Equal(BackupFileStatus.DeleteFailed, file.Status);
        Assert.Equal("file.delete_access_denied", file.DeletionErrorCode);
        Assert.Equal(nextAttempt, file.NextDeletionAttemptAtUtc);
        Assert.Null(file.DeletionLeaseToken);
        Assert.Throws<InvalidOperationException>(() => file.ClaimDeletion(
            Guid.NewGuid(),
            "worker-b",
            nextAttempt.AddTicks(-1),
            nextAttempt.AddMinutes(5)));

        var secondToken = Guid.NewGuid();
        file.ClaimDeletion(secondToken, "worker-b", nextAttempt, nextAttempt.AddMinutes(5));
        file.RecordDeleted(secondToken, nextAttempt.AddMinutes(1));

        Assert.Equal(BackupFileStatus.Deleted, file.Status);
        Assert.Equal(2, file.DeletionAttemptCount);
        Assert.Null(file.DeletionErrorCode);
        Assert.Null(file.DeletionLeaseToken);
        Assert.Throws<InvalidOperationException>(() => file.ClaimDeletion(
            Guid.NewGuid(),
            "worker-c",
            nextAttempt.AddMinutes(2),
            nextAttempt.AddMinutes(7)));
    }

    [Fact]
    public void ExpiredDeletionLeaseCanBeTakenOverButActiveLeaseCannot()
    {
        var file = CreateLocal();
        file.ClaimDeletion(
            Guid.NewGuid(),
            "worker-a",
            VerifiedAt.AddDays(8),
            VerifiedAt.AddDays(8).AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() => file.TakeOverExpiredDeletionLease(
            Guid.NewGuid(),
            "worker-b",
            VerifiedAt.AddDays(8).AddSeconds(30),
            VerifiedAt.AddDays(8).AddMinutes(2)));

        var replacement = Guid.NewGuid();
        file.TakeOverExpiredDeletionLease(
            replacement,
            "worker-b",
            VerifiedAt.AddDays(8).AddMinutes(1),
            VerifiedAt.AddDays(8).AddMinutes(3));
        file.RecordMissing(replacement, VerifiedAt.AddDays(8).AddMinutes(2));

        Assert.Equal(BackupFileStatus.Missing, file.Status);
        Assert.Equal(1, file.DeletionAttemptCount);
        Assert.NotNull(file.MissingDetectedAtUtc);
        Assert.Null(file.DeletionLeaseToken);
    }

    [Fact]
    public void ExternalMissingRequiresAvailableCopyWithoutLease()
    {
        var file = CreateLocal();
        var pending = CreateLocal();
        pending.ClaimDeletion(
            Guid.NewGuid(),
            "worker-a",
            VerifiedAt.AddDays(8),
            VerifiedAt.AddDays(8).AddMinutes(5));

        Assert.Throws<InvalidOperationException>(() => pending.RecordExternalMissing(VerifiedAt.AddDays(8)));
        file.RecordExternalMissing(VerifiedAt.AddDays(1));
        Assert.Equal(BackupFileStatus.Missing, file.Status);
        Assert.Equal(1, file.DeletionAttemptCount);
        Assert.Equal(VerifiedAt.AddDays(1), file.MissingDetectedAtUtc);
        Assert.Throws<InvalidOperationException>(() => file.RecordExternalMissing(VerifiedAt.AddDays(2)));
    }

    [Fact]
    public void StateChangeRejectsInvalidIdentityAndNonUtcTime()
    {
        Assert.Throws<ArgumentException>(() => new BackupFileStateChange(
            Guid.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            BackupFileStatus.Available,
            "file.registered",
            VerifiedAt));
        Assert.Throws<ArgumentException>(() => new BackupFileStateChange(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            BackupFileStatus.Available,
            "file.registered",
            VerifiedAt.ToOffset(TimeSpan.FromHours(8))));
    }

    private static BackupFile CreateLocal()
    {
        return BackupFile.CreateLocal(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            FileTransferProtocol.Smb,
            @"\\synthetic-host\share\backup.bak",
            4096,
            VerifiedAt,
            7);
    }
}
