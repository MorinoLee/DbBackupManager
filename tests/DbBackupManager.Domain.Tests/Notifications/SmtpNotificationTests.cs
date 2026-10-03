using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Notifications;

namespace DbBackupManager.Domain.Tests.Notifications;

public sealed class SmtpNotificationTests
{
    private static readonly DateTimeOffset OccurredAt = new(2026, 9, 15, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SmtpSettingsRejectsCleartextAndInvalidEmail()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SmtpSettings("smtp.example.test", 587, (SmtpSecurityMode)0, "ops@example.test", 30, false));
        Assert.Throws<ArgumentException>(() =>
            new SmtpSettings("smtp.example.test", 587, SmtpSecurityMode.StartTls, "not-an-email", 30, false));
        var settings = new SmtpSettings(
            "smtp.example.test", 465, SmtpSecurityMode.TlsOnConnect, "ops@example.test", 30, true);
        Assert.Equal(SmtpSettings.SingletonId, settings.Id);
        var plaintext = new SmtpSettings(
            "smtp.example.test", 25, SmtpSecurityMode.Plaintext, "ops@example.test", 30, false);
        Assert.Equal(SmtpSecurityMode.Plaintext, plaintext.SecurityMode);
        Assert.Equal(1, settings.ConfigurationSerial);
        settings.AssignCredential(Guid.NewGuid());
        Assert.NotNull(settings.CredentialReferenceId);
        settings.ClearCredential();
        Assert.Null(settings.CredentialReferenceId);
        Assert.True(settings.ConfigurationSerial > 2);
    }

    [Fact]
    public void RecipientNormalizesAddressForUniqueness()
    {
        var recipient = new SmtpRecipient(Guid.NewGuid(), SmtpSettings.SingletonId, "Ops@Example.TEST");
        Assert.Equal("Ops@Example.TEST", recipient.Address);
        Assert.Equal("OPS@EXAMPLE.TEST", recipient.NormalizedAddress);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    [InlineData(5, 360)]
    [InlineData(9, 360)]
    public void SendBackoffUsesFixedSchedule(int attempts, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), NotificationSendPolicy.Backoff(attempts));
    }

    [Fact]
    public void PendingNotificationCanBeClaimedSentAndFailedWithLeaseRules()
    {
        var taskId = Guid.NewGuid();
        var item = TaskItem(taskId, NotificationType.TaskFailed, BackupTaskStage.Backup);
        var token = Guid.NewGuid();
        var acquired = OccurredAt.AddMinutes(1);
        item.Claim(token, "worker-a", acquired, acquired.AddMinutes(1));
        Assert.Equal(NotificationStatus.Sending, item.Status);
        Assert.Equal(1, item.SendAttemptCount);
        Assert.Throws<InvalidOperationException>(() =>
            item.Claim(Guid.NewGuid(), "worker-b", acquired.AddSeconds(1), acquired.AddMinutes(2)));
        item.RecordSendFailure(
            token,
            acquired.AddSeconds(5),
            "smtp_temporary_failure",
            acquired.AddSeconds(5) + NotificationSendPolicy.Backoff(item.SendAttemptCount));
        Assert.Equal(NotificationStatus.SendFailed, item.Status);
        Assert.Null(item.SendLeaseToken);
        Assert.Throws<InvalidOperationException>(() =>
            item.Claim(Guid.NewGuid(), "worker-a", acquired.AddSeconds(6), acquired.AddMinutes(2)));
        var retry = item.NextAttemptAtUtc!.Value;
        var retryToken = Guid.NewGuid();
        item.Claim(retryToken, "worker-a", retry, retry.AddMinutes(1));
        item.RecordSent(retryToken, retry.AddSeconds(2));
        Assert.Equal(NotificationStatus.Sent, item.Status);
        Assert.Equal(2, item.SendAttemptCount);
        Assert.Throws<InvalidOperationException>(() =>
            item.Claim(Guid.NewGuid(), "worker-a", retry.AddMinutes(2), retry.AddMinutes(3)));
    }

    [Fact]
    public void ExpiredSendingLeaseCanBeTakenOverAndReleasedToPending()
    {
        var item = TaskItem(Guid.NewGuid(), NotificationType.TaskIndeterminate, BackupTaskStage.Transfer);
        var first = Guid.NewGuid();
        var acquired = OccurredAt.AddMinutes(2);
        item.Claim(first, "worker-a", acquired, acquired.AddMinutes(1));
        var takeover = Guid.NewGuid();
        item.TakeOverExpiredLease(takeover, "worker-b", acquired.AddMinutes(1).AddSeconds(1), acquired.AddMinutes(2));
        Assert.Equal(takeover, item.SendLeaseToken);
        Assert.Equal(1, item.SendAttemptCount);
        item.ReleaseToPending(takeover, acquired.AddMinutes(1).AddSeconds(2));
        Assert.Equal(NotificationStatus.Pending, item.Status);
        Assert.Null(item.SendLeaseToken);
    }

    [Fact]
    public void PendingAndFailedNotificationsCanBeDiscardedIdempotently()
    {
        var pending = TaskItem(Guid.NewGuid(), NotificationType.TaskFailed, BackupTaskStage.Backup);
        pending.Discard(OccurredAt.AddMinutes(1), "outbox.discarded");
        Assert.Equal(NotificationStatus.Discarded, pending.Status);
        Assert.Equal("outbox.discarded", pending.LastFailureCode);
        Assert.Null(pending.NextAttemptAtUtc);
        pending.Discard(OccurredAt.AddMinutes(2), "outbox.discarded");
        Assert.Throws<InvalidOperationException>(() =>
            pending.Discard(OccurredAt.AddMinutes(3), "outbox.other"));
        Assert.Throws<InvalidOperationException>(() =>
            pending.Claim(Guid.NewGuid(), "worker-a", OccurredAt.AddMinutes(4), OccurredAt.AddMinutes(5)));

        var failed = TaskItem(Guid.NewGuid(), NotificationType.TaskIndeterminate, BackupTaskStage.Transfer);
        var token = Guid.NewGuid();
        var acquired = OccurredAt.AddMinutes(1);
        failed.Claim(token, "worker-a", acquired, acquired.AddMinutes(1));
        failed.RecordSendFailure(
            token,
            acquired.AddSeconds(5),
            "smtp_temporary_failure",
            acquired.AddSeconds(5) + NotificationSendPolicy.Backoff(failed.SendAttemptCount));
        failed.Discard(acquired.AddMinutes(2), "outbox.discarded");
        Assert.Equal(NotificationStatus.Discarded, failed.Status);
        Assert.Null(failed.NextAttemptAtUtc);
        Assert.Null(failed.SendLeaseToken);
    }

    [Fact]
    public void SendingAndSentNotificationsCannotBeDiscarded()
    {
        var sending = TaskItem(Guid.NewGuid(), NotificationType.TaskFailed, BackupTaskStage.Backup);
        var token = Guid.NewGuid();
        var acquired = OccurredAt.AddMinutes(1);
        sending.Claim(token, "worker-a", acquired, acquired.AddMinutes(1));
        Assert.Throws<InvalidOperationException>(() =>
            sending.Discard(acquired.AddSeconds(1), "outbox.discarded"));

        var sentItem = new NotificationOutbox(
            Guid.NewGuid(),
            Guid.NewGuid(),
            NotificationType.AdminTest,
            NotificationSourceKind.AdminRequest,
            Guid.NewGuid(),
            null,
            null,
            OccurredAt,
            "smtp.test");
        var sentToken = Guid.NewGuid();
        sentItem.Claim(sentToken, "worker-a", acquired, acquired.AddMinutes(1));
        sentItem.RecordSent(sentToken, acquired.AddSeconds(2));
        Assert.Throws<InvalidOperationException>(() =>
            sentItem.Discard(acquired.AddMinutes(2), "outbox.discarded"));
        Assert.Equal(NotificationStatus.Sent, sentItem.Status);
    }

    [Fact]
    public void RetentionAndAdminTestEnforceSourceShape()
    {
        var fileId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var retention = new NotificationOutbox(
            Guid.NewGuid(),
            Guid.NewGuid(),
            NotificationType.RetentionMissing,
            NotificationSourceKind.BackupFile,
            fileId,
            taskId,
            null,
            OccurredAt,
            "file.health_missing");
        Assert.Equal(fileId, retention.SourceId);
        Assert.Null(retention.Stage);
        var requestId = Guid.NewGuid();
        var test = new NotificationOutbox(
            Guid.NewGuid(),
            requestId,
            NotificationType.AdminTest,
            NotificationSourceKind.AdminRequest,
            requestId,
            null,
            null,
            OccurredAt,
            "smtp.test");
        Assert.Equal(NotificationSourceKind.AdminRequest, test.SourceKind);
        Assert.Throws<ArgumentException>(() => new NotificationOutbox(
            Guid.NewGuid(),
            Guid.NewGuid(),
            NotificationType.TaskFailed,
            NotificationSourceKind.BackupTask,
            Guid.NewGuid(),
            Guid.NewGuid(),
            BackupTaskStage.Backup,
            OccurredAt,
            "stage.confirmed_failed"));
        Assert.Throws<ArgumentException>(() => new NotificationOutbox(
            Guid.NewGuid(),
            Guid.NewGuid(),
            NotificationType.AdminTest,
            NotificationSourceKind.AdminRequest,
            Guid.NewGuid(),
            taskId,
            null,
            OccurredAt,
            "smtp.test"));
    }

    private static NotificationOutbox TaskItem(
        Guid taskId,
        NotificationType type,
        BackupTaskStage stage) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            type,
            NotificationSourceKind.BackupTask,
            taskId,
            taskId,
            stage,
            OccurredAt,
            type == NotificationType.TaskFailed ? "stage.confirmed_failed" : "stage.indeterminate");
}
