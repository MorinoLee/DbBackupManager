using System.Data.Common;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.TargetSql;

internal sealed class SqlPasswordCredentialResolver(
    IDbContextFactory<PlatformDbContext> contextFactory,
    IBusinessCredentialProtector protector) : ITargetSqlCredentialResolver
{
    private readonly IDbContextFactory<PlatformDbContext> _contextFactory = contextFactory
        ?? throw new ArgumentNullException(nameof(contextFactory));
    private readonly IBusinessCredentialProtector _protector = protector
        ?? throw new ArgumentNullException(nameof(protector));

    public async ValueTask<TargetSqlCredentialResolution> ResolveSqlPasswordAsync(
        Guid credentialReferenceId,
        CancellationToken cancellationToken)
    {
        if (credentialReferenceId == Guid.Empty)
        {
            return TargetSqlCredentialResolution.Failed(
                TargetSqlFailureCode.CredentialInvalid);
        }

        CredentialEnvelope? envelope;
        try
        {
            envelope = await ReadEnvelopeAsync(credentialReferenceId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            return TargetSqlCredentialResolution.Failed(
                TargetSqlFailureCode.CredentialUnavailable);
        }

        if (envelope is null || !envelope.IsEnabled)
        {
            return TargetSqlCredentialResolution.Failed(
                TargetSqlFailureCode.CredentialUnavailable);
        }

        if (envelope.Kind != CredentialKind.SqlPassword
            || envelope.ProtectedSecondarySecret is not null
            || !string.Equals(
                envelope.ProtectionVersion,
                BusinessCredentialDataProtector.SqlPasswordProtectionVersion,
                StringComparison.Ordinal))
        {
            return TargetSqlCredentialResolution.Failed(
                TargetSqlFailureCode.CredentialInvalid);
        }

        var unprotected = _protector.UnprotectSqlPassword(envelope.ProtectedSecret);
        if (unprotected.Status != BusinessCredentialProtectionStatus.Succeeded)
        {
            return TargetSqlCredentialResolution.Failed(
                unprotected.Status == BusinessCredentialProtectionStatus.Unavailable
                    ? TargetSqlFailureCode.CredentialUnavailable
                    : TargetSqlFailureCode.CredentialInvalid);
        }

        var secret = unprotected.Secret!;
        try
        {
            return TargetSqlCredentialResolution.Succeeded(
                TargetSqlCredentialLease.CreateAndClear(envelope.Username, secret));
        }
        catch (ArgumentException)
        {
            return TargetSqlCredentialResolution.Failed(
                TargetSqlFailureCode.CredentialInvalid);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(secret.AsSpan()));
        }
    }

    private async Task<CredentialEnvelope?> ReadEnvelopeAsync(
        Guid credentialReferenceId,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.CredentialReferences
            .AsNoTracking()
            .Where(credential => credential.Id == credentialReferenceId)
            .Select(credential => new CredentialEnvelope(
                credential.Kind,
                credential.Username,
                credential.ProtectedSecret,
                credential.ProtectedSecondarySecret,
                credential.ProtectionVersion,
                credential.IsEnabled))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private sealed record CredentialEnvelope(
        CredentialKind Kind,
        string Username,
        string ProtectedSecret,
        string? ProtectedSecondarySecret,
        string ProtectionVersion,
        bool IsEnabled);
}
