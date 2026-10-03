using System.Data.Common;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.Notifications;
using DbBackupManager.Application.Servers;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Domain.Notifications;
using DbBackupManager.Infrastructure.Notifications;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class SmtpNotificationSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>, IDisposable
{
    private const string KeyPrefix = "DbBackupManagerP586Keys_";
    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"{KeyPrefix}{Guid.NewGuid():N}");

    [Fact]
    public async Task SettingsRoundTripProtectsPasswordAndDeduplicatesOutbox()
    {
        await ClearSmtpAsync();
        var actor = await CreateActorAsync();
        using var provider = database.CreateServiceProvider();
        var service = Service(provider);
        var saved = await service.SaveAsync(
            actor,
            null,
            new SmtpSettingsInput("smtp.example.test", 587, (int)SmtpSecurityMode.StartTls, "ops@example.test", 30, false));
        Assert.Equal(ManagementCode.Succeeded, saved.Code);
        var item = saved.Value!;
        Assert.False(item.HasCredential);
        var added = await service.AddRecipientAsync(actor, item.Version, "Ops@Example.TEST");
        Assert.Equal(ManagementCode.Succeeded, added.Code);
        item = added.Value!;
        var duplicate = await service.AddRecipientAsync(actor, item.Version, "ops@example.test");
        Assert.Equal(ManagementCode.Conflict, duplicate.Code);

        var rotated = await service.RotatePasswordAsync(actor, item.Version, "smtp-user", "synthetic-smtp-password");
        Assert.Equal(ManagementCode.Succeeded, rotated.Code);
        Assert.True(rotated.Value!.HasCredential);
        Assert.DoesNotContain("synthetic-smtp-password", rotated.Value.Host, StringComparison.Ordinal);
        await using (var db = database.CreateContext())
        {
            var credential = await db.CredentialReferences.SingleAsync(x => x.Kind == Domain.Configuration.CredentialKind.SmtpPassword);
            Assert.Equal(BusinessCredentialDataProtector.SmtpPasswordProtectionVersion, credential.ProtectionVersion);
            Assert.DoesNotContain("synthetic-smtp-password", credential.ProtectedSecret, StringComparison.Ordinal);
            var decrypted = new BusinessCredentialDataProtector(
                _keyPath, purpose: BusinessCredentialDataProtector.SmtpPasswordPurpose)
                .UnprotectSqlPassword(credential.ProtectedSecret);
            Assert.Equal("synthetic-smtp-password", new string(decrypted.Secret!));
            Array.Clear(decrypted.Secret!);
        }

        var enabled = await service.SetEnabledAsync(actor, rotated.Value.Version, true);
        var requestId = Guid.NewGuid();
        var first = await service.QueueTestAsync(actor, enabled.Value!.Version, requestId);
        var second = await service.QueueTestAsync(actor, first.Value!.Version, requestId);
        Assert.Equal(ManagementCode.Succeeded, first.Code);
        Assert.Equal(ManagementCode.Succeeded, second.Code);
        await using (var db = database.CreateContext())
        {
            Assert.Equal(1, await db.NotificationOutbox.CountAsync(x => x.MutationId == requestId));
            Assert.True(await db.AuditRecords.AnyAsync(x => x.Action == "smtp.test.enqueue"));
        }
    }

    [Fact]
    public async Task DisabledSmtpLeavesPendingAndEnabledClaimCanCompleteWithFakeSender()
    {
        await ClearSmtpAsync();
        var actor = await CreateActorAsync();
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var service = Service(provider);
        var saved = await service.SaveAsync(
            actor,
            null,
            new SmtpSettingsInput("smtp.example.test", 587, (int)SmtpSecurityMode.StartTls, "ops@example.test", 30, false));
        var withRecipient = await service.AddRecipientAsync(actor, saved.Value!.Version, "ops@example.test");
        var mutationId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            NotificationOutboxWriter.TryAdd(
                db,
                mutationId,
                NotificationType.TaskFailed,
                NotificationSourceKind.BackupTask,
                taskId,
                taskId,
                BackupTaskStage.Backup,
                DateTimeOffset.UtcNow,
                "stage.confirmed_failed");
            await db.SaveChangesAsync();
        }

        var store = new NotificationOutboxStore(factory);
        var now = DateTimeOffset.UtcNow;
        Assert.Null(await store.ClaimNextAsync(new ClaimNotificationSendCommand(
            Guid.NewGuid(), "worker-a", now, now.AddMinutes(1))));
        await using (var db = database.CreateContext())
        {
            var pending = await db.NotificationOutbox.SingleAsync(x => x.MutationId == mutationId);
            Assert.Equal(NotificationStatus.Pending, pending.Status);
        }

        await service.SetEnabledAsync(actor, withRecipient.Value!.Version, true);
        var runner = new NotificationDeliveryRunner(
            store,
            new ImmediateSender(),
            TimeProvider.System,
            NotificationDeliveryOptions.Default);
        Assert.True(await runner.RunOnceAsync());
        await using (var db = database.CreateContext())
        {
            var sent = await db.NotificationOutbox.SingleAsync(x => x.MutationId == mutationId);
            Assert.Equal(NotificationStatus.Sent, sent.Status);
            Assert.True(await db.AuditRecords.AnyAsync(x => x.Action == "notification.send.sent"));
        }
    }

    [Fact]
    public async Task ClaimOutboxAsyncClaimsSpecifiedItemAndLeavesOlderPending()
    {
        await ClearSmtpAsync();
        var actor = await CreateActorAsync();
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var service = Service(provider);
        var saved = await service.SaveAsync(
            actor,
            null,
            new SmtpSettingsInput("smtp.example.test", 587, (int)SmtpSecurityMode.StartTls, "ops@example.test", 30, true));
        await service.AddRecipientAsync(actor, saved.Value!.Version, "ops@example.test");
        var olderId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var olderMutation = Guid.NewGuid();
        var targetMutation = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            db.NotificationOutbox.Add(new NotificationOutbox(
                olderId,
                olderMutation,
                NotificationType.TaskFailed,
                NotificationSourceKind.BackupTask,
                olderMutation,
                olderMutation,
                BackupTaskStage.Backup,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                "stage.confirmed_failed"));
            db.NotificationOutbox.Add(new NotificationOutbox(
                targetId,
                targetMutation,
                NotificationType.AdminTest,
                NotificationSourceKind.AdminRequest,
                targetMutation,
                null,
                null,
                DateTimeOffset.UtcNow,
                "smtp.test"));
            await db.SaveChangesAsync();
        }

        var store = new NotificationOutboxStore(factory);
        var now = DateTimeOffset.UtcNow;
        var claimed = await store.ClaimOutboxAsync(
            targetId,
            new ClaimNotificationSendCommand(Guid.NewGuid(), "worker-a", now, now.AddMinutes(1)));

        Assert.NotNull(claimed);
        Assert.Equal(targetId, claimed.OutboxId);
        await using (var db = database.CreateContext())
        {
            Assert.Equal(NotificationStatus.Sending, (await db.NotificationOutbox.SingleAsync(x => x.Id == targetId)).Status);
            Assert.Equal(NotificationStatus.Pending, (await db.NotificationOutbox.SingleAsync(x => x.Id == olderId)).Status);
        }
    }

    [Fact]
    public async Task DiscardMarksPendingAndSendFailedAndLeavesSentUntouched()
    {
        await ClearSmtpAsync();
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var pendingId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        var sentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var db = database.CreateContext())
        {
            db.NotificationOutbox.Add(new NotificationOutbox(
                pendingId,
                Guid.NewGuid(),
                NotificationType.TaskFailed,
                NotificationSourceKind.BackupTask,
                pendingId,
                pendingId,
                BackupTaskStage.Backup,
                now.AddMinutes(-2),
                "stage.confirmed_failed"));
            var failed = new NotificationOutbox(
                failedId,
                Guid.NewGuid(),
                NotificationType.TaskIndeterminate,
                NotificationSourceKind.BackupTask,
                failedId,
                failedId,
                BackupTaskStage.Backup,
                now.AddMinutes(-3),
                "stage.indeterminate");
            var failedToken = Guid.NewGuid();
            failed.Claim(failedToken, "worker-a", now.AddMinutes(-1), now);
            failed.RecordSendFailure(
                failedToken,
                now.AddSeconds(-30),
                "smtp_temporary_failure",
                now.AddMinutes(5));
            db.NotificationOutbox.Add(failed);
            var sent = new NotificationOutbox(
                sentId,
                Guid.NewGuid(),
                NotificationType.AdminTest,
                NotificationSourceKind.AdminRequest,
                sentId,
                null,
                null,
                now.AddMinutes(-4),
                "smtp.test");
            var sentToken = Guid.NewGuid();
            sent.Claim(sentToken, "worker-a", now.AddMinutes(-3), now.AddMinutes(-1));
            sent.RecordSent(sentToken, now.AddMinutes(-2));
            db.NotificationOutbox.Add(sent);
            await db.SaveChangesAsync();
        }

        var store = new NotificationOutboxStore(factory);
        var discarded = await store.DiscardAsync(
            new DiscardNotificationCommand([pendingId, failedId], "outbox.discarded", now));
        var replay = await store.DiscardAsync(
            new DiscardNotificationCommand([pendingId, failedId], "outbox.discarded", now.AddSeconds(1)));

        Assert.Equal(2, discarded);
        Assert.Equal(0, replay);
        await using (var db = database.CreateContext())
        {
            Assert.Equal(NotificationStatus.Discarded, (await db.NotificationOutbox.SingleAsync(x => x.Id == pendingId)).Status);
            Assert.Equal(NotificationStatus.Discarded, (await db.NotificationOutbox.SingleAsync(x => x.Id == failedId)).Status);
            Assert.Equal(NotificationStatus.Sent, (await db.NotificationOutbox.SingleAsync(x => x.Id == sentId)).Status);
            Assert.Equal(2, await db.AuditRecords.CountAsync(x => x.Action == "notification.outbox.discard"));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DiscardAsync(new DiscardNotificationCommand([sentId], "outbox.discarded", now.AddMinutes(1))));
    }

    [Fact]
    public async Task DiscardedOutboxIsNeverClaimedEvenWhenSmtpIsReady()
    {
        await ClearSmtpAsync();
        var actor = await CreateActorAsync();
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var service = Service(provider);
        var saved = await service.SaveAsync(
            actor,
            null,
            new SmtpSettingsInput("smtp.example.test", 587, (int)SmtpSecurityMode.StartTls, "ops@example.test", 30, false));
        var withRecipient = await service.AddRecipientAsync(actor, saved.Value!.Version, "ops@example.test");
        await service.SetEnabledAsync(actor, withRecipient.Value!.Version, true);
        var pendingId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var db = database.CreateContext())
        {
            db.NotificationOutbox.Add(new NotificationOutbox(
                pendingId,
                Guid.NewGuid(),
                NotificationType.TaskFailed,
                NotificationSourceKind.BackupTask,
                pendingId,
                pendingId,
                BackupTaskStage.Backup,
                now.AddMinutes(-2),
                "stage.confirmed_failed"));
            await db.SaveChangesAsync();
        }

        var store = new NotificationOutboxStore(factory);
        Assert.Equal(1, await store.DiscardAsync(
            new DiscardNotificationCommand([pendingId], "outbox.discarded", now)));
        Assert.Null(await store.ClaimNextAsync(new ClaimNotificationSendCommand(
            Guid.NewGuid(), "worker-a", now.AddSeconds(1), now.AddMinutes(1))));
        await using (var db = database.CreateContext())
        {
            Assert.Equal(NotificationStatus.Discarded, (await db.NotificationOutbox.SingleAsync()).Status);
        }
    }

    [Fact]
    public async Task ConcurrentDiscardOfSameIdsAddsAuditOnce()
    {
        await ClearSmtpAsync();
        var pendingId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var db = database.CreateContext())
        {
            db.NotificationOutbox.Add(new NotificationOutbox(
                pendingId,
                Guid.NewGuid(),
                NotificationType.TaskFailed,
                NotificationSourceKind.BackupTask,
                pendingId,
                pendingId,
                BackupTaskStage.Backup,
                now.AddMinutes(-2),
                "stage.confirmed_failed"));
            await db.SaveChangesAsync();
        }

        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var first = new NotificationOutboxStore(factory);
        var second = new NotificationOutboxStore(factory);
        var command = new DiscardNotificationCommand([pendingId], "outbox.discarded", now);
        var counts = await Task.WhenAll(
            first.DiscardAsync(command),
            second.DiscardAsync(command));

        Assert.Equal(1, counts.Sum());
        await using (var db = database.CreateContext())
        {
            Assert.Equal(NotificationStatus.Discarded, (await db.NotificationOutbox.SingleAsync()).Status);
            Assert.Equal(1, await db.AuditRecords.CountAsync(x =>
                x.Action == "notification.outbox.discard"
                && x.TargetId == pendingId.ToString("N")));
        }
    }

    [Fact]
    public async Task SavePlaintextModeWritesAuditReasonAndDoesNotDowngradeStartTls()
    {
        await ClearSmtpAsync();
        var actor = await CreateActorAsync();
        using var provider = database.CreateServiceProvider();
        var service = Service(provider);
        var saved = await service.SaveAsync(
            actor,
            null,
            new SmtpSettingsInput("smtp.example.test", 25, (int)SmtpSecurityMode.Plaintext, "ops@example.test", 30, false));
        Assert.Equal(ManagementCode.Succeeded, saved.Code);
        Assert.Equal((int)SmtpSecurityMode.Plaintext, saved.Value!.SecurityMode);
        await using (var db = database.CreateContext())
        {
            var audit = await db.AuditRecords.SingleAsync(x => x.Action == "smtp.settings.create");
            Assert.Equal("smtp.plaintext", audit.ReasonCode);
            Assert.Equal(SmtpSecurityMode.Plaintext, (await db.SmtpSettings.SingleAsync()).SecurityMode);
        }

        var encrypted = await service.SaveAsync(
            actor,
            saved.Value.Version,
            new SmtpSettingsInput("smtp.example.test", 587, (int)SmtpSecurityMode.StartTls, "ops@example.test", 30, false));
        Assert.Equal(ManagementCode.Succeeded, encrypted.Code);
        await using (var db = database.CreateContext())
        {
            Assert.Equal(SmtpSecurityMode.StartTls, (await db.SmtpSettings.SingleAsync()).SecurityMode);
            Assert.True(await db.AuditRecords.AnyAsync(
                x => x.Action == "smtp.settings.update" && x.ReasonCode == null));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("no-recipient")]
    public async Task BothClaimEntrypointsCheckReadinessOnceAndLeavePendingUntouched(string configuration)
    {
        await ClearSmtpAsync();
        var now = DateTimeOffset.UtcNow;
        var pending = NewNotification(now.AddMinutes(-5));
        await using (var db = database.CreateContext())
        {
            if (configuration != "missing")
            {
                db.SmtpSettings.Add(NewSettings(configuration != "disabled"));
                if (configuration == "disabled") db.SmtpRecipients.Add(NewRecipient());
            }
            db.NotificationOutbox.Add(pending);
            await db.SaveChangesAsync();
        }

        var counter = new ClaimReadCounter();
        var store = new NotificationOutboxStore(new InterceptingFactory(database, counter));
        foreach (var specified in new[] { false, true })
        {
            counter.Reset();
            Assert.Null(await ClaimAsync(store, specified, pending.Id, now));
            Assert.Equal(1, counter.SettingsReads);
            Assert.Equal(0, counter.OutboxReads);
            Assert.Equal(0, counter.MaterializedSettings);
            await using var db = database.CreateContext();
            var unchanged = await db.NotificationOutbox.SingleAsync();
            Assert.Equal(NotificationStatus.Pending, unchanged.Status);
            Assert.Equal(pending.RowVersion, unchanged.RowVersion);
        }
    }

    [Fact]
    public async Task ExpiredQueueClaimLoadsOneCandidateAndFencesOldLeaseCommit()
    {
        await ClearSmtpAsync();
        var now = DateTimeOffset.UtcNow;
        var expired = Enumerable.Range(0, 60).Select(index =>
        {
            var item = NewNotification(now.AddMinutes(-5));
            item.Claim(Guid.NewGuid(), "old-worker", now.AddMinutes(-2), now.AddSeconds(index - 60));
            return item;
        }).ToArray();
        var pending = NewNotification(now.AddHours(-1));
        await using (var db = database.CreateContext())
        {
            db.SmtpSettings.Add(NewSettings(true));
            db.SmtpRecipients.Add(NewRecipient());
            db.NotificationOutbox.AddRange(expired);
            db.NotificationOutbox.Add(pending);
            await db.SaveChangesAsync();
        }
        var first = expired[0];
        var oldWork = new NotificationSendWorkItem(first.Id, first.MutationId, first.Type,
            first.SourceKind, first.SourceId, first.TaskId, first.Stage, first.OccurredAtUtc,
            first.ErrorCode, first.SendLeaseToken!.Value, first.SendLeaseExpiresAtUtc!.Value, first.RowVersion);
        var counter = new ClaimReadCounter();
        var store = new NotificationOutboxStore(new InterceptingFactory(database, counter));

        var claimed = await ClaimAsync(store, false, first.Id, now);

        Assert.NotNull(claimed);
        Assert.Equal(first.Id, claimed.OutboxId);
        Assert.Equal(1, counter.MaterializedOutbox);
        Assert.Equal(1, counter.SettingsReads);
        Assert.Equal(1, counter.OutboxReads);
        Assert.Equal(0, counter.MaterializedSettings);
        await store.CommitSentAsync(oldWork, new NotificationSendCommitCommand(first.MutationId, now, null));
        await using (var db = database.CreateContext())
        {
            var current = await db.NotificationOutbox.SingleAsync(item => item.Id == first.Id);
            Assert.Equal(NotificationStatus.Sending, current.Status);
            Assert.Equal(claimed.LeaseToken, current.SendLeaseToken);
            Assert.Equal(1, current.SendAttemptCount);
            Assert.Equal(claimed.RowVersion, current.RowVersion);
            Assert.Equal(59, await db.NotificationOutbox.CountAsync(item => item.SendLeaseOwner == "old-worker"));
            Assert.Equal(NotificationStatus.Pending, (await db.NotificationOutbox.SingleAsync(item => item.Id == pending.Id)).Status);
            Assert.False(await db.AuditRecords.AnyAsync(item => item.Action == "notification.send.sent"
                && item.TargetId == first.Id.ToString("N")));
        }
        await store.CommitSentAsync(claimed, new NotificationSendCommitCommand(first.MutationId, now, null));
        await using (var db = database.CreateContext())
        {
            Assert.Equal(NotificationStatus.Sent, (await db.NotificationOutbox.SingleAsync(item => item.Id == first.Id)).Status);
        }
    }

    [Theory]
    [InlineData("pending", true)]
    [InlineData("due", true)]
    [InlineData("expired", true)]
    [InlineData("active", false)]
    [InlineData("backoff", false)]
    [InlineData("sent", false)]
    [InlineData("discarded", false)]
    public async Task BothClaimEntrypointsPreserveEligibilityAndAttemptCounts(string state, bool eligible)
    {
        await ClearSmtpAsync();
        var now = DateTimeOffset.UtcNow;
        var counter = new ClaimReadCounter();
        var store = new NotificationOutboxStore(new InterceptingFactory(database, counter));
        await using (var db = database.CreateContext())
        {
            db.SmtpSettings.Add(NewSettings(true));
            db.SmtpRecipients.Add(NewRecipient());
            await db.SaveChangesAsync();
        }
        foreach (var specified in new[] { false, true })
        {
            var item = NewNotification(now.AddMinutes(-5));
            var oldToken = Guid.NewGuid();
            if (state is "due" or "expired" or "active" or "backoff" or "sent")
                item.Claim(oldToken, "old-worker", now.AddMinutes(-4), state == "expired" ? now : now.AddMinutes(1));
            if (state is "due" or "backoff")
                item.RecordSendFailure(oldToken, now.AddMinutes(-3), "smtp_indeterminate", state == "due" ? now : now.AddMinutes(1));
            if (state == "sent") item.RecordSent(oldToken, now.AddMinutes(-2));
            if (state == "discarded") item.Discard(now.AddMinutes(-1), "outbox.discarded");
            await using (var db = database.CreateContext())
            {
                await db.NotificationOutbox.ExecuteDeleteAsync();
                db.NotificationOutbox.Add(item);
                await db.SaveChangesAsync();
            }
            counter.Reset();

            var claimed = await ClaimAsync(store, specified, item.Id, now);

            Assert.Equal(eligible, claimed is not null);
            Assert.Equal(1, counter.SettingsReads);
            Assert.Equal(0, counter.MaterializedSettings);
            if (specified || eligible) Assert.Equal(1, counter.MaterializedOutbox);
            await using var check = database.CreateContext();
            var current = await check.NotificationOutbox.SingleAsync();
            if (eligible)
            {
                Assert.Equal(NotificationStatus.Sending, current.Status);
                Assert.Equal(claimed!.RowVersion, current.RowVersion);
                Assert.Equal(claimed.LeaseToken, current.SendLeaseToken);
                Assert.Equal(state == "due" ? 2 : 1, current.SendAttemptCount);
            }
            else
            {
                Assert.Equal(item.RowVersion, current.RowVersion);
                Assert.Equal(item.Status, current.Status);
                Assert.Equal(item.SendLeaseToken, current.SendLeaseToken);
            }
        }
    }

    [Fact]
    public async Task SenderRechecksDisabledConfigurationAfterClaimAndReleasesToPending()
    {
        await ClearSmtpAsync();
        var now = DateTimeOffset.UtcNow;
        var pending = NewNotification(now.AddMinutes(-5));
        await using (var db = database.CreateContext())
        {
            db.SmtpSettings.Add(NewSettings(true));
            db.SmtpRecipients.Add(NewRecipient());
            db.NotificationOutbox.Add(pending);
            await db.SaveChangesAsync();
        }
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var store = new NotificationOutboxStore(factory);
        var claimed = await ClaimAsync(store, true, pending.Id, now);
        Assert.NotNull(claimed);
        await using (var db = database.CreateContext())
        {
            (await db.SmtpSettings.AsTracking().SingleAsync()).SetEnabled(false);
            await db.SaveChangesAsync();
        }
        var runner = new NotificationDeliveryRunner(store,
            new MailKitSmtpMailSender(factory, new ConfigurationBuilder().Build()),
            TimeProvider.System, NotificationDeliveryOptions.Default);

        await runner.ProcessClaimedAsync(claimed);

        await using var check = database.CreateContext();
        Assert.Equal(NotificationStatus.Pending, (await check.NotificationOutbox.SingleAsync()).Status);
        Assert.True(await check.AuditRecords.AnyAsync(item => item.Action == "notification.send.release"
            && item.TargetId == pending.Id.ToString("N")));
    }

    [Fact]
    public async Task ConcurrentQueueAndSpecifiedClaimHaveOnlyOneLeaseOwner()
    {
        await ClearSmtpAsync();
        var now = DateTimeOffset.UtcNow;
        var pending = NewNotification(now.AddMinutes(-5));
        await using (var db = database.CreateContext())
        {
            db.SmtpSettings.Add(NewSettings(true));
            db.SmtpRecipients.Add(NewRecipient());
            db.NotificationOutbox.Add(pending);
            await db.SaveChangesAsync();
        }
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();

        var claims = await Task.WhenAll(
            ClaimAsync(new NotificationOutboxStore(factory), false, pending.Id, now),
            ClaimAsync(new NotificationOutboxStore(factory), true, pending.Id, now));

        var claimed = Assert.Single(claims, item => item is not null)!;
        await using var check = database.CreateContext();
        var current = await check.NotificationOutbox.SingleAsync();
        Assert.Equal(NotificationStatus.Sending, current.Status);
        Assert.Equal(claimed.LeaseToken, current.SendLeaseToken);
        Assert.Equal(claimed.RowVersion, current.RowVersion);
        Assert.Equal(1, current.SendAttemptCount);
    }

    private static Task<NotificationSendWorkItem?> ClaimAsync(
        NotificationOutboxStore store, bool specified, Guid id, DateTimeOffset now)
    {
        var command = new ClaimNotificationSendCommand(Guid.NewGuid(), "new-worker", now, now.AddMinutes(1));
        return specified ? store.ClaimOutboxAsync(id, command) : store.ClaimNextAsync(command);
    }

    private static NotificationOutbox NewNotification(DateTimeOffset occurred) => new(
        Guid.NewGuid(), Guid.NewGuid(), NotificationType.AdminTest, NotificationSourceKind.AdminRequest,
        Guid.NewGuid(), null, null, occurred, "smtp.test");

    private static SmtpSettings NewSettings(bool enabled) => new(
        "smtp.example.test", 587, SmtpSecurityMode.StartTls, "ops@example.test", 30, enabled);

    private static SmtpRecipient NewRecipient() => new(Guid.NewGuid(), SmtpSettings.SingletonId, "ops@example.test");

    private sealed class InterceptingFactory(PlatformDatabaseSqlServerFixture database, IInterceptor interceptor)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => database.CreateContext(interceptor);
        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class ClaimReadCounter : DbCommandInterceptor, IMaterializationInterceptor
    {
        public int SettingsReads;
        public int OutboxReads;
        public int MaterializedSettings;
        public int MaterializedOutbox;
        public void Reset() => (SettingsReads, OutboxReads, MaterializedSettings, MaterializedOutbox) = (0, 0, 0, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.Ordinal))
            {
                if (command.CommandText.Contains("[SmtpSettings]", StringComparison.Ordinal)
                    || command.CommandText.Contains("[SmtpRecipients]", StringComparison.Ordinal)) SettingsReads++;
                if (command.CommandText.Contains("[NotificationOutbox]", StringComparison.Ordinal)) OutboxReads++;
            }
            return ValueTask.FromResult(result);
        }

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is SmtpSettings) MaterializedSettings++;
            if (entity is NotificationOutbox) MaterializedOutbox++;
            return entity;
        }
    }

    private SmtpSettingsService Service(IServiceProvider provider) => new(
        provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BusinessCredentialDataProtector.KeyRingPathConfigurationKey] = _keyPath,
        }).Build());

    private async Task ClearSmtpAsync()
    {
        await using var context = database.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM [NotificationOutbox];
            DELETE FROM [SmtpRecipients];
            UPDATE [SmtpSettings] SET [CredentialReferenceId] = NULL;
            DELETE FROM [SmtpSettings];
            DELETE FROM [CredentialReferences] WHERE [Kind] = N'SmtpPassword';
            """);
    }

    private async Task<AdminSession> CreateActorAsync()
    {
        var id = Guid.NewGuid();
        var actor = new AdminSession(id, $"admin-{id:N}", $"stamp-{id:N}");
        await using var context = database.CreateContext();
        context.AdminUsers.Add(new AdminUser(
            id, actor.Username, actor.Username.ToUpperInvariant(), "synthetic-hash", actor.SecurityStamp));
        await context.SaveChangesAsync();
        return actor;
    }

    public void Dispose()
    {
        var path = Path.GetFullPath(_keyPath);
        if (!string.Equals(
                Path.GetDirectoryName(path),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("拒绝清理非测试密钥目录。");
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class ImmediateSender : ISmtpMailSender
    {
        public Task<SmtpSendResult> SendAsync(
            NotificationSendWorkItem work,
            string subject,
            string body,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SmtpSendResult.Succeeded());
    }
}
