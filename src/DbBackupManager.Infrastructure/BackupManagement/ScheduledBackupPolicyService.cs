using System.Data;
using System.Data.Common;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.BackupManagement;

internal sealed class ScheduledBackupPolicyService(
    IDbContextFactory<PlatformDbContext> factory,
    TimeProvider clock) : IScheduledBackupPolicyService
{
    public Task<BackupManagementResult<ScheduledBackupDashboard>> ListAsync(
        AdminSession actor,
        CancellationToken token = default) =>
        Guard(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await Authorize(db, actor, token);
            var choices = await DatabaseChoices(db).ToListAsync(token);
            var policies = await db.BackupPolicies.AsNoTracking()
                .Where(policy => !policy.IsManualOnly)
                .OrderBy(policy => policy.Name)
                .ToListAsync(token);
            var nowUtc = clock.GetUtcNow().ToUniversalTime();
            return new BackupManagementResult<ScheduledBackupDashboard>(
                BackupManagementCode.Succeeded,
                new(choices, policies.Select(policy => Map(policy, nowUtc)).ToArray()));
        });

    public Task<BackupManagementResult<ScheduledBackupPolicy>> GetAsync(
        AdminSession actor,
        Guid id,
        CancellationToken token = default) =>
        Guard(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await Authorize(db, actor, token);
            var policy = await db.BackupPolicies.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == id && !item.IsManualOnly, token)
                ?? throw new Rejected(BackupManagementCode.NotFound);
            return new BackupManagementResult<ScheduledBackupPolicy>(
                BackupManagementCode.Succeeded,
                Map(policy, clock.GetUtcNow().ToUniversalTime()));
        });

    public Task<BackupManagementResult<ScheduledBackupPolicy>> SaveAsync(
        AdminSession actor,
        Guid? id,
        string? version,
        ScheduledBackupPolicyInput input,
        CancellationToken token = default) =>
        Guard(async () =>
        {
            await using var strategyDb = await factory.CreateDbContextAsync(token);
            return await strategyDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var db = await factory.CreateDbContextAsync(token);
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
                await Authorize(db, actor, token);
                if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Any(char.IsControl))
                {
                    throw new Rejected(BackupManagementCode.Invalid);
                }

                if (!Enum.IsDefined(typeof(BackupScheduleType), input.ScheduleType)
                    || !Enum.IsDefined((BackupStorageMode)input.StorageMode))
                {
                    throw new Rejected(BackupManagementCode.Invalid);
                }

                var valid = await (from d in db.ManagedDatabases
                                   join i in db.DatabaseInstances on d.InstanceId equals i.Id
                                   join s in db.DatabaseServers on i.ServerId equals s.Id
                                   where d.Id == input.DatabaseId && !d.IsSystemDatabase
                                       && (!input.IsEnabled || d.IsManaged && d.IsAvailable && i.IsEnabled
                                           && s.IsEnabled && i.ConnectionStatus == SqlConnectionStatus.Connected)
                                   select d.Id).AnyAsync(token);
                if (!valid)
                {
                    throw new Rejected(BackupManagementCode.Invalid);
                }

                var mode = (BackupStorageMode)input.StorageMode;
                if (mode != BackupStorageMode.LocalOnly)
                {
                    if (input.StorageTargetId is null
                        || input.RemoteRetentionDays is null
                        || input.TransferTimeoutMinutes is < 1 or > 1440)
                    {
                        throw new Rejected(BackupManagementCode.Invalid);
                    }

                    var targetOk = await (from t in db.StorageTargets
                                          join c in db.CredentialReferences on t.CredentialReferenceId equals c.Id
                                          where t.Id == input.StorageTargetId
                                              && (!input.IsEnabled || t.IsEnabled && c.IsEnabled)
                                          select t.Id).AnyAsync(token);
                    if (!targetOk)
                    {
                        throw new Rejected(BackupManagementCode.Invalid);
                    }
                }
                else if (input.StorageTargetId is not null || input.RemoteRetentionDays is not null)
                {
                    throw new Rejected(BackupManagementCode.Invalid);
                }

                var settings = new BackupPolicySettings(
                    mode,
                    input.StorageTargetId,
                    (BackupScheduleType)input.ScheduleType,
                    input.LocalTime,
                    (BackupWeekdays)input.DaysOfWeek,
                    input.TimeZoneId,
                    mode == BackupStorageMode.RemoteOnly ? null : input.LocalRetentionDays,
                    mode == BackupStorageMode.LocalOnly ? null : input.RemoteRetentionDays,
                    true,
                    input.UseCompression,
                    true,
                    input.BackupTimeoutMinutes,
                    input.VerifyTimeoutMinutes,
                    input.TransferTimeoutMinutes);
                var nowUtc = clock.GetUtcNow().ToUniversalTime();
                BackupPolicy policy;
                if (id is null)
                {
                    policy = new BackupPolicy(
                        Guid.NewGuid(),
                        input.Name,
                        input.DatabaseId,
                        settings,
                        input.IsEnabled,
                        isManualOnly: false,
                        nowUtc);
                    db.BackupPolicies.Add(policy);
                }
                else
                {
                    policy = await db.BackupPolicies.AsTracking()
                            .SingleOrDefaultAsync(item => item.Id == id && !item.IsManualOnly, token)
                        ?? throw new Rejected(BackupManagementCode.NotFound);
                    if (version != Convert.ToBase64String(policy.RowVersion))
                    {
                        throw new Rejected(BackupManagementCode.Conflict);
                    }

                    if (policy.DatabaseId != input.DatabaseId)
                    {
                        throw new Rejected(BackupManagementCode.Invalid);
                    }

                    policy.Update(input.Name, settings, nowUtc);
                    policy.SetEnabled(input.IsEnabled, nowUtc);
                }

                db.AuditRecords.Add(new(
                    actor.AdminUserId,
                    "backup.scheduled_policy.save",
                    "BackupPolicy",
                    policy.Id.ToString("N"),
                    "Succeeded",
                    null));
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
                return new BackupManagementResult<ScheduledBackupPolicy>(
                    BackupManagementCode.Succeeded,
                    Map(policy, nowUtc));
            });
        });

    private static IQueryable<BackupDatabaseChoice> DatabaseChoices(PlatformDbContext db) =>
        from d in db.ManagedDatabases
        join i in db.DatabaseInstances on d.InstanceId equals i.Id
        join s in db.DatabaseServers on i.ServerId equals s.Id
        where d.IsManaged && d.IsAvailable && !d.IsSystemDatabase && i.IsEnabled && s.IsEnabled
            && i.ConnectionStatus == SqlConnectionStatus.Connected
        orderby s.Name, i.Name, d.DatabaseName
        select new BackupDatabaseChoice(d.Id, s.Name + " / " + i.Name + " / " + d.DatabaseName);

    private static ScheduledBackupPolicy Map(BackupPolicy policy, DateTimeOffset nowUtc)
    {
        DateTimeOffset? nextUtc = null;
        DateTime? nextLocal = null;
        if (policy is { IsEnabled: true, ScheduleEffectiveFromUtc: not null })
        {
            var calculation = BackupScheduleCalculator.Calculate(new BackupScheduleRequest(
                policy.ScheduleType,
                policy.LocalTime,
                policy.DaysOfWeek,
                policy.TimeZoneId,
                policy.ScheduleEffectiveFromUtc.Value,
                nowUtc));
            if (calculation.Status == BackupScheduleCalculationStatus.Succeeded
                && calculation.NextFutureSlotUtc is { } slot)
            {
                nextUtc = slot.ToUniversalTime();
                try
                {
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId);
                    nextLocal = TimeZoneInfo.ConvertTimeFromUtc(nextUtc.Value.UtcDateTime, zone);
                }
                catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    nextUtc = null;
                    nextLocal = null;
                }
            }
        }

        return new ScheduledBackupPolicy(
            policy.Id,
            new ScheduledBackupPolicyInput(
                policy.DatabaseId,
                policy.Name,
                (int)policy.ScheduleType,
                policy.LocalTime,
                (int)policy.DaysOfWeek,
                policy.TimeZoneId,
                (int)policy.StorageMode,
                policy.StorageTargetId,
                policy.LocalRetentionDays,
                policy.RemoteRetentionDays,
                policy.BackupTimeoutMinutes,
                policy.VerifyTimeoutMinutes,
                policy.TransferTimeoutMinutes,
                policy.UseCompression,
                policy.IsEnabled),
            Convert.ToBase64String(policy.RowVersion),
            policy.ScheduleEffectiveFromUtc,
            nextUtc,
            nextLocal,
            nextUtc is null ? null : policy.TimeZoneId);
    }

    private static async Task Authorize(PlatformDbContext db, AdminSession actor, CancellationToken token)
    {
        var admin = await db.AdminUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actor.AdminUserId, token);
        if (admin is not { IsEnabled: true } || admin.IsLockedOut(DateTimeOffset.UtcNow)
            || !string.Equals(admin.SecurityStamp, actor.SecurityStamp, StringComparison.Ordinal))
        {
            throw new Rejected(BackupManagementCode.AuthenticationRequired);
        }
    }

    private static async Task<BackupManagementResult<T>> Guard<T>(Func<Task<BackupManagementResult<T>>> action)
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
            return new(BackupManagementCode.Invalid);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(BackupManagementCode.Conflict);
        }
        catch (DbUpdateException e) when (e.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            return new(BackupManagementCode.Conflict);
        }
        catch (Exception e) when (e is DbException or DbUpdateException or InvalidOperationException)
        {
            return new(BackupManagementCode.Unavailable);
        }
    }

    private sealed class Rejected(BackupManagementCode code) : Exception
    {
        public BackupManagementCode Code { get; } = code;
    }
}
