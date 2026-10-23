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

            var evidence = await readiness.ReadLogReadinessAsync(request, deadline.Token);
            return NormalizeDeadline(evidence with { Server = server }, deadline, cancellationToken);
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

    private static TargetSqlLogReadinessEvidence NormalizeDeadline(
        TargetSqlLogReadinessEvidence evidence,
        CancellationTokenSource deadline,
        CancellationToken caller)
    {
        if (!deadline.IsCancellationRequested || caller.IsCancellationRequested) return evidence;
        return evidence with
        {
            Database = evidence.Database with
            {
                FailureCode = Timeout(evidence.Database.FailureCode),
            },
            Recovery = evidence.Recovery with
            {
                FailureCode = Timeout(evidence.Recovery.FailureCode),
            },
            Recheck = evidence.Recheck is null
                ? null
                : evidence.Recheck with
                {
                    FailureCode = Timeout(evidence.Recheck.FailureCode),
                },
            RecoveryRecheck = evidence.RecoveryRecheck is null
                ? null
                : evidence.RecoveryRecheck with
                {
                    FailureCode = Timeout(evidence.RecoveryRecheck.FailureCode),
                },
        };
    }

    private static TargetSqlFailureCode? Timeout(TargetSqlFailureCode? code) =>
        code == TargetSqlFailureCode.Cancelled ? TargetSqlFailureCode.TimedOut : code;

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
