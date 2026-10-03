using DbBackupManager.Application.Notifications;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Notifications;

namespace DbBackupManager.Application.Tests;

public sealed class NotificationDeliveryContractTests
{
    [Fact]
    public void MessageBodyOnlyContainsShortIdentityStageTimeAndErrorCode()
    {
        var work = CreateWork(NotificationType.TaskFailed, BackupTaskStage.VerifyLocal, "stage.confirmed_failed");
        var body = NotificationMessageFormatter.Body(work);
        var subject = NotificationMessageFormatter.Subject(work);

        Assert.Equal("备份失败通知", subject);
        Assert.Contains(work.TaskId!.Value.ToString("N")[..8], body, StringComparison.Ordinal);
        Assert.Contains("VerifyLocal", body, StringComparison.Ordinal);
        Assert.Contains("stage.confirmed_failed", body, StringComparison.Ordinal);
        Assert.DoesNotContain('@', body);
        Assert.DoesNotContain('\\', body);
        Assert.DoesNotContain("smtp.", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunnerReleasesWhenSenderReportsNotReadyAndCommitsSentOnSuccess()
    {
        var work = CreateWork(NotificationType.AdminTest, null, "smtp.test");
        var store = new FakeStore(work);
        var runner = new NotificationDeliveryRunner(
            store,
            new FakeSender(SmtpSendResult.NotReady()),
            TimeProvider.System,
            NotificationDeliveryOptions.Default);

        Assert.True(await runner.RunOnceAsync());
        Assert.Equal("release", store.LastAction);

        store.Reset(work);
        var sent = new NotificationDeliveryRunner(
            store,
            new FakeSender(SmtpSendResult.Succeeded()),
            TimeProvider.System,
            NotificationDeliveryOptions.Default);
        Assert.True(await sent.RunOnceAsync());
        Assert.Equal("sent", store.LastAction);
    }

    [Theory]
    [InlineData(NotificationType.TaskFailed, BackupTaskStage.Backup, NotificationSourceKind.BackupTask, "stage.confirmed_failed")]
    [InlineData(NotificationType.TaskIndeterminate, BackupTaskStage.Transfer, NotificationSourceKind.BackupTask, "stage.indeterminate")]
    [InlineData(NotificationType.RetentionDeleteFailed, null, NotificationSourceKind.BackupFile, "file.delete_failed")]
    [InlineData(NotificationType.RetentionMissing, null, NotificationSourceKind.BackupFile, "file.health_missing")]
    [InlineData(NotificationType.AdminTest, null, NotificationSourceKind.AdminRequest, "smtp.test")]
    public void MessageBodiesStayRedactedForEveryNotificationType(
        NotificationType type,
        BackupTaskStage? stage,
        NotificationSourceKind sourceKind,
        string errorCode)
    {
        var work = CreateWork(type, stage, errorCode, sourceKind);
        var body = NotificationMessageFormatter.Body(work);
        var subject = NotificationMessageFormatter.Subject(work);

        Assert.Equal(type == NotificationType.AdminTest ? "备份通知测试" : "备份失败通知", subject);
        Assert.DoesNotContain('@', body);
        Assert.DoesNotContain('\\', body);
        Assert.DoesNotContain('/', body);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("smtp.example", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(errorCode, body, StringComparison.Ordinal);
        Assert.Contains("UTC", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SmtpSendStatus.Failed, "smtp_permanent_failure")]
    [InlineData(SmtpSendStatus.Indeterminate, "smtp_response_lost")]
    public async Task RunnerCommitsFailurePathAndNeverMarksIndeterminateAsSent(
        SmtpSendStatus status,
        string failureCode)
    {
        var work = CreateWork(NotificationType.AdminTest, null, "smtp.test");
        var store = new FakeStore(work);
        var result = status == SmtpSendStatus.Failed
            ? SmtpSendResult.Failed(failureCode)
            : SmtpSendResult.Indeterminate(failureCode);
        var runner = new NotificationDeliveryRunner(
            store,
            new FakeSender(result),
            TimeProvider.System,
            NotificationDeliveryOptions.Default);

        Assert.True(await runner.RunOnceAsync());
        Assert.Equal("failed", store.LastAction);
        Assert.NotEqual("sent", store.LastAction);
    }

    [Fact]
    public async Task ProcessClaimedCommitsWithoutCallingClaimNext()
    {
        var work = CreateWork(NotificationType.AdminTest, null, "smtp.test");
        var store = new FakeStore(work);
        var runner = new NotificationDeliveryRunner(
            store,
            new FakeSender(SmtpSendResult.Succeeded()),
            TimeProvider.System,
            NotificationDeliveryOptions.Default);

        await runner.ProcessClaimedAsync(work);
        Assert.Equal("sent", store.LastAction);
    }

    private static NotificationSendWorkItem CreateWork(
        NotificationType type,
        BackupTaskStage? stage,
        string errorCode,
        NotificationSourceKind? sourceKind = null)
    {
        var kind = sourceKind ?? (type == NotificationType.AdminTest
            ? NotificationSourceKind.AdminRequest
            : type is NotificationType.RetentionDeleteFailed or NotificationType.RetentionMissing
                ? NotificationSourceKind.BackupFile
                : NotificationSourceKind.BackupTask);
        var source = Guid.NewGuid();
        return new NotificationSendWorkItem(
            Guid.NewGuid(),
            Guid.NewGuid(),
            type,
            kind,
            source,
            kind == NotificationSourceKind.AdminRequest ? null : source,
            stage,
            DateTimeOffset.UtcNow,
            errorCode,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1),
            [1, 2, 3, 4, 5, 6, 7, 8]);
    }

    private sealed class FakeStore : INotificationOutboxStore
    {
        private readonly Guid _outboxId;
        private NotificationSendWorkItem? _next;

        public FakeStore(NotificationSendWorkItem work)
        {
            _outboxId = work.OutboxId;
            _next = work;
        }

        public string? LastAction { get; private set; }

        public void Reset(NotificationSendWorkItem next)
        {
            _next = next;
            LastAction = null;
        }

        public Task<NotificationSendWorkItem?> ClaimNextAsync(
            ClaimNotificationSendCommand command,
            CancellationToken cancellationToken = default)
        {
            var claimed = _next;
            _next = null;
            return Task.FromResult(claimed);
        }

        public Task<NotificationSendWorkItem?> ClaimOutboxAsync(
            Guid outboxId,
            ClaimNotificationSendCommand command,
            CancellationToken cancellationToken = default)
        {
            if (outboxId != _outboxId)
            {
                return Task.FromResult<NotificationSendWorkItem?>(null);
            }

            var claimed = _next;
            _next = null;
            return Task.FromResult(claimed);
        }

        public Task ReleaseToPendingAsync(
            NotificationSendWorkItem item,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default)
        {
            LastAction = "release";
            return Task.CompletedTask;
        }

        public Task CommitSentAsync(
            NotificationSendWorkItem item,
            NotificationSendCommitCommand command,
            CancellationToken cancellationToken = default)
        {
            LastAction = "sent";
            return Task.CompletedTask;
        }

        public Task CommitSendFailureAsync(
            NotificationSendWorkItem item,
            NotificationSendCommitCommand command,
            CancellationToken cancellationToken = default)
        {
            LastAction = "failed";
            return Task.CompletedTask;
        }

        public Task<int> DiscardAsync(
            DiscardNotificationCommand command,
            CancellationToken cancellationToken = default)
        {
            LastAction = "discard";
            return Task.FromResult(command.OutboxIds.Count);
        }
    }

    private sealed class FakeSender(SmtpSendResult result) : ISmtpMailSender
    {
        public Task<SmtpSendResult> SendAsync(
            NotificationSendWorkItem work,
            string subject,
            string body,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
}
