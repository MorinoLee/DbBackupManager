using System.Data;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.Configuration;
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

    Task<TargetSqlBackupCompletion> ExecuteBackupAsync(TargetSqlBackupRequest request, CancellationToken cancellationToken);

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

    public async Task<TargetSqlBackupCompletion> ExecuteBackupAsync(
        TargetSqlBackupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        await using var command = TargetSqlCommandFactory.CreateBackup(_connection, request);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new(request.BackupType, request.UseCopyOnly, request.UseChecksum, request.UseCompression);
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
    ITargetSqlBackupCommandObserver? commandObserver = null,
    DifferentialBackupAllowance? differentialAllowance = null) : ITargetSqlBackupExecutor, ITargetSqlPlanBackupExecutor
{
    private readonly ITargetSqlCredentialResolver _credentialResolver = credentialResolver
        ?? throw new ArgumentNullException(nameof(credentialResolver));
    private readonly ITargetSqlBackupClientSessionFactory _sessionFactory = sessionFactory
        ?? throw new ArgumentNullException(nameof(sessionFactory));
    private readonly ITargetSqlBackupCommandObserver _commandObserver = commandObserver
        ?? new NoOpTargetSqlBackupCommandObserver();

    private readonly DifferentialBackupAllowance _differentialAllowance = differentialAllowance ?? DifferentialBackupAllowance.None;

    public Task<TargetSqlResult<TargetSqlFullBackupCompletion>> ExecuteFullBackupAsync(
        TargetSqlConnectionInput connection, TargetSqlFullBackupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteCoreAsync(connection, request.UseCompression, false,
            (session, token) => session.ExecuteFullBackupAsync(request, token), cancellationToken);
    }

    public Task<TargetSqlResult<TargetSqlBackupCompletion>> ExecuteBackupAsync(
        TargetSqlConnectionInput connection, TargetSqlBackupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteCoreAsync(connection, request.UseCompression, request.BackupType == BackupType.Differential,
            (session, token) => session.ExecuteBackupAsync(request, token), cancellationToken);
    }

    private async Task<TargetSqlResult<T>> ExecuteCoreAsync<T>(
        TargetSqlConnectionInput connection,
        bool useCompression,
        bool isDifferential,
        Func<ITargetSqlBackupClientSession, CancellationToken, Task<T>> execute,
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
            if (credentialResolution.FailureCode is { } credentialFailure)
            {
                return ConfirmedFailure<T>(
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
                var serverFailure = await ValidateServerAsync<T>(
                    session,
                    useCompression,
                    isDifferential,
                    connection.AllowLegacyTls,
                    cancellationToken);
                if (serverFailure is not null)
                {
                    return serverFailure;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return ConfirmedFailure<T>(
                        TargetSqlFailureCode.Cancelled,
                        TargetSqlFailurePhase.BackupExecution);
                }

                try
                {
                    var operation = execute(session, cancellationToken);
                    await _commandObserver.OnCommandStartedAsync(cancellationToken);
                    var completion = await operation;
                    return TargetSqlResult.Succeeded(completion);
                }
                catch (OperationCanceledException)
                {
                    return TargetSqlResult.Indeterminate<T>(
                        TargetSqlFailureCode.Cancelled,
                        TargetSqlFailurePhase.BackupExecution);
                }
                catch (TargetSqlClientException exception)
                {
                    return exception.Certainty == TargetSqlClientFailureCertainty.Indeterminate
                        ? TargetSqlResult.Indeterminate<T>(
                            exception.FailureCode,
                            TargetSqlFailurePhase.BackupExecution)
                        : ConfirmedFailure<T>(
                            exception.FailureCode,
                            TargetSqlFailurePhase.BackupExecution);
                }
            }
        }
    }

    private async Task<TargetSqlResult<T>?> ValidateServerAsync<T>(
        ITargetSqlBackupClientSession session,
        bool useCompression,
        bool isDifferential,
        bool allowLegacyTls,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var serverInfo = await session.ReadServerInfoAsync(cancellationToken);
            if (!TargetSqlServerInfoMapper.IsSupported(serverInfo, allowLegacyTls))
            {
                return ConfirmedFailure<T>(
                    TargetSqlFailureCode.UnsupportedServerVersion,
                    TargetSqlFailurePhase.ServerProbe);
            }

            if (isDifferential && !_differentialAllowance.Allows(serverInfo))
            {
                return ConfirmedFailure<T>(TargetSqlFailureCode.BackupTypeNotValidated, TargetSqlFailurePhase.ServerProbe);
            }

            return useCompression && !serverInfo.SupportsBackupCompression
                ? ConfirmedFailure<T>(
                    TargetSqlFailureCode.CompressionUnsupported,
                    TargetSqlFailurePhase.ServerProbe)
                : null;
        }
        catch (OperationCanceledException)
        {
            return ConfirmedFailure<T>(
                TargetSqlFailureCode.Cancelled,
                TargetSqlFailurePhase.ServerProbe);
        }
        catch (TargetSqlClientException exception)
        {
            return ConfirmedFailure<T>(
                exception.FailureCode,
                TargetSqlFailurePhase.ServerProbe);
        }
    }

    private static TargetSqlResult<T> ConfirmedFailure<T>(
        TargetSqlFailureCode code,
        TargetSqlFailurePhase phase)
        where T : class
    {
        return TargetSqlResult.ConfirmedFailure<T>(code, phase);
    }
}
