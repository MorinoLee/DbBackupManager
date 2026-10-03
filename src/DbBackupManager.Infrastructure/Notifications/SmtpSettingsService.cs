using System.Data;
using System.Data.Common;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.Notifications;
using DbBackupManager.Application.Servers;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Domain.Notifications;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Notifications;

public static class SmtpNotificationRegistration
{
    public static IServiceCollection AddSmtpNotificationManagement(this IServiceCollection services)
    {
        services.AddScoped<ISmtpSettingsService, SmtpSettingsService>();
        services.AddScoped<ISmtpMailSender, MailKitSmtpMailSender>();
        services.AddScoped<INotificationOutboxStore, NotificationOutboxStore>();
        services.AddScoped<NotificationDeliveryRunner>();
        services.AddSingleton(NotificationDeliveryOptions.Default);
        return services;
    }
}

internal sealed class SmtpSettingsService(
    IDbContextFactory<PlatformDbContext> factory,
    IConfiguration configuration) : ISmtpSettingsService
{
    private const string SmtpCredentialName = "smtp-global";

    public Task<ManagementResult<SmtpSettingsItem>> GetAsync(
        AdminSession actor, CancellationToken cancellationToken = default) =>
        GuardAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await AuthorizeAsync(db, actor, cancellationToken);
            var entity = await db.SmtpSettings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == SmtpSettings.SingletonId, cancellationToken);
            if (entity is null)
            {
                return new ManagementResult<SmtpSettingsItem>(ManagementCode.Succeeded, Unconfigured());
            }

            return new ManagementResult<SmtpSettingsItem>(
                ManagementCode.Succeeded,
                await MapAsync(db, entity, cancellationToken));
        });

    public Task<ManagementResult<SmtpSettingsItem>> SaveAsync(
        AdminSession actor, string? version, SmtpSettingsInput input,
        CancellationToken cancellationToken = default) =>
        MutateAsync(actor, async db =>
        {
            RequireSafe(input.Host, input.FromAddress);
            if (!Enum.IsDefined((SmtpSecurityMode)input.SecurityMode))
            {
                Reject(ManagementCode.ValidationFailed);
            }

            var entity = await LoadTrackedAsync(db, cancellationToken);
            if (entity is null)
            {
                if (!string.IsNullOrWhiteSpace(version))
                {
                    Reject(ManagementCode.ValidationFailed);
                }

                entity = new SmtpSettings(
                    input.Host,
                    input.Port,
                    (SmtpSecurityMode)input.SecurityMode,
                    input.FromAddress,
                    input.TimeoutSeconds,
                    input.IsEnabled);
                db.SmtpSettings.Add(entity);
                Audit(db, actor, "smtp.settings.create", entity.Id, PlaintextReason(input.SecurityMode));
            }
            else
            {
                CheckVersion(entity, version);
                entity.Replace(
                    input.Host,
                    input.Port,
                    (SmtpSecurityMode)input.SecurityMode,
                    input.FromAddress,
                    input.TimeoutSeconds,
                    input.IsEnabled);
                Audit(db, actor, "smtp.settings.update", entity.Id, PlaintextReason(input.SecurityMode));
            }

            await db.SaveChangesAsync(cancellationToken);
            return await MapAsync(db, entity, cancellationToken);
        }, cancellationToken);

    public Task<ManagementResult<SmtpSettingsItem>> SetEnabledAsync(
        AdminSession actor, string version, bool isEnabled,
        CancellationToken cancellationToken = default) =>
        MutateAsync(actor, async db =>
        {
            var entity = await RequireSettingsAsync(db, version, cancellationToken);
            entity.SetEnabled(isEnabled);
            Audit(db, actor, isEnabled ? "smtp.settings.enable" : "smtp.settings.disable", entity.Id);
            await db.SaveChangesAsync(cancellationToken);
            return await MapAsync(db, entity, cancellationToken);
        }, cancellationToken);

    public Task<ManagementResult<SmtpSettingsItem>> RotatePasswordAsync(
        AdminSession actor, string version, string username, string password,
        CancellationToken cancellationToken = default) =>
        MutateAsync(actor, async db =>
        {
            RequireSafe(username);
            if (string.IsNullOrWhiteSpace(password) || password.Length > 1024 || password.Any(char.IsControl))
            {
                Reject(ManagementCode.ValidationFailed);
            }

            var entity = await RequireSettingsAsync(db, version, cancellationToken);
            var protector = new BusinessCredentialDataProtector(
                configuration[BusinessCredentialDataProtector.KeyRingPathConfigurationKey],
                purpose: BusinessCredentialDataProtector.SmtpPasswordPurpose);
            string protectedSecret;
            try
            {
                protectedSecret = protector.ProtectSmtpPassword(password);
            }
            catch (ArgumentException)
            {
                throw new Rejected(ManagementCode.ValidationFailed);
            }
            catch (InvalidOperationException)
            {
                throw new Rejected(ManagementCode.Unavailable);
            }

            if (entity.CredentialReferenceId is { } credentialId)
            {
                var credential = await db.CredentialReferences.AsTracking()
                    .SingleOrDefaultAsync(x => x.Id == credentialId, cancellationToken)
                    ?? throw new Rejected(ManagementCode.NotFound);
                if (credential.Kind != CredentialKind.SmtpPassword)
                {
                    Reject(ManagementCode.ValidationFailed);
                }

                credential.RotateProtectedValues(
                    protectedSecret,
                    BusinessCredentialDataProtector.SmtpPasswordProtectionVersion);
                credential.SetEnabled(true);
            }
            else
            {
                var credential = new CredentialReference(
                    Guid.NewGuid(),
                    SmtpCredentialName,
                    CredentialKind.SmtpPassword,
                    username.Trim(),
                    protectedSecret,
                    BusinessCredentialDataProtector.SmtpPasswordProtectionVersion);
                db.CredentialReferences.Add(credential);
                entity.AssignCredential(credential.Id);
            }

            Audit(db, actor, "smtp.password.rotate", entity.Id);
            await db.SaveChangesAsync(cancellationToken);
            return await MapAsync(db, entity, cancellationToken);
        }, cancellationToken);

    public Task<ManagementResult<SmtpSettingsItem>> ClearPasswordAsync(
        AdminSession actor, string version, CancellationToken cancellationToken = default) =>
        MutateAsync(actor, async db =>
        {
            var entity = await RequireSettingsAsync(db, version, cancellationToken);
            if (entity.CredentialReferenceId is { } credentialId)
            {
                var credential = await db.CredentialReferences.AsTracking()
                    .SingleOrDefaultAsync(x => x.Id == credentialId, cancellationToken);
                credential?.SetEnabled(false);
            }

            entity.ClearCredential();
            Audit(db, actor, "smtp.password.clear", entity.Id);
            await db.SaveChangesAsync(cancellationToken);
            return await MapAsync(db, entity, cancellationToken);
        }, cancellationToken);

    public Task<ManagementResult<SmtpSettingsItem>> AddRecipientAsync(
        AdminSession actor, string version, string address,
        CancellationToken cancellationToken = default) =>
        MutateAsync(actor, async db =>
        {
            RequireSafe(address);
            var entity = await RequireSettingsAsync(db, version, cancellationToken);
            var recipient = new SmtpRecipient(Guid.NewGuid(), entity.Id, address);
            db.SmtpRecipients.Add(recipient);
            entity.MarkRecipientsChanged();
            Audit(db, actor, "smtp.recipient.add", recipient.Id);
            await db.SaveChangesAsync(cancellationToken);
            return await MapAsync(db, entity, cancellationToken);
        }, cancellationToken);

    public Task<ManagementResult<SmtpSettingsItem>> RemoveRecipientAsync(
        AdminSession actor, string version, Guid recipientId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(actor, async db =>
        {
            var entity = await RequireSettingsAsync(db, version, cancellationToken);
            var recipient = await db.SmtpRecipients.AsTracking()
                .SingleOrDefaultAsync(
                    x => x.Id == recipientId && x.SmtpSettingsId == entity.Id,
                    cancellationToken)
                ?? throw new Rejected(ManagementCode.NotFound);
            db.SmtpRecipients.Remove(recipient);
            entity.MarkRecipientsChanged();
            Audit(db, actor, "smtp.recipient.remove", recipientId);
            await db.SaveChangesAsync(cancellationToken);
            return await MapAsync(db, entity, cancellationToken);
        }, cancellationToken);

    public Task<ManagementResult<SmtpSettingsItem>> QueueTestAsync(
        AdminSession actor, string version, Guid requestId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(actor, async db =>
        {
            if (requestId == Guid.Empty)
            {
                Reject(ManagementCode.ValidationFailed);
            }

            var entity = await RequireSettingsAsync(db, version, cancellationToken);
            if (!entity.IsEnabled)
            {
                Reject(ManagementCode.ValidationFailed);
            }

            var hasRecipient = await db.SmtpRecipients.AnyAsync(
                x => x.SmtpSettingsId == entity.Id, cancellationToken);
            if (!hasRecipient)
            {
                Reject(ManagementCode.ValidationFailed);
            }

            var existing = await db.NotificationOutbox.AsNoTracking().AnyAsync(
                x => x.MutationId == requestId && x.Type == NotificationType.AdminTest,
                cancellationToken);
            if (!existing)
            {
                NotificationOutboxWriter.TryAdd(
                    db,
                    requestId,
                    NotificationType.AdminTest,
                    NotificationSourceKind.AdminRequest,
                    requestId,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    "smtp.test");
            }

            Audit(db, actor, "smtp.test.enqueue", requestId);
            await db.SaveChangesAsync(cancellationToken);
            return await MapAsync(db, entity, cancellationToken);
        }, cancellationToken);

    private static async Task<SmtpSettings> RequireSettingsAsync(
        PlatformDbContext db, string version, CancellationToken cancellationToken)
    {
        var entity = await LoadTrackedAsync(db, cancellationToken)
            ?? throw new Rejected(ManagementCode.NotFound);
        CheckVersion(entity, version);
        return entity;
    }

    private static Task<SmtpSettings?> LoadTrackedAsync(
        PlatformDbContext db, CancellationToken cancellationToken) =>
        db.SmtpSettings.AsTracking()
            .SingleOrDefaultAsync(x => x.Id == SmtpSettings.SingletonId, cancellationToken);

    private static async Task<SmtpSettingsItem> MapAsync(
        PlatformDbContext db, SmtpSettings entity, CancellationToken cancellationToken)
    {
        var recipients = await db.SmtpRecipients.AsNoTracking()
            .Where(x => x.SmtpSettingsId == entity.Id)
            .OrderBy(x => x.NormalizedAddress)
            .Select(x => new SmtpRecipientItem(x.Id, x.Address))
            .ToArrayAsync(cancellationToken);
        return new SmtpSettingsItem(
            entity.Id,
            entity.Host,
            entity.Port,
            (int)entity.SecurityMode,
            entity.FromAddress,
            entity.TimeoutSeconds,
            entity.IsEnabled,
            entity.CredentialReferenceId is not null,
            recipients,
            Convert.ToBase64String(entity.RowVersion));
    }

    private static SmtpSettingsItem Unconfigured() => new(
        SmtpSettings.SingletonId,
        string.Empty,
        587,
        (int)SmtpSecurityMode.StartTls,
        string.Empty,
        30,
        false,
        false,
        [],
        string.Empty);

    private async Task<ManagementResult<T>> MutateAsync<T>(
        AdminSession actor, Func<PlatformDbContext, Task<T>> action, CancellationToken cancellationToken) =>
        await GuardAsync(async () =>
        {
            await using var strategy = await factory.CreateDbContextAsync(cancellationToken);
            return await strategy.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                await using var tx = await db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken);
                await AuthorizeAsync(db, actor, cancellationToken);
                var result = await action(db);
                await tx.CommitAsync(cancellationToken);
                return new ManagementResult<T>(ManagementCode.Succeeded, result);
            });
        });

    private static async Task<ManagementResult<T>> GuardAsync<T>(Func<Task<ManagementResult<T>>> action)
    {
        try
        {
            return await action();
        }
        catch (Rejected e)
        {
            return new(e.Code);
        }
        catch (ArgumentException)
        {
            return new(ManagementCode.ValidationFailed);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(ManagementCode.Conflict);
        }
        catch (DbUpdateException e) when (e.InnerException is Microsoft.Data.SqlClient.SqlException
        {
            Number: 2601 or 2627
        })
        {
            return new(ManagementCode.Conflict);
        }
        catch (Exception e) when (e is DbException or DbUpdateException or InvalidOperationException)
        {
            return new(ManagementCode.Unavailable);
        }
    }

    private static async Task AuthorizeAsync(PlatformDbContext db, AdminSession actor, CancellationToken token)
    {
        var admin = await db.AdminUsers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == actor.AdminUserId, token);
        if (admin is not { IsEnabled: true } || admin.IsLockedOut(DateTimeOffset.UtcNow)
            || !string.Equals(admin.SecurityStamp, actor.SecurityStamp, StringComparison.Ordinal))
        {
            Reject(ManagementCode.AuthenticationRequired);
        }
    }

    private static void RequireSafe(params string?[] values)
    {
        if (values.Any(x => x?.Any(char.IsControl) == true))
        {
            Reject(ManagementCode.ValidationFailed);
        }
    }

    private static void CheckVersion(ConcurrentEntity entity, string? version)
    {
        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(version is { Length: <= 16 } ? version : "");
        }
        catch (FormatException)
        {
            throw new Rejected(ManagementCode.ValidationFailed);
        }

        if (expected.Length != 8)
        {
            Reject(ManagementCode.ValidationFailed);
        }

        if (!entity.RowVersion.AsSpan().SequenceEqual(expected))
        {
            Reject(ManagementCode.Conflict);
        }
    }

    private static string? PlaintextReason(int securityMode) =>
        securityMode == (int)SmtpSecurityMode.Plaintext ? "smtp.plaintext" : null;

    private static void Audit(
        PlatformDbContext db,
        AdminSession actor,
        string action,
        Guid id,
        string? reasonCode = null) =>
        db.AuditRecords.Add(new AuditRecord(
            actor.AdminUserId, action, "SmtpSettings", id.ToString("N"), "Succeeded", reasonCode));

    private static void Reject(ManagementCode code) => throw new Rejected(code);

    private sealed class Rejected(ManagementCode code) : Exception
    {
        public ManagementCode Code { get; } = code;
    }
}
