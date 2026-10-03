using System.Data;
using DbBackupManager.Application.TargetSql;
using Microsoft.Data.SqlClient;

namespace DbBackupManager.Infrastructure.TargetSql;

internal interface ITargetSqlBackupClientSessionFactory
{
    Task<ITargetSqlBackupClientSession> OpenAsync(
        TargetSqlConnectionInput input,
        TargetSqlCredentialLease credential,
        CancellationToken cancellationToken);
}

internal interface ITargetSqlBackupClientSession : IAsyncDisposable
{
    Task<TargetSqlServerInfo> ReadServerInfoAsync(CancellationToken cancellationToken);

    Task<TargetSqlFullBackupCompletion> ExecuteFullBackupAsync(
        TargetSqlFullBackupRequest request,
        CancellationToken cancellationToken);
}

internal sealed class SqlClientTargetSqlBackupSessionFactory : ITargetSqlBackupClientSessionFactory
{
    public async Task<ITargetSqlBackupClientSession> OpenAsync(
        TargetSqlConnectionInput input,
        TargetSqlCredentialLease credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(credential);

        SqlConnection? connection = null;
        try
        {
            connection = SqlClientTargetSqlConnectionFactory.Create(input, credential);
            await connection.OpenAsync(cancellationToken);
            return new SqlClientTargetSqlBackupSession(connection);
        }
        catch (OperationCanceledException)
        {
            await SqlClientTargetSqlConnectionFactory.DisposeQuietlyAsync(connection);
            throw;
        }
        catch (Exception exception) when (TargetSqlClientException.CanClassify(exception))
        {
            await SqlClientTargetSqlConnectionFactory.DisposeQuietlyAsync(connection);
            throw TargetSqlClientException.From(exception, TargetSqlClientOperation.OpenConnection);
        }
    }
}

internal sealed class SqlClientTargetSqlBackupSession(SqlConnection connection)
    : ITargetSqlBackupClientSession
{
    private readonly SqlConnection _connection = connection
        ?? throw new ArgumentNullException(nameof(connection));

    public async Task<TargetSqlServerInfo> ReadServerInfoAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = TargetSqlCommandFactory.CreateServerProbe(_connection);
            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SingleRow,
                cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw TargetSqlClientException.InvalidResponse();
            }

            return TargetSqlServerInfoMapper.Map(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TargetSqlClientException)
        {
            throw;
        }
        catch (Exception exception) when (TargetSqlClientException.IsResponseShapeFailure(exception))
        {
            throw TargetSqlClientException.InvalidResponse();
        }
        catch (Exception exception) when (TargetSqlClientException.CanClassify(exception))
        {
            throw TargetSqlClientException.From(exception, TargetSqlClientOperation.ReadOnlyCommand);
        }
    }

    public async Task<TargetSqlFullBackupCompletion> ExecuteFullBackupAsync(
        TargetSqlFullBackupRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        await using var command = TargetSqlCommandFactory.CreateFullBackup(_connection, request);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new TargetSqlFullBackupCompletion(
                request.UseCopyOnly,
                request.UseChecksum,
                request.UseCompression);
        }
        catch (Exception exception) when (exception is OperationCanceledException
            || TargetSqlClientException.CanClassify(exception))
        {
            throw TargetSqlClientException.FromBackupExecution(exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        return SqlClientTargetSqlConnectionFactory.DisposeQuietlyAsync(_connection);
    }
}

internal sealed class SqlClientTargetSqlBackupExecutor(
    ITargetSqlCredentialResolver credentialResolver,
    ITargetSqlBackupClientSessionFactory sessionFactory,
    ITargetSqlBackupCommandObserver? commandObserver = null) : ITargetSqlBackupExecutor
{
    private readonly ITargetSqlCredentialResolver _credentialResolver = credentialResolver
        ?? throw new ArgumentNullException(nameof(credentialResolver));
    private readonly ITargetSqlBackupClientSessionFactory _sessionFactory = sessionFactory
        ?? throw new ArgumentNullException(nameof(sessionFactory));
    private readonly ITargetSqlBackupCommandObserver _commandObserver = commandObserver
        ?? new NoOpTargetSqlBackupCommandObserver();

    public async Task<TargetSqlResult<TargetSqlFullBackupCompletion>> ExecuteFullBackupAsync(
        TargetSqlConnectionInput connection,
        TargetSqlFullBackupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);

        TargetSqlCredentialResolution credentialResolution;
        try
        {
            credentialResolution = await _credentialResolver.ResolveSqlPasswordAsync(
                connection.CredentialReferenceId,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ConfirmedFailure(
                TargetSqlFailureCode.Cancelled,
                TargetSqlFailurePhase.CredentialResolution);
        }

        using (credentialResolution)
        {
            if (credentialResolution.FailureCode is { } credentialFailure)
            {
                return ConfirmedFailure(
                    credentialFailure,
                    TargetSqlFailurePhase.CredentialResolution);
            }

            ITargetSqlBackupClientSession session;
            try
            {
                session = await _sessionFactory.OpenAsync(
                    connection,
                    credentialResolution.Credential!,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return ConfirmedFailure(
                    TargetSqlFailureCode.Cancelled,
                    TargetSqlFailurePhase.ConnectionOpen);
            }
            catch (TargetSqlClientException exception)
            {
                return ConfirmedFailure(
                    exception.FailureCode,
                    TargetSqlFailurePhase.ConnectionOpen);
            }

            await using (session)
            {
                var serverFailure = await ValidateServerAsync(
                    session,
                    request.UseCompression,
                    connection.AllowLegacyTls,
                    cancellationToken);
                if (serverFailure is not null)
                {
                    return serverFailure;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return ConfirmedFailure(
                        TargetSqlFailureCode.Cancelled,
                        TargetSqlFailurePhase.BackupExecution);
                }

                try
                {
                    var operation = session.ExecuteFullBackupAsync(
                        request,
                        cancellationToken);
                    await _commandObserver.OnCommandStartedAsync(cancellationToken);
                    var completion = await operation;
                    return TargetSqlResult.Succeeded(completion);
                }
                catch (OperationCanceledException)
                {
                    return TargetSqlResult.Indeterminate<TargetSqlFullBackupCompletion>(
                        TargetSqlFailureCode.Cancelled,
                        TargetSqlFailurePhase.BackupExecution);
                }
                catch (TargetSqlClientException exception)
                {
                    return exception.Certainty == TargetSqlClientFailureCertainty.Indeterminate
                        ? TargetSqlResult.Indeterminate<TargetSqlFullBackupCompletion>(
                            exception.FailureCode,
                            TargetSqlFailurePhase.BackupExecution)
                        : ConfirmedFailure(
                            exception.FailureCode,
                            TargetSqlFailurePhase.BackupExecution);
                }
            }
        }
    }

    private static async Task<TargetSqlResult<TargetSqlFullBackupCompletion>?> ValidateServerAsync(
        ITargetSqlBackupClientSession session,
        bool useCompression,
        bool allowLegacyTls,
        CancellationToken cancellationToken)
    {
        try
        {
            var serverInfo = await session.ReadServerInfoAsync(cancellationToken);
            if (!TargetSqlServerInfoMapper.IsSupported(serverInfo, allowLegacyTls))
            {
                return ConfirmedFailure(
                    TargetSqlFailureCode.UnsupportedServerVersion,
                    TargetSqlFailurePhase.ServerProbe);
            }

            return useCompression && !serverInfo.SupportsBackupCompression
                ? ConfirmedFailure(
                    TargetSqlFailureCode.CompressionUnsupported,
                    TargetSqlFailurePhase.ServerProbe)
                : null;
        }
        catch (OperationCanceledException)
        {
            return ConfirmedFailure(
                TargetSqlFailureCode.Cancelled,
                TargetSqlFailurePhase.ServerProbe);
        }
        catch (TargetSqlClientException exception)
        {
            return ConfirmedFailure(
                exception.FailureCode,
                TargetSqlFailurePhase.ServerProbe);
        }
    }

    private static TargetSqlResult<TargetSqlFullBackupCompletion> ConfirmedFailure(
        TargetSqlFailureCode code,
        TargetSqlFailurePhase phase)
    {
        return TargetSqlResult.ConfirmedFailure<TargetSqlFullBackupCompletion>(code, phase);
    }
}
