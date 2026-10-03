using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using DbBackupManager.Application.FileCredentials;
using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.FileCredentials;

public static class FileCredentialServiceCollectionExtensions
{
    public static IServiceCollection AddFileCredentialManagement(this IServiceCollection services)
    {
        services.AddScoped<IFileCredentialService, FileCredentialService>();
        return services;
    }
}

internal sealed class FileCredentialService(
    IDbContextFactory<PlatformDbContext> contextFactory,
    IConfiguration configuration) : IFileCredentialService
{
    public async Task<FileCredentialResult<IReadOnlyList<FileCredentialItem>>> ListAsync(
        AdminSession actor, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            if (!await IsAuthorizedAsync(context, actor, cancellationToken))
            {
                return new(FileCredentialResultCode.AuthenticationRequired);
            }

            var rows = await context.CredentialReferences.AsNoTracking()
                .Where(x => x.Kind == CredentialKind.SmbPassword
                    || x.Kind == CredentialKind.SftpPassword
                    || x.Kind == CredentialKind.SftpPrivateKey)
                .OrderBy(x => x.Name)
                .Select(x => new { x.Id, x.Name, x.Username, x.Kind, x.IsEnabled, x.RowVersion, x.ProtectedSecondarySecret })
                .ToListAsync(cancellationToken);
            return new(FileCredentialResultCode.Succeeded, rows.Select(x => new FileCredentialItem(
                x.Id, x.Name, x.Username, (FileCredentialKind)x.Kind, x.IsEnabled, Convert.ToBase64String(x.RowVersion),
                x.ProtectedSecondarySecret is not null)).ToArray());
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new(FileCredentialResultCode.Unavailable);
        }
    }

    public Task<FileCredentialResult<FileCredentialItem>> CreateAsync(
        AdminSession actor, string name, string username, FileCredentialKind kind, string password,
        CancellationToken cancellationToken = default)
    {
        if (!IsText(name, 200) || !IsText(username, 256) || !IsPasswordKind(kind) || !IsPassword(password))
        {
            return Task.FromResult(new FileCredentialResult<FileCredentialItem>(
                FileCredentialResultCode.ValidationFailed));
        }

        var id = Guid.NewGuid();
        return SaveAsync(actor, id, null, password, "credential.create",
            _ => new CredentialReference(id, name, (CredentialKind)kind,
                username, "pending", ProtectionVersion((CredentialKind)kind)),
            cancellationToken);
    }

    public Task<FileCredentialResult<FileCredentialItem>> RotatePasswordAsync(
        AdminSession actor, Guid id, string version, string password,
        CancellationToken cancellationToken = default)
    {
        if (!IsPassword(password))
        {
            return Task.FromResult(new FileCredentialResult<FileCredentialItem>(
                FileCredentialResultCode.ValidationFailed));
        }

        return SaveAsync(actor, id, version, password, "credential.rotate", x => x!, cancellationToken);
    }

    public Task<FileCredentialResult<FileCredentialItem>> SetEnabledAsync(
        AdminSession actor, Guid id, string version, bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        return SaveAsync(actor, id, version, null, isEnabled ? "credential.enable" : "credential.disable", x =>
        {
            x!.SetEnabled(isEnabled);
            return x;
        }, cancellationToken);
    }

    public Task<FileCredentialResult<FileCredentialItem>> CreatePrivateKeyAsync(
        AdminSession actor, string name, string username, string privateKey, string? passphrase,
        CancellationToken cancellationToken = default)
    {
        if (!IsText(name, 200) || !IsText(username, 256) || !IsPrivateKey(privateKey) || !IsOptionalPassphrase(passphrase))
        {
            return Task.FromResult(new FileCredentialResult<FileCredentialItem>(
                FileCredentialResultCode.ValidationFailed));
        }

        var id = Guid.NewGuid();
        return SaveAsync(actor, id, null, null, "credential.create",
            _ => new CredentialReference(id, name, CredentialKind.SftpPrivateKey,
                username, "pending", "dp-sftp-private-key-v1"),
            cancellationToken, privateKey, passphrase);
    }

    public Task<FileCredentialResult<FileCredentialItem>> RotatePrivateKeyAsync(
        AdminSession actor, Guid id, string version, string privateKey, string? passphrase,
        CancellationToken cancellationToken = default)
    {
        if (!IsPrivateKey(privateKey) || !IsOptionalPassphrase(passphrase))
        {
            return Task.FromResult(new FileCredentialResult<FileCredentialItem>(
                FileCredentialResultCode.ValidationFailed));
        }

        return SaveAsync(actor, id, version, null, "credential.rotate", x => x!, cancellationToken, privateKey, passphrase);
    }

    private async Task<FileCredentialResult<FileCredentialItem>> SaveAsync(
        AdminSession actor, Guid id, string? version, string? password, string action,
        Func<CredentialReference?, CredentialReference> change, CancellationToken cancellationToken,
        string? privateKey = null, string? passphrase = null)
    {
        var creating = action == "credential.create";
        byte[]? expectedVersion = null;
        if (!creating)
        {
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(version) || version.Length > 16)
            {
                return new(FileCredentialResultCode.ValidationFailed);
            }

            try { expectedVersion = Convert.FromBase64String(version); }
            catch (FormatException) { return new(FileCredentialResultCode.ValidationFailed); }
            if (expectedVersion.Length != 8) { return new(FileCredentialResultCode.ValidationFailed); }
        }

        try
        {
            // 在加密和数据库写入前均复核身份，长期 Circuit 不能沿用失效身份。
            await using (var authorizationContext = await contextFactory.CreateDbContextAsync(cancellationToken))
            {
                if (!await IsAuthorizedAsync(authorizationContext, actor, cancellationToken))
                {
                    return new(FileCredentialResultCode.AuthenticationRequired);
                }
            }

            CredentialKind secretKind;
            if (creating) { secretKind = change(null).Kind; }
            else
            {
                await using var kindContext = await contextFactory.CreateDbContextAsync(cancellationToken);
                var kind = await kindContext.CredentialReferences.Where(x => x.Id == id)
                    .Select(x => (CredentialKind?)x.Kind).SingleOrDefaultAsync(cancellationToken);
                if (kind is null || !IsManagedKind(kind.Value))
                    return new(FileCredentialResultCode.NotFound);
                if (password is not null && !IsPasswordKind((FileCredentialKind)kind.Value))
                    return new(FileCredentialResultCode.NotFound);
                if (privateKey is not null && kind != CredentialKind.SftpPrivateKey)
                    return new(FileCredentialResultCode.NotFound);
                secretKind = kind.Value;
            }
            string? protectedPassword = null;
            string? protectedPassphrase = null;
            if (password is not null || privateKey is not null)
            {
                try
                {
                    if (password is not null)
                    {
                        var protector = new BusinessCredentialDataProtector(
                            configuration[BusinessCredentialDataProtector.KeyRingPathConfigurationKey],
                            purpose: secretKind == CredentialKind.SmbPassword ? "SmbPassword.v1" : "SftpPassword.v1");
                        protectedPassword = protector.ProtectFilePassword(password.AsSpan());
                    }
                    else
                    {
                        var keyRing = configuration[BusinessCredentialDataProtector.KeyRingPathConfigurationKey];
                        protectedPassword = new BusinessCredentialDataProtector(keyRing, purpose: "SftpPrivateKey.v1")
                            .ProtectFilePassword(privateKey!.AsSpan());
                        if (!string.IsNullOrEmpty(passphrase))
                        {
                            protectedPassphrase = new BusinessCredentialDataProtector(
                                keyRing, purpose: "SftpPrivateKeyPassphrase.v1")
                                .ProtectFilePassword(passphrase.AsSpan());
                        }
                    }
                }
                catch (Exception exception) when (exception is CryptographicException
                    or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return new(FileCredentialResultCode.ProtectionUnavailable);
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
                    return new FileCredentialResult<FileCredentialItem>(FileCredentialResultCode.AuthenticationRequired);
                }

                CredentialReference? entity = null;
                if (!creating)
                {
                    entity = await context.CredentialReferences.AsTracking().SingleOrDefaultAsync(
                        x => x.Id == id && (x.Kind == CredentialKind.SmbPassword
                            || x.Kind == CredentialKind.SftpPassword
                            || x.Kind == CredentialKind.SftpPrivateKey), cancellationToken);
                    if (entity is null) { return new(FileCredentialResultCode.NotFound); }
                    if (!entity.RowVersion.AsSpan().SequenceEqual(expectedVersion))
                    {
                        return new(FileCredentialResultCode.Conflict);
                    }
                }

                entity = change(entity);
                if (protectedPassword is not null)
                {
                    entity.RotateProtectedValues(protectedPassword,
                        ProtectionVersion(secretKind), protectedPassphrase);
                }

                if (creating) { context.CredentialReferences.Add(entity); }
                context.AuditRecords.Add(new AuditRecord(actor.AdminUserId, action,
                    "CredentialReference", entity.Id.ToString("N"), "Succeeded", null));
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new FileCredentialResult<FileCredentialItem>(FileCredentialResultCode.Succeeded,
                    new(entity.Id, entity.Name, entity.Username, (FileCredentialKind)entity.Kind, entity.IsEnabled,
                        Convert.ToBase64String(entity.RowVersion), entity.ProtectedSecondarySecret is not null));
            });
        }
        catch (DbUpdateConcurrencyException) { return new(FileCredentialResultCode.Conflict); }
        catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.SqlClient.SqlException
        { Number: 2601 or 2627 })
        { return new(FileCredentialResultCode.Conflict); }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new(FileCredentialResultCode.Unavailable);
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

    private static string ProtectionVersion(CredentialKind kind) => kind switch
    {
        CredentialKind.SmbPassword => "dp-smb-password-v1",
        CredentialKind.SftpPrivateKey => "dp-sftp-private-key-v1",
        _ => "dp-sftp-password-v1",
    };

    private static bool IsManagedKind(CredentialKind kind) =>
        kind is CredentialKind.SmbPassword or CredentialKind.SftpPassword or CredentialKind.SftpPrivateKey;

    private static bool IsPasswordKind(FileCredentialKind kind) =>
        kind is FileCredentialKind.SmbPassword or FileCredentialKind.SftpPassword;

    private static bool IsText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximum && !value.Any(char.IsControl);

    private static bool IsPassword(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 1024;

    private static bool IsPrivateKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 32_768
        && value.All(c => !char.IsControl(c) || c is '\n' or '\r' or '\t');

    private static bool IsOptionalPassphrase(string? value) =>
        value is null || value.Length == 0 || (value.Length <= 1024 && !value.Any(c => char.IsControl(c)));

    private static bool IsStorageFailure(Exception exception) =>
        exception is DbException or DbUpdateException or InvalidOperationException;
}
