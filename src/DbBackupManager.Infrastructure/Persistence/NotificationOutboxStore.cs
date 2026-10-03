using System.Data;
using DbBackupManager.Application.Notifications;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class NotificationOutboxStore(
    IDbContextFactory<PlatformDbContext> contextFactory) : INotificationOutboxStore
{
    public async Task<NotificationSendWorkItem?> ClaimNextAsync(
        ClaimNotificationSendCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateClaim(command);
        return await ClaimAsync(null, command, cancellationToken);
    }

    public async Task<NotificationSendWorkItem?> ClaimOutboxAsync(
        Guid outboxId,
        ClaimNotificationSendCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateClaim(command);
        if (outboxId == Guid.Empty)
        {
            throw new ArgumentException("通知标识不能为空。", nameof(outboxId));
        }

        return await ClaimAsync(outboxId, command, cancellationToken);
    }

    private async Task<NotificationSendWorkItem?> ClaimAsync(
        Guid? outboxId,
        ClaimNotificationSendCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteWithStrategyAsync(async context =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
                if (!await IsDeliveryReadyAsync(context, cancellationToken))
                {
                    await transaction.CommitAsync(cancellationToken);
                    return null;
                }

                var item = await FindClaimCandidateAsync(context, outboxId, command, cancellationToken);
                if (item is null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return null;
                }

                if (item.Status == NotificationStatus.Sending
                    && item.SendLeaseExpiresAtUtc <= command.AcquiredAtUtc)
                {
                    item.TakeOverExpiredLease(
                        command.LeaseToken,
                        command.LeaseOwner,
                        command.AcquiredAtUtc,
                        command.ExpiresAtUtc);
                }
                else if (item.Status == NotificationStatus.Pending
                    || (item.Status == NotificationStatus.SendFailed
                        && item.NextAttemptAtUtc <= command.AcquiredAtUtc))
                {
                    item.Claim(
                        command.LeaseToken,
                        command.LeaseOwner,
                        command.AcquiredAtUtc,
                        command.ExpiresAtUtc);
                }
                else
                {
                    await transaction.CommitAsync(cancellationToken);
                    return null;
                }

                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return CreateWorkItem(item, command.LeaseToken, item.SendLeaseExpiresAtUtc!.Value);
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
        catch (DbUpdateException)
        {
            return null;
        }
    }

    public async Task<int> DiscardAsync(
        DiscardNotificationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.OccurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("时间必须使用 UTC。", nameof(command));
        }

        var reason = command.ReasonCode.Trim();
        if (reason.Length is 0 or > 100)
        {
            throw new ArgumentException("作废原因码无效。", nameof(command));
        }

        var ids = command.OutboxIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (ids.Length == 0 || ids.Length != command.OutboxIds.Count)
        {
            throw new ArgumentException("作废通知标识无效。", nameof(command));
        }

        return await ExecuteWithStrategyAsync(async context =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var items = await context.NotificationOutbox.AsTracking()
                .Where(item => ids.Contains(item.Id))
                .ToListAsync(cancellationToken);
            if (items.Count != ids.Length)
            {
                throw new InvalidOperationException("作废通知标识不完整或不存在。");
            }

            var discarded = 0;
            foreach (var item in items.OrderBy(item => item.Id))
            {
                var alreadyDiscarded = item.Status == NotificationStatus.Discarded
                    && item.LastFailureCode == reason;
                item.Discard(command.OccurredAtUtc, reason);
                if (alreadyDiscarded)
                {
                    continue;
                }

                context.AuditRecords.Add(new AuditRecord(
                    null,
                    "notification.outbox.discard",
                    "NotificationOutbox",
                    item.Id.ToString("N"),
                    "succeeded",
                    reason));
                discarded++;
            }

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return discarded;
        }, cancellationToken);
    }

    public Task ReleaseToPendingAsync(
        NotificationSendWorkItem work,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        MutateAsync(work, utcNow, (item, now) =>
        {
            item.ReleaseToPending(work.LeaseToken, now);
            return "notification.send.release";
        }, null, cancellationToken);

    public Task CommitSentAsync(
        NotificationSendWorkItem work,
        NotificationSendCommitCommand command,
        CancellationToken cancellationToken = default) =>
        MutateAsync(work, command.OccurredAtUtc, (item, now) =>
        {
            item.RecordSent(work.LeaseToken, now);
            return "notification.send.sent";
        }, null, cancellationToken);

    public Task CommitSendFailureAsync(
        NotificationSendWorkItem work,
        NotificationSendCommitCommand command,
        CancellationToken cancellationToken = default) =>
        MutateAsync(work, command.OccurredAtUtc, (item, now) =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command.FailureCode);
            item.RecordSendFailure(
                work.LeaseToken,
                now,
                command.FailureCode,
                now + NotificationSendPolicy.Backoff(item.SendAttemptCount));
            return "notification.send.failed";
        }, command.FailureCode, cancellationToken);

    private async Task MutateAsync(
        NotificationSendWorkItem work,
        DateTimeOffset utcNow,
        Func<NotificationOutbox, DateTimeOffset, string> apply,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (utcNow.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("时间必须使用 UTC。", nameof(utcNow));
        }

        var now = utcNow;
        await ExecuteWithStrategyAsync(async context =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var item = await context.NotificationOutbox.AsTracking()
                .SingleOrDefaultAsync(x => x.Id == work.OutboxId, cancellationToken);
            if (item is null
                || item.SendLeaseToken != work.LeaseToken
                || !item.RowVersion.SequenceEqual(work.RowVersion))
            {
                await transaction.CommitAsync(cancellationToken);
                return 0;
            }

            var action = apply(item, now);
            context.AuditRecords.Add(new AuditRecord(
                null,
                action,
                "NotificationOutbox",
                item.Id.ToString("N"),
                "succeeded",
                failureCode));
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return 1;
        }, cancellationToken);
    }

    private static Task<bool> IsDeliveryReadyAsync(
        PlatformDbContext context, CancellationToken cancellationToken) =>
        context.SmtpSettings.AnyAsync(settings =>
            settings.Id == SmtpSettings.SingletonId
            && settings.IsEnabled
            && context.SmtpRecipients.Any(recipient => recipient.SmtpSettingsId == settings.Id),
            cancellationToken);

    private static async Task<NotificationOutbox?> FindClaimCandidateAsync(
        PlatformDbContext context,
        Guid? outboxId,
        ClaimNotificationSendCommand command,
        CancellationToken cancellationToken)
    {
        if (outboxId is { } id)
        {
            return await context.NotificationOutbox.AsTracking()
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        }

        var expired = await context.NotificationOutbox.AsTracking()
            .Where(item => item.Status == NotificationStatus.Sending
                && item.SendLeaseExpiresAtUtc <= command.AcquiredAtUtc)
            .OrderBy(item => item.SendLeaseExpiresAtUtc)
            .ThenBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (expired is not null)
        {
            return expired;
        }

        return await context.NotificationOutbox.AsTracking()
            .Where(item =>
                item.Status == NotificationStatus.Pending
                || (item.Status == NotificationStatus.SendFailed
                    && item.NextAttemptAtUtc <= command.AcquiredAtUtc))
            .OrderBy(item => item.OccurredAtUtc)
            .ThenBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<T> ExecuteWithStrategyAsync<T>(
        Func<PlatformDbContext, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            return await action(context);
        });
    }

    private static NotificationSendWorkItem CreateWorkItem(
        NotificationOutbox item,
        Guid leaseToken,
        DateTimeOffset leaseExpiresAtUtc) =>
        new(
            item.Id,
            item.MutationId,
            item.Type,
            item.SourceKind,
            item.SourceId,
            item.TaskId,
            item.Stage,
            item.OccurredAtUtc,
            item.ErrorCode,
            leaseToken,
            leaseExpiresAtUtc,
            item.RowVersion);

    private static void ValidateClaim(ClaimNotificationSendCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.LeaseToken == Guid.Empty)
        {
            throw new ArgumentException("发送租约标识不能为空。", nameof(command));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command.LeaseOwner);
        if (command.AcquiredAtUtc.Offset != TimeSpan.Zero
            || command.ExpiresAtUtc.Offset != TimeSpan.Zero
            || command.ExpiresAtUtc <= command.AcquiredAtUtc)
        {
            throw new ArgumentException("发送租约时间范围无效。", nameof(command));
        }
    }
}
