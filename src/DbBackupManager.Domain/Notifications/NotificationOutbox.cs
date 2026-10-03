using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.Notifications;

public sealed class NotificationOutbox : ConcurrentEntity
{
    private NotificationOutbox()
    {
    }

    public NotificationOutbox(
        Guid id,
        Guid mutationId,
        NotificationType type,
        NotificationSourceKind sourceKind,
        Guid sourceId,
        Guid? taskId,
        BackupTaskStage? stage,
        DateTimeOffset occurredAtUtc,
        string errorCode)
        : base(id)
    {
        ConfigurationValues.RequireDefined(type, nameof(type));
        ConfigurationValues.RequireDefined(sourceKind, nameof(sourceKind));
        MutationId = ConfigurationValues.RequireId(mutationId, nameof(mutationId));
        Type = type;
        SourceKind = sourceKind;
        SourceId = ConfigurationValues.RequireId(sourceId, nameof(sourceId));
        OccurredAtUtc = ConfigurationValues.RequireUtc(occurredAtUtc, nameof(occurredAtUtc));
        ErrorCode = ConfigurationValues.RequireText(errorCode, 100, nameof(errorCode));
        Status = NotificationStatus.Pending;
        SendAttemptCount = 0;
        ValidateIdentity(type, sourceKind, sourceId, taskId, stage);
        TaskId = taskId;
        Stage = stage;
    }

    public Guid MutationId { get; private set; }

    public NotificationType Type { get; private set; }

    public NotificationSourceKind SourceKind { get; private set; }

    public Guid SourceId { get; private set; }

    public Guid? TaskId { get; private set; }

    public BackupTaskStage? Stage { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string ErrorCode { get; private set; } = string.Empty;

    public NotificationStatus Status { get; private set; }

    public Guid? SendLeaseToken { get; private set; }

    public string? SendLeaseOwner { get; private set; }

    public DateTimeOffset? SendLeaseAcquiredAtUtc { get; private set; }

    public DateTimeOffset? SendLeaseExpiresAtUtc { get; private set; }

    public int SendAttemptCount { get; private set; }

    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public string? LastFailureCode { get; private set; }

    public DateTimeOffset? SentAtUtc { get; private set; }

    public void Claim(
        Guid leaseToken,
        string leaseOwner,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var acquiredAt = ConfigurationValues.RequireUtc(acquiredAtUtc, nameof(acquiredAtUtc));
        if (Status is not (NotificationStatus.Pending or NotificationStatus.SendFailed))
        {
            throw new InvalidOperationException("只有待发送或发送失败的通知可以取得发送租约。");
        }

        if (Status == NotificationStatus.SendFailed
            && (NextAttemptAtUtc is null || NextAttemptAtUtc > acquiredAt))
        {
            throw new InvalidOperationException("通知尚未到达下一次允许发送时间。");
        }

        SetSendLease(leaseToken, leaseOwner, acquiredAt, expiresAtUtc);
        SendAttemptCount = checked(SendAttemptCount + 1);
        Status = NotificationStatus.Sending;
        LastFailureCode = null;
        NextAttemptAtUtc = null;
    }

    public void TakeOverExpiredLease(
        Guid leaseToken,
        string leaseOwner,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var acquiredAt = ConfigurationValues.RequireUtc(acquiredAtUtc, nameof(acquiredAtUtc));
        if (Status != NotificationStatus.Sending
            || SendLeaseExpiresAtUtc is null
            || SendLeaseExpiresAtUtc > acquiredAt)
        {
            throw new InvalidOperationException("只有发送租约已经到期的通知可以被接管。");
        }

        ClearSendLease();
        SetSendLease(leaseToken, leaseOwner, acquiredAt, expiresAtUtc);
    }

    public void ReleaseToPending(Guid leaseToken, DateTimeOffset utcNow)
    {
        var now = ConfigurationValues.RequireUtc(utcNow, nameof(utcNow));
        EnsureSendLease(leaseToken, now);
        Status = NotificationStatus.Pending;
        LastFailureCode = null;
        NextAttemptAtUtc = null;
        ClearSendLease();
    }

    public void RecordSent(Guid leaseToken, DateTimeOffset sentAtUtc)
    {
        var sentAt = ConfigurationValues.RequireUtc(sentAtUtc, nameof(sentAtUtc));
        EnsureSendLease(leaseToken, sentAt);
        if (sentAt < OccurredAtUtc)
        {
            throw new ArgumentException("发送时间不能早于通知发生时间。", nameof(sentAtUtc));
        }

        Status = NotificationStatus.Sent;
        SentAtUtc = sentAt;
        LastFailureCode = null;
        NextAttemptAtUtc = null;
        ClearSendLease();
    }

    public void RecordSendFailure(
        Guid leaseToken,
        DateTimeOffset failedAtUtc,
        string failureCode,
        DateTimeOffset nextAttemptAtUtc)
    {
        var failedAt = ConfigurationValues.RequireUtc(failedAtUtc, nameof(failedAtUtc));
        var nextAttempt = ConfigurationValues.RequireUtc(nextAttemptAtUtc, nameof(nextAttemptAtUtc));
        var code = ConfigurationValues.RequireText(failureCode, 100, nameof(failureCode));
        EnsureSendLease(leaseToken, failedAt);
        if (nextAttempt <= failedAt)
        {
            throw new ArgumentException("下次发送时间必须晚于本次失败时间。", nameof(nextAttemptAtUtc));
        }

        Status = NotificationStatus.SendFailed;
        LastFailureCode = code;
        NextAttemptAtUtc = nextAttempt;
        SentAtUtc = null;
        ClearSendLease();
    }

    public void Discard(DateTimeOffset utcNow, string reasonCode)
    {
        _ = ConfigurationValues.RequireUtc(utcNow, nameof(utcNow));
        var code = ConfigurationValues.RequireText(reasonCode, 100, nameof(reasonCode));
        if (Status == NotificationStatus.Discarded)
        {
            if (LastFailureCode != code)
            {
                throw new InvalidOperationException("通知已经以其他原因作废。");
            }

            return;
        }

        if (Status is not (NotificationStatus.Pending or NotificationStatus.SendFailed))
        {
            throw new InvalidOperationException("只有待发送或发送失败的通知可以作废。");
        }

        Status = NotificationStatus.Discarded;
        LastFailureCode = code;
        NextAttemptAtUtc = null;
        SentAtUtc = null;
        ClearSendLease();
    }

    private static void ValidateIdentity(
        NotificationType type,
        NotificationSourceKind sourceKind,
        Guid sourceId,
        Guid? taskId,
        BackupTaskStage? stage)
    {
        switch (type)
        {
            case NotificationType.TaskFailed:
            case NotificationType.TaskIndeterminate:
                if (sourceKind != NotificationSourceKind.BackupTask)
                {
                    throw new ArgumentException("任务通知的来源必须是备份任务。", nameof(sourceKind));
                }

                TaskIdRequired(taskId);
                ConfigurationValues.RequireDefined(
                    stage ?? throw new ArgumentException("任务通知必须包含阶段。", nameof(stage)),
                    nameof(stage));
                if (taskId != sourceId)
                {
                    throw new ArgumentException("任务通知的来源标识必须与任务标识一致。", nameof(sourceId));
                }

                break;
            case NotificationType.RetentionDeleteFailed:
            case NotificationType.RetentionMissing:
                if (sourceKind != NotificationSourceKind.BackupFile)
                {
                    throw new ArgumentException("Retention 通知的来源必须是备份文件。", nameof(sourceKind));
                }

                TaskIdRequired(taskId);
                if (stage is not null)
                {
                    throw new ArgumentException("Retention 通知不能包含备份阶段。", nameof(stage));
                }

                break;
            case NotificationType.AdminTest:
                if (sourceKind != NotificationSourceKind.AdminRequest)
                {
                    throw new ArgumentException("测试通知的来源必须是管理员请求。", nameof(sourceKind));
                }

                if (taskId is not null || stage is not null)
                {
                    throw new ArgumentException("测试通知不能关联备份任务或阶段。", nameof(taskId));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    private static Guid TaskIdRequired(Guid? taskId)
    {
        if (taskId is null)
        {
            throw new ArgumentException("该通知必须关联备份任务。", nameof(taskId));
        }

        return ConfigurationValues.RequireId(taskId.Value, nameof(taskId));
    }

    private void SetSendLease(
        Guid leaseToken,
        string leaseOwner,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var token = ConfigurationValues.RequireId(leaseToken, nameof(leaseToken));
        var owner = ConfigurationValues.RequireText(leaseOwner, 200, nameof(leaseOwner));
        var expiry = ConfigurationValues.RequireUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (acquiredAtUtc < OccurredAtUtc || expiry <= acquiredAtUtc)
        {
            throw new ArgumentException("发送租约时间范围无效。", nameof(expiresAtUtc));
        }

        SendLeaseToken = token;
        SendLeaseOwner = owner;
        SendLeaseAcquiredAtUtc = acquiredAtUtc;
        SendLeaseExpiresAtUtc = expiry;
    }

    private void EnsureSendLease(Guid leaseToken, DateTimeOffset utcNow)
    {
        _ = ConfigurationValues.RequireId(leaseToken, nameof(leaseToken));
        if (Status != NotificationStatus.Sending
            || SendLeaseToken != leaseToken
            || SendLeaseExpiresAtUtc is null
            || SendLeaseExpiresAtUtc <= utcNow)
        {
            throw new InvalidOperationException("当前通知没有有效的发送租约。");
        }
    }

    private void ClearSendLease()
    {
        SendLeaseToken = null;
        SendLeaseOwner = null;
        SendLeaseAcquiredAtUtc = null;
        SendLeaseExpiresAtUtc = null;
    }
}
