using DbBackupManager.Application.TargetSql;

namespace DbBackupManager.Infrastructure.TargetSql;

internal sealed class SqlClientTargetSqlReadOnlyProbe(
    ITargetSqlCredentialResolver credentialResolver,
    ITargetSqlClientSessionFactory sessionFactory) : ITargetSqlReadOnlyProbe, ITargetSqlBackupEvidenceProbe
{
    private readonly ITargetSqlCredentialResolver _credentialResolver = credentialResolver
        ?? throw new ArgumentNullException(nameof(credentialResolver));
    private readonly ITargetSqlClientSessionFactory _sessionFactory = sessionFactory
        ?? throw new ArgumentNullException(nameof(sessionFactory));

    public Task<TargetSqlResult<TargetSqlServerInfo>> ProbeServerAsync(
        TargetSqlConnectionInput connection,
        CancellationToken cancellationToken = default)
    {
        return WithSessionAsync(
            connection,
            async (session, token) =>
            {
                var serverResult = await ReadSupportedServerAsync(session, connection.AllowLegacyTls, token);
                return serverResult;
            },
            cancellationToken);
    }

    public Task<TargetSqlResult<TargetSqlDatabaseCatalog>> DiscoverDatabasesAsync(
        TargetSqlConnectionInput connection,
        CancellationToken cancellationToken = default)
    {
        return WithSessionAsync(
            connection,
            async (session, token) =>
            {
                var serverResult = await ReadSupportedServerAsync(session, connection.AllowLegacyTls, token);
                if (!serverResult.IsSucceeded)
                {
                    return CopyFailure<TargetSqlDatabaseCatalog>(serverResult);
                }

                try
                {
                    var catalog = await session.ReadDatabasesAsync(token);
                    return TargetSqlResult.Succeeded(catalog);
                }
                catch (OperationCanceledException)
                {
                    return ConfirmedFailure<TargetSqlDatabaseCatalog>(
                        TargetSqlFailureCode.Cancelled,
                        TargetSqlFailurePhase.DatabaseDiscovery);
                }
                catch (TargetSqlClientException exception)
                {
                    return ConfirmedFailure<TargetSqlDatabaseCatalog>(
                        exception.FailureCode,
                        TargetSqlFailurePhase.DatabaseDiscovery);
                }
            },
            cancellationToken);
    }

    public Task<TargetSqlResult<TargetSqlBackupVerification>> VerifyBackupAsync(
        TargetSqlConnectionInput connection,
        TargetSqlBackupVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return WithSessionAsync(
            connection,
            async (session, token) =>
            {
                var serverResult = await ReadSupportedServerAsync(session, connection.AllowLegacyTls, token);
                if (!serverResult.IsSucceeded)
                {
                    return CopyFailure<TargetSqlBackupVerification>(serverResult);
                }

                try
                {
                    var verification = await session.VerifyBackupAsync(request, token);
                    return TargetSqlResult.Succeeded(verification);
                }
                catch (OperationCanceledException)
                {
                    return ConfirmedFailure<TargetSqlBackupVerification>(
                        TargetSqlFailureCode.Cancelled,
                        TargetSqlFailurePhase.BackupVerification);
                }
                catch (TargetSqlClientException exception)
                {
                    return ConfirmedFailure<TargetSqlBackupVerification>(
                        exception.FailureCode,
                        TargetSqlFailurePhase.BackupVerification);
                }
            },
            cancellationToken);
    }

    public Task<TargetSqlResult<TargetSqlBackupIdentity>> InspectBackupIdentityAsync(
        TargetSqlConnectionInput connection,
        TargetSqlBackupIdentityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return WithSessionAsync(
            connection,
            async (session, token) =>
            {
                var serverResult = await ReadSupportedServerAsync(session, connection.AllowLegacyTls, token);
                if (!serverResult.IsSucceeded)
                {
                    return CopyFailure<TargetSqlBackupIdentity>(serverResult);
                }

                try
                {
                    return TargetSqlResult.Succeeded(
                        await session.InspectBackupIdentityAsync(request, token));
                }
                catch (OperationCanceledException)
                {
                    return ConfirmedFailure<TargetSqlBackupIdentity>(
                        TargetSqlFailureCode.Cancelled,
                        TargetSqlFailurePhase.BackupIdentityInspection);
                }
                catch (TargetSqlClientException exception)
                {
                    return ConfirmedFailure<TargetSqlBackupIdentity>(
                        exception.FailureCode,
                        TargetSqlFailurePhase.BackupIdentityInspection);
                }
            },
            cancellationToken);
    }

    private async Task<TargetSqlResult<T>> WithSessionAsync<T>(
        TargetSqlConnectionInput connection,
        Func<ITargetSqlClientSession, CancellationToken, Task<TargetSqlResult<T>>> operation,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(connection);

        TargetSqlCredentialResolution credentialResolution;
        try
        {
            credentialResolution = await _credentialResolver.ResolveSqlPasswordAsync(
                connection.CredentialReferenceId,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ConfirmedFailure<T>(
                TargetSqlFailureCode.Cancelled,
                TargetSqlFailurePhase.CredentialResolution);
        }

        using (credentialResolution)
        {
            if (credentialResolution.FailureCode is { } failureCode)
            {
                return ConfirmedFailure<T>(
                    failureCode,
                    TargetSqlFailurePhase.CredentialResolution);
            }

            ITargetSqlClientSession session;
            try
            {
                session = await _sessionFactory.OpenAsync(
                    connection,
                    credentialResolution.Credential!,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return ConfirmedFailure<T>(
                    TargetSqlFailureCode.Cancelled,
                    TargetSqlFailurePhase.ConnectionOpen);
            }
            catch (TargetSqlClientException exception)
            {
                return ConfirmedFailure<T>(
                    exception.FailureCode,
                    TargetSqlFailurePhase.ConnectionOpen);
            }

            await using (session)
            {
                return await operation(session, cancellationToken);
            }
        }
    }

    private static async Task<TargetSqlResult<TargetSqlServerInfo>> ReadSupportedServerAsync(
        ITargetSqlClientSession session,
        bool allowLegacyTls,
        CancellationToken cancellationToken)
    {
        try
        {
            var serverInfo = await session.ReadServerInfoAsync(cancellationToken);
            return TargetSqlServerInfoMapper.IsSupported(serverInfo, allowLegacyTls)
                ? TargetSqlResult.Succeeded(serverInfo)
                : ConfirmedFailure<TargetSqlServerInfo>(
                    TargetSqlFailureCode.UnsupportedServerVersion,
                    TargetSqlFailurePhase.ServerProbe);
        }
        catch (OperationCanceledException)
        {
            return ConfirmedFailure<TargetSqlServerInfo>(
                TargetSqlFailureCode.Cancelled,
                TargetSqlFailurePhase.ServerProbe);
        }
        catch (TargetSqlClientException exception)
        {
            return ConfirmedFailure<TargetSqlServerInfo>(
                exception.FailureCode,
                TargetSqlFailurePhase.ServerProbe);
        }
    }

    private static TargetSqlResult<T> CopyFailure<T>(TargetSqlResult<TargetSqlServerInfo> source)
        where T : class
    {
        return ConfirmedFailure<T>(source.Failure!.Code, source.Failure.Phase);
    }

    private static TargetSqlResult<T> ConfirmedFailure<T>(
        TargetSqlFailureCode code,
        TargetSqlFailurePhase phase)
        where T : class
    {
        return TargetSqlResult.ConfirmedFailure<T>(code, phase);
    }
}
