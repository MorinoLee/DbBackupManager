using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.SqlCredentials;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.SqlCredentials;

public static class SqlCredentialServiceCollectionExtensions
{
    public static IServiceCollection AddSqlCredentialManagement(this IServiceCollection services)
    {
        services.AddScoped<ISqlCredentialService, SqlCredentialService>();
        return services;
    }
}

internal sealed class SqlCredentialService(
    IDbContextFactory<PlatformDbContext> contextFactory,
    IConfiguration configuration) : ISqlCredentialService
{
    public async Task<SqlCredentialResult<IReadOnlyList<SqlCredentialItem>>> ListAsync(
        AdminSession actor, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            if (!await IsAuthorizedAsync(context, actor, cancellationToken))
            {
                return new(SqlCredentialResultCode.AuthenticationRequired);
            }

            var rows = await context.CredentialReferences.AsNoTracking()
                .Where(x => x.Kind == CredentialKind.SqlPassword)
                .OrderBy(x => x.Name)
                .Select(x => new { x.Id, x.Name, x.Username, x.IsEnabled, x.RowVersion })
                .ToListAsync(cancellationToken);
            return new(SqlCredentialResultCode.Succeeded, rows.Select(x => new SqlCredentialItem(
                x.Id, x.Name, x.Username, x.IsEnabled, Convert.ToBase64String(x.RowVersion))).ToArray());
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new(SqlCredentialResultCode.Unavailable);
        }
    }

    public Task<SqlCredentialResult<SqlCredentialItem>> CreateAsync(
        AdminSession actor, string name, string username, string password,
        CancellationToken cancellationToken = default)
    {
        if (!IsText(name, 200) || !IsText(username, 128) || !IsPassword(password))
        {
            return Task.FromResult(new SqlCredentialResult<SqlCredentialItem>(
                SqlCredentialResultCode.ValidationFailed));
        }

        var id = Guid.NewGuid();
        return SaveAsync(actor, id, null, password, "credential.create",
            _ => new CredentialReference(id, name, CredentialKind.SqlPassword,
                username, "pending", BusinessCredentialDataProtector.SqlPasswordProtectionVersion),
            cancellationToken);
    }

    public Task<SqlCredentialResult<SqlCredentialItem>> RotatePasswordAsync(
        AdminSession actor, Guid id, string version, string password,
        CancellationToken cancellationToken = default)
    {
        if (!IsPassword(password))
        {
            return Task.FromResult(new SqlCredentialResult<SqlCredentialItem>(
                SqlCredentialResultCode.ValidationFailed));
        }

        return SaveAsync(actor, id, version, password, "credential.rotate", x => x!, cancellationToken);
    }

    public Task<SqlCredentialResult<SqlCredentialItem>> SetEnabledAsync(
        AdminSession actor, Guid id, string version, bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        return SaveAsync(actor, id, version, null, isEnabled ? "credential.enable" : "credential.disable", x =>
        {
            x!.SetEnabled(isEnabled);
            return x;
        }, cancellationToken);
    }

    private async Task<SqlCredentialResult<SqlCredentialItem>> SaveAsync(
        AdminSession actor, Guid id, string? version, string? password, string action,
        Func<CredentialReference?, CredentialReference> change, CancellationToken cancellationToken)
    {
        var creating = action == "credential.create";
        byte[]? expectedVersion = null;
        if (!creating)
        {
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(version) || version.Length > 16)
            {
                return new(SqlCredentialResultCode.ValidationFailed);
            }

            try { expectedVersion = Convert.FromBase64String(version); }
            catch (FormatException) { return new(SqlCredentialResultCode.ValidationFailed); }
            if (expectedVersion.Length != 8) { return new(SqlCredentialResultCode.ValidationFailed); }
        }

        try
        {
            // 在加密和数据库写入前均复核身份，长期 Circuit 不能沿用失效身份。
            await using (var authorizationContext = await contextFactory.CreateDbContextAsync(cancellationToken))
            {
                if (!await IsAuthorizedAsync(authorizationContext, actor, cancellationToken))
                {
                    return new(SqlCredentialResultCode.AuthenticationRequired);
                }
            }

            string? protectedPassword = null;
            if (password is not null)
            {
                try
                {
                    var protector = new BusinessCredentialDataProtector(
                        configuration[BusinessCredentialDataProtector.KeyRingPathConfigurationKey]);
                    protectedPassword = protector.ProtectSqlPassword(password.AsSpan());
                }
                catch (Exception exception) when (exception is CryptographicException
                    or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return new(SqlCredentialResultCode.ProtectionUnavailable);
                }
            }

            await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
            return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken);
                if (!await IsAuthorizedAsync(context, actor, cancellationToken))
                {
                    return new SqlCredentialResult<SqlCredentialItem>(SqlCredentialResultCode.AuthenticationRequired);
                }

                CredentialReference? entity = null;
                if (!creating)
                {
                    entity = await context.CredentialReferences.AsTracking().SingleOrDefaultAsync(
                        x => x.Id == id && x.Kind == CredentialKind.SqlPassword, cancellationToken);
                    if (entity is null) { return new(SqlCredentialResultCode.NotFound); }
                    if (!entity.RowVersion.AsSpan().SequenceEqual(expectedVersion))
                    {
                        return new(SqlCredentialResultCode.Conflict);
                    }
                }

                entity = change(entity);
                if (protectedPassword is not null)
                {
                    entity.RotateProtectedValues(protectedPassword,
                        BusinessCredentialDataProtector.SqlPasswordProtectionVersion);
                }

                if (creating) { context.CredentialReferences.Add(entity); }
                context.AuditRecords.Add(new AuditRecord(actor.AdminUserId, action,
                    "CredentialReference", entity.Id.ToString("N"), "Succeeded", null));
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new SqlCredentialResult<SqlCredentialItem>(SqlCredentialResultCode.Succeeded,
                    new(entity.Id, entity.Name, entity.Username, entity.IsEnabled,
                        Convert.ToBase64String(entity.RowVersion)));
            });
        }
        catch (DbUpdateConcurrencyException) { return new(SqlCredentialResultCode.Conflict); }
        catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.SqlClient.SqlException
        { Number: 2601 or 2627 })
        { return new(SqlCredentialResultCode.Conflict); }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new(SqlCredentialResultCode.Unavailable);
        }
    }

    private static async Task<bool> IsAuthorizedAsync(
        PlatformDbContext context, AdminSession actor, CancellationToken cancellationToken)
    {
        var admin = await context.AdminUsers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == actor.AdminUserId, cancellationToken);
        return admin is { IsEnabled: true }
            && !admin.IsLockedOut(DateTimeOffset.UtcNow)
            && string.Equals(admin.SecurityStamp, actor.SecurityStamp, StringComparison.Ordinal);
    }

    private static bool IsText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximum && !value.Any(char.IsControl);

    private static bool IsPassword(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

    private static bool IsStorageFailure(Exception exception) =>
        exception is DbException or DbUpdateException or InvalidOperationException;
}
