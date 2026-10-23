using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;

namespace DbBackupManager.Infrastructure.TargetSql;

internal sealed class SqlClientTargetSqlLogReadinessReader(
    ITargetSqlCredentialResolver resolver,
    ITargetSqlClientSessionFactory factory,
    TimeProvider? clock = null) : ITargetSqlLogReadinessReader
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<TargetSqlLogReadinessEvidence> ReadAsync(
        TargetSqlConnectionInput connection,
        TargetSqlLogReadinessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        try
        {
            using var resolution = await resolver.ResolveSqlPasswordAsync(connection.CredentialReferenceId, deadline.Token);
            if (resolution.FailureCode is { } failure) return Failed(request, failure);
            await using var session = await factory.OpenAsync(connection, resolution.Credential!, deadline.Token);
            var server = await session.ReadServerInfoAsync(deadline.Token);
            if (!TargetSqlServerInfoMapper.IsSupported(server, connection.AllowLegacyTls))
            {
                return Failed(request, TargetSqlFailureCode.UnsupportedServerVersion);
            }

            if (session is not ITargetSqlLogReadinessSession readiness)
            {
                return Failed(request, TargetSqlFailureCode.InvalidResponse);
            }

            return (await readiness.ReadLogReadinessAsync(request, deadline.Token)) with { Server = server };
        }
        catch (OperationCanceledException)
        {
            return Failed(
                request,
                cancellationToken.IsCancellationRequested
                    ? TargetSqlFailureCode.Cancelled
                    : TargetSqlFailureCode.TimedOut);
        }
        catch (TargetSqlClientException exception)
        {
            return Failed(request, exception.FailureCode);
        }
    }

    private TargetSqlLogReadinessEvidence Failed(
        TargetSqlLogReadinessRequest request,
        TargetSqlFailureCode code)
    {
        var observedAt = _clock.GetUtcNow();
        return new(
            request.PlatformDatabaseId,
            request.DatabaseName,
            request.ExpectedDatabase,
            null,
            new(
                null,
                null,
                null,
                null,
                false,
                observedAt,
                [new("source", BackupMetadataReadProblem.ReadFailed)],
                code,
                TargetSqlFailurePhase.DatabaseReadiness),
            new(
                false,
                null,
                null,
                null,
                TargetSqlLogLsnState.Unknown,
                null,
                false,
                observedAt,
                [new("source", BackupMetadataReadProblem.ReadFailed)],
                code,
                TargetSqlFailurePhase.RecoveryStatusRead),
            null,
            null,
            null);
    }
}
