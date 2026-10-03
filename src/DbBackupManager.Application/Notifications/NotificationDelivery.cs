using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Notifications;

namespace DbBackupManager.Application.Notifications;

public enum SmtpSendStatus
{
    Succeeded = 1,
    NotReady = 2,
    Failed = 3,
    Indeterminate = 4
}

public sealed record SmtpSendResult(SmtpSendStatus Status, string? FailureCode)
{
    public static SmtpSendResult Succeeded() => new(SmtpSendStatus.Succeeded, null);

    public static SmtpSendResult NotReady() => new(SmtpSendStatus.NotReady, null);

    public static SmtpSendResult Failed(string failureCode) =>
        new(SmtpSendStatus.Failed, RequireCode(failureCode));

    public static SmtpSendResult Indeterminate(string failureCode) =>
        new(SmtpSendStatus.Indeterminate, RequireCode(failureCode));

    private static string RequireCode(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        return failureCode.Trim();
    }
}

public sealed class NotificationSendWorkItem
{
    private readonly byte[] _rowVersion;

    public NotificationSendWorkItem(
        Guid outboxId,
        Guid mutationId,
        NotificationType type,
        NotificationSourceKind sourceKind,
        Guid sourceId,
        Guid? taskId,
        BackupTaskStage? stage,
        DateTimeOffset occurredAtUtc,
        string errorCode,
        Guid leaseToken,
        DateTimeOffset leaseExpiresAtUtc,
        byte[] rowVersion)
    {
        ArgumentNullException.ThrowIfNull(rowVersion);
        if (outboxId == Guid.Empty || mutationId == Guid.Empty || sourceId == Guid.Empty || leaseToken == Guid.Empty)
        {
            throw new ArgumentException("通知发送工作项标识不能为空。");
        }

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        if (!Enum.IsDefined(sourceKind))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceKind));
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero || leaseExpiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("通知时间必须使用 UTC。");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        OutboxId = outboxId;
        MutationId = mutationId;
        Type = type;
        SourceKind = sourceKind;
        SourceId = sourceId;
        TaskId = taskId;
        Stage = stage;
        OccurredAtUtc = occurredAtUtc;
        ErrorCode = errorCode.Trim();
        LeaseToken = leaseToken;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
        _rowVersion = [.. rowVersion];
    }

    public Guid OutboxId { get; }

    public Guid MutationId { get; }

    public NotificationType Type { get; }

    public NotificationSourceKind SourceKind { get; }

    public Guid SourceId { get; }

    public Guid? TaskId { get; }

    public BackupTaskStage? Stage { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string ErrorCode { get; }

    public Guid LeaseToken { get; }

    public DateTimeOffset LeaseExpiresAtUtc { get; }

    public byte[] RowVersion => [.. _rowVersion];
}

public sealed record ClaimNotificationSendCommand(
    Guid LeaseToken,
    string LeaseOwner,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record NotificationSendCommitCommand(
    Guid MutationId,
    DateTimeOffset OccurredAtUtc,
    string? FailureCode);

public sealed record DiscardNotificationCommand(
    IReadOnlyList<Guid> OutboxIds,
    string ReasonCode,
    DateTimeOffset OccurredAtUtc);

public sealed record NotificationDeliveryOptions(TimeSpan IdleInterval, TimeSpan LeaseDuration)
{
    public static NotificationDeliveryOptions Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(60));

    public void Validate()
    {
        if (IdleInterval <= TimeSpan.Zero || LeaseDuration < IdleInterval * 3)
        {
            throw new InvalidOperationException("通知空闲间隔或发送租约配置无效。");
        }
    }
}

public interface ISmtpMailSender
{
    Task<SmtpSendResult> SendAsync(
        NotificationSendWorkItem work,
        string subject,
        string body,
        CancellationToken cancellationToken = default);
}

public interface INotificationOutboxStore
{
    Task<NotificationSendWorkItem?> ClaimNextAsync(
        ClaimNotificationSendCommand command,
        CancellationToken cancellationToken = default);

    Task<NotificationSendWorkItem?> ClaimOutboxAsync(
        Guid outboxId,
        ClaimNotificationSendCommand command,
        CancellationToken cancellationToken = default);

    Task ReleaseToPendingAsync(
        NotificationSendWorkItem work,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    Task CommitSentAsync(
        NotificationSendWorkItem work,
        NotificationSendCommitCommand command,
        CancellationToken cancellationToken = default);

    Task CommitSendFailureAsync(
        NotificationSendWorkItem work,
        NotificationSendCommitCommand command,
        CancellationToken cancellationToken = default);

    Task<int> DiscardAsync(
        DiscardNotificationCommand command,
        CancellationToken cancellationToken = default);
}

public static class NotificationMessageFormatter
{
    public static string Subject(NotificationSendWorkItem work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return work.Type == NotificationType.AdminTest ? "备份通知测试" : "备份失败通知";
    }

    public static string Body(NotificationSendWorkItem work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var identity = work.TaskId?.ToString("N")[..8]
            ?? work.SourceId.ToString("N")[..8];
        var stage = work.Stage switch
        {
            BackupTaskStage.Backup => "Backup",
            BackupTaskStage.VerifyLocal => "VerifyLocal",
            BackupTaskStage.Transfer => "Transfer",
            BackupTaskStage.ValidateCopy => "ValidateCopy",
            BackupTaskStage.Cleanup => "Cleanup",
            _ when work.Type == NotificationType.AdminTest => "管理员测试",
            _ => "Retention",
        };
        return string.Join(
            Environment.NewLine,
            [
                $"任务 {identity}",
                $"阶段 {stage}",
                $"时间 {work.OccurredAtUtc:yyyy-MM-dd HH:mm:ss} UTC",
                $"分类 {work.ErrorCode}",
            ]);
    }
}

public sealed class NotificationDeliveryRunner(
    INotificationOutboxStore store,
    ISmtpMailSender sender,
    TimeProvider clock,
    NotificationDeliveryOptions options)
{
    private readonly string _owner = Guid.NewGuid().ToString("N");

    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        options.Validate();
        var now = clock.GetUtcNow();
        var claimed = await store.ClaimNextAsync(
            new ClaimNotificationSendCommand(
                Guid.NewGuid(),
                _owner,
                now,
                now + options.LeaseDuration),
            cancellationToken);
        if (claimed is null)
        {
            return false;
        }

        await ProcessClaimedAsync(claimed, cancellationToken);
        return true;
    }

    public async Task ProcessClaimedAsync(
        NotificationSendWorkItem claimed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claimed);
        options.Validate();
        SmtpSendResult result;
        try
        {
            result = await sender.SendAsync(
                claimed,
                NotificationMessageFormatter.Subject(claimed),
                NotificationMessageFormatter.Body(claimed),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var occurredAt = clock.GetUtcNow();
        var command = new NotificationSendCommitCommand(claimed.MutationId, occurredAt, result.FailureCode);
        switch (result.Status)
        {
            case SmtpSendStatus.Succeeded:
                await store.CommitSentAsync(claimed, command, cancellationToken);
                break;
            case SmtpSendStatus.NotReady:
                await store.ReleaseToPendingAsync(claimed, occurredAt, cancellationToken);
                break;
            case SmtpSendStatus.Failed:
            case SmtpSendStatus.Indeterminate:
                await store.CommitSendFailureAsync(claimed, command, cancellationToken);
                break;
            default:
                throw new InvalidOperationException("SMTP 发送结果状态无效。");
        }
    }
}
