using System.Data;
using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class AdminIdentityStore(IDbContextFactory<PlatformDbContext> contextFactory)
    : IAdminIdentityStore
{
    private const string FirstAdminSetupLockName = "DbBackupManager:FirstAdminSetup";

    public async Task<bool> IsSetupRequiredAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return !await context.AdminUsers.AnyAsync(cancellationToken);
    }

    public async Task<AdminStoreResult> TryCreateFirstAdminAsync(
        NewAdminCredential credential,
        CancellationToken cancellationToken = default)
    {
        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var lockResult = await AcquireFirstAdminSetupLockAsync(context, cancellationToken);

            if (lockResult < 0)
            {
                return AdminStoreResult.ConcurrencyConflict;
            }

            if (await context.AdminUsers.AnyAsync(
                    user => user.Id == credential.Id,
                    cancellationToken))
            {
                return AdminStoreResult.Succeeded;
            }

            if (await context.AdminUsers.AnyAsync(cancellationToken))
            {
                return AdminStoreResult.SetupUnavailable;
            }

            var user = new AdminUser(
                credential.Id,
                credential.Username,
                credential.NormalizedUsername,
                credential.PasswordHash,
                credential.SecurityStamp);
            context.AdminUsers.Add(user);
            context.AuditRecords.Add(CreateAudit(
                user.Id,
                "auth.setup",
                "succeeded",
                null));

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AdminStoreResult.Succeeded;
        });
    }

    public async Task<AdminCredentialSnapshot?> FindByNormalizedUsernameAsync(
        string normalizedUsername,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await context.AdminUsers.SingleOrDefaultAsync(
            item => item.NormalizedUsername == normalizedUsername,
            cancellationToken);
        return user is null ? null : CreateSnapshot(user);
    }

    public async Task<AdminCredentialSnapshot?> FindByIdAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await context.AdminUsers.SingleOrDefaultAsync(
            item => item.Id == adminUserId,
            cancellationToken);
        return user is null ? null : CreateSnapshot(user);
    }

    public async Task<AdminStoreResult> RecordFailedLoginAsync(
        AdminCredentialSnapshot credential,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await context.AdminUsers.AsTracking().SingleOrDefaultAsync(
            item => item.Id == credential.Id,
            cancellationToken);

        if (user is null)
        {
            return AdminStoreResult.NotFound;
        }

        if (!user.RowVersion.SequenceEqual(credential.RowVersion))
        {
            return AdminStoreResult.ConcurrencyConflict;
        }

        user.RecordFailedLogin(
            utcNow,
            AdminCredentialPolicy.LoginFailureWindow,
            AdminCredentialPolicy.LoginFailureThreshold,
            AdminCredentialPolicy.LockoutDuration);
        context.AuditRecords.Add(CreateAudit(
            user.Id,
            "auth.login",
            "failed",
            "invalid_credentials"));

        return await SaveUpdateAsync(context, cancellationToken);
    }

    public async Task<AdminStoreResult> RecordSuccessfulLoginAsync(
        AdminCredentialSnapshot credential,
        string? replacementPasswordHash,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await context.AdminUsers.AsTracking().SingleOrDefaultAsync(
            item => item.Id == credential.Id,
            cancellationToken);

        if (user is null)
        {
            return AdminStoreResult.NotFound;
        }

        if (!user.RowVersion.SequenceEqual(credential.RowVersion))
        {
            return AdminStoreResult.ConcurrencyConflict;
        }

        user.RecordSuccessfulLogin(replacementPasswordHash);
        context.AuditRecords.Add(CreateAudit(user.Id, "auth.login", "succeeded", null));
        return await SaveUpdateAsync(context, cancellationToken);
    }

    public async Task<AdminStoreResult> ChangePasswordAsync(
        AdminCredentialSnapshot credential,
        string passwordHash,
        string securityStamp,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await context.AdminUsers.AsTracking().SingleOrDefaultAsync(
            item => item.Id == credential.Id,
            cancellationToken);

        if (user is null)
        {
            return AdminStoreResult.NotFound;
        }

        if (!user.RowVersion.SequenceEqual(credential.RowVersion))
        {
            return AdminStoreResult.ConcurrencyConflict;
        }

        user.ChangePassword(passwordHash, securityStamp);
        context.AuditRecords.Add(CreateAudit(
            user.Id,
            "auth.password.change",
            "succeeded",
            null));
        return await SaveUpdateAsync(context, cancellationToken);
    }

    public async Task RecordRejectedAuthenticationAsync(
        Guid? actorAdminUserId,
        string action,
        string reasonCode,
        CancellationToken cancellationToken = default)
    {
        if (action is not ("auth.login" or "auth.password.change"))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        ArgumentOutOfRangeException.ThrowIfNotEqual(reasonCode, "invalid_credentials");

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.AuditRecords.Add(CreateAudit(
            actorAdminUserId,
            action,
            "failed",
            reasonCode));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordCsrfFailureAsync(
        Guid? actorAdminUserId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.AuditRecords.Add(new AuditRecord(
            actorAdminUserId,
            "security.csrf",
            "HttpRequest",
            null,
            "failed",
            "invalid_csrf_token"));
        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task<int> AcquireFirstAdminSetupLockAsync(
        PlatformDbContext context,
        CancellationToken cancellationToken)
    {
        var result = new SqlParameter
        {
            ParameterName = "@result",
            SqlDbType = SqlDbType.Int,
            Direction = ParameterDirection.Output,
        };
        var resource = new SqlParameter("@resource", SqlDbType.NVarChar, 255)
        {
            Value = FirstAdminSetupLockName,
        };

        await context.Database.ExecuteSqlRawAsync(
            "EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', "
            + "@LockOwner = 'Transaction', @LockTimeout = 5000",
            [result, resource],
            cancellationToken);

        return (int)result.Value;
    }

    private static async Task<AdminStoreResult> SaveUpdateAsync(
        PlatformDbContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return AdminStoreResult.Succeeded;
        }
        catch (DbUpdateConcurrencyException)
        {
            return AdminStoreResult.ConcurrencyConflict;
        }
    }

    private static AdminCredentialSnapshot CreateSnapshot(AdminUser user)
    {
        return new AdminCredentialSnapshot(
            user.Id,
            user.Username,
            user.NormalizedUsername,
            user.PasswordHash,
            user.SecurityStamp,
            user.IsEnabled,
            user.FailedLoginCount,
            user.FailedLoginWindowStartedAtUtc,
            user.LockoutEndUtc,
            [.. user.RowVersion]);
    }

    private static AuditRecord CreateAudit(
        Guid? actorAdminUserId,
        string action,
        string result,
        string? reasonCode)
    {
        return new AuditRecord(
            actorAdminUserId,
            action,
            "AdminUser",
            actorAdminUserId?.ToString("N"),
            result,
            reasonCode);
    }
}
