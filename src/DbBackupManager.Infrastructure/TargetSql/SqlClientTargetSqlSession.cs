using System.Data;
using System.Security.Authentication;
using DbBackupManager.Application.TargetSql;
using Microsoft.Data.SqlClient;

namespace DbBackupManager.Infrastructure.TargetSql;

internal interface ITargetSqlClientSessionFactory
{
    Task<ITargetSqlClientSession> OpenAsync(
        TargetSqlConnectionInput input,
        TargetSqlCredentialLease credential,
        CancellationToken cancellationToken);
}

internal interface ITargetSqlClientSession : IAsyncDisposable
{
    Task<TargetSqlServerInfo> ReadServerInfoAsync(CancellationToken cancellationToken);

    Task<TargetSqlDatabaseCatalog> ReadDatabasesAsync(CancellationToken cancellationToken);

    Task<TargetSqlBackupIdentity> InspectBackupIdentityAsync(
        TargetSqlBackupIdentityRequest request,
        CancellationToken cancellationToken);

    Task<TargetSqlBackupVerification> VerifyBackupAsync(
        TargetSqlBackupVerificationRequest request,
        CancellationToken cancellationToken);
}

internal sealed class SqlClientTargetSqlSessionFactory : ITargetSqlClientSessionFactory
{
    public async Task<ITargetSqlClientSession> OpenAsync(
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
            return new SqlClientTargetSqlSession(connection);
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

internal static class SqlClientTargetSqlConnectionFactory
{
    internal const string ApplicationName = "DbBackupManager.TargetSql";

    public static SqlConnection Create(
        TargetSqlConnectionInput input,
        TargetSqlCredentialLease credential)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(credential);

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = input.ConnectionAddress,
            InitialCatalog = "master",
            IntegratedSecurity = false,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = input.TrustServerCertificate,
            ConnectTimeout = input.ConnectionTimeoutSeconds,
            Pooling = false,
            PersistSecurityInfo = false,
            Enlist = false,
            ConnectRetryCount = 0,
            MultipleActiveResultSets = false,
            ApplicationName = ApplicationName,
        };

        return new SqlConnection(
            builder.ConnectionString,
            credential.CreateSqlCredential());
    }

    public static async ValueTask DisposeQuietlyAsync(SqlConnection? connection)
    {
        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception exception) when (TargetSqlClientException.CanClassify(exception))
        {
            // 连接池已禁用；关闭阶段不覆盖已经获得的稳定业务结果或失败分类。
        }
    }
}

internal sealed partial class SqlClientTargetSqlSession(SqlConnection connection) : ITargetSqlClientSession
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

    public async Task<TargetSqlDatabaseCatalog> ReadDatabasesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = TargetSqlCommandFactory.CreateDatabaseDiscovery(_connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var databases = new List<TargetSqlDatabaseInfo>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var state = TargetSqlDatabaseInfoMapper.MapState(
                    reader.IsDBNull(1) ? null : reader.GetString(1));
                var recoveryModel = TargetSqlDatabaseInfoMapper.MapRecoveryModel(
                    reader.IsDBNull(2) ? null : reader.GetString(2));
                var isSystemDatabase = reader.GetBoolean(3);
                var isSnapshot = reader.GetBoolean(4);
                databases.Add(new TargetSqlDatabaseInfo(
                    reader.GetString(0),
                    state,
                    recoveryModel,
                    isSystemDatabase,
                    isSnapshot,
                    state == TargetSqlDatabaseState.Online
                        && !isSystemDatabase
                        && !isSnapshot));
            }

            return new TargetSqlDatabaseCatalog(databases);
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

    public async Task<TargetSqlBackupVerification> VerifyBackupAsync(
        TargetSqlBackupVerificationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = TargetSqlCommandFactory.CreateBackupVerification(
                _connection,
                request);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new TargetSqlBackupVerification(request.UseChecksum);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TargetSqlClientException)
        {
            throw;
        }
        catch (Exception exception) when (TargetSqlClientException.CanClassify(exception))
        {
            throw TargetSqlClientException.From(exception, TargetSqlClientOperation.ReadOnlyCommand);
        }
    }

    public async Task<TargetSqlBackupIdentity> InspectBackupIdentityAsync(
        TargetSqlBackupIdentityRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = TargetSqlCommandFactory.CreateBackupIdentityInspection(
                _connection,
                request);
            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SingleRow,
                cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new TargetSqlBackupIdentity(TargetSqlBackupIdentityStatus.NotFound);
            }

            var databaseMatches = reader.GetBoolean(0);
            var isFullBackup = reader.GetBoolean(1);
            var isFinished = reader.GetBoolean(2);
            return new TargetSqlBackupIdentity(
                !databaseMatches || !isFullBackup
                    ? TargetSqlBackupIdentityStatus.IdentityMismatch
                    : isFinished
                        ? TargetSqlBackupIdentityStatus.CompletedMatching
                        : TargetSqlBackupIdentityStatus.InProgress);
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

    public ValueTask DisposeAsync()
    {
        return SqlClientTargetSqlConnectionFactory.DisposeQuietlyAsync(_connection);
    }
}

internal static class TargetSqlServerInfoMapper
{
    public static TargetSqlServerInfo Map(
        string? productVersion,
        string? productLevel,
        string? edition,
        int? engineEdition)
    {
        var normalizedVersion = productVersion?.Trim();
        if (string.IsNullOrEmpty(normalizedVersion)
            || !Version.TryParse(normalizedVersion, out var parsedVersion)
            || parsedVersion.Major <= 0
            || engineEdition is null)
        {
            throw TargetSqlClientException.InvalidResponse();
        }

        return new TargetSqlServerInfo(
            normalizedVersion,
            NormalizeOptional(productLevel),
            NormalizeOptional(edition),
            engineEdition.Value,
            parsedVersion.Major,
            SupportsBackupCompression(parsedVersion, engineEdition.Value));
    }

    public static bool IsSupported(TargetSqlServerInfo info, bool allowLegacyTls = false)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!Version.TryParse(info.ProductVersion, out var version))
        {
            return false;
        }

        return version.Major > 10
            || (version.Major == 10
                && version.Minor == 50
                && version.Build >= (allowLegacyTls ? 1_600 : 6_542));
    }

    private static bool SupportsBackupCompression(Version version, int engineEdition)
    {
        var versionSupportsCompression = version.Major > 10
            || (version.Major == 10 && version.Minor >= 50);
        return versionSupportsCompression && engineEdition is 2 or 3;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

internal static class TargetSqlDatabaseInfoMapper
{
    public static TargetSqlDatabaseState MapState(string? value)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "ONLINE" => TargetSqlDatabaseState.Online,
            "RESTORING" => TargetSqlDatabaseState.Restoring,
            "RECOVERING" => TargetSqlDatabaseState.Recovering,
            "RECOVERY_PENDING" => TargetSqlDatabaseState.RecoveryPending,
            "SUSPECT" => TargetSqlDatabaseState.Suspect,
            "EMERGENCY" => TargetSqlDatabaseState.Emergency,
            "OFFLINE" => TargetSqlDatabaseState.Offline,
            "COPYING" => TargetSqlDatabaseState.Copying,
            "OFFLINE_SECONDARY" => TargetSqlDatabaseState.OfflineSecondary,
            _ => TargetSqlDatabaseState.Unknown,
        };
    }

    public static TargetSqlRecoveryModel MapRecoveryModel(string? value)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "FULL" => TargetSqlRecoveryModel.Full,
            "BULK_LOGGED" => TargetSqlRecoveryModel.BulkLogged,
            "SIMPLE" => TargetSqlRecoveryModel.Simple,
            _ => TargetSqlRecoveryModel.Unknown,
        };
    }
}

internal enum TargetSqlClientOperation
{
    OpenConnection,
    ReadOnlyCommand,
    BackupCommand,
}

internal enum TargetSqlClientFailureCertainty
{
    Confirmed,
    Indeterminate,
}

internal sealed class TargetSqlClientException : Exception
{
    private static readonly HashSet<int> AuthenticationErrorNumbers = [18452, 18456];
    private static readonly HashSet<int> AuthorizationErrorNumbers = [229, 262, 916];
    private static readonly HashSet<int> DatabaseNotFoundErrorNumbers = [911];
    private static readonly HashSet<int> DatabaseUnavailableErrorNumbers = [4060, 924, 927, 942, 945];
    private static readonly HashSet<int> BackupDestinationErrorNumbers = [3201, 3202];
    private static readonly HashSet<int> NetworkErrorNumbers =
        [-1, 2, 20, 53, 64, 233, 258, 10053, 10054, 10060, 10061, 11001];

    private TargetSqlClientException(
        TargetSqlFailureCode failureCode,
        TargetSqlClientFailureCertainty certainty)
        : base($"目标 SQL 操作失败：{failureCode}。")
    {
        FailureCode = failureCode;
        Certainty = certainty;
    }

    public TargetSqlFailureCode FailureCode { get; }

    public TargetSqlClientFailureCertainty Certainty { get; }

    public static bool CanClassify(Exception exception)
    {
        return exception is SqlException
            or ArgumentException
            or InvalidOperationException
            or TimeoutException
            or AuthenticationException;
    }

    public static bool IsResponseShapeFailure(Exception exception)
    {
        return exception is InvalidCastException
            or FormatException
            or IndexOutOfRangeException;
    }

    public static TargetSqlClientException From(
        Exception exception,
        TargetSqlClientOperation operation)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (ContainsAuthenticationException(exception))
        {
            return Confirmed(TargetSqlFailureCode.TransportSecurityFailed);
        }

        if (exception is TimeoutException)
        {
            return Confirmed(TargetSqlFailureCode.TimedOut);
        }

        if (exception is SqlException sqlException)
        {
            var errorNumbers = sqlException.Errors.Cast<SqlError>().Select(error => error.Number);
            return Confirmed(ClassifySqlErrorNumbers(errorNumbers, operation));
        }

        return Confirmed(
            operation == TargetSqlClientOperation.OpenConnection
                ? TargetSqlFailureCode.ConnectionFailed
                : TargetSqlFailureCode.CommandRejected);
    }

    public static TargetSqlClientException FromBackupExecution(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException)
        {
            return Indeterminate(TargetSqlFailureCode.Cancelled);
        }

        if (ContainsAuthenticationException(exception))
        {
            return Indeterminate(TargetSqlFailureCode.TransportSecurityFailed);
        }

        if (exception is TimeoutException)
        {
            return Indeterminate(TargetSqlFailureCode.TimedOut);
        }

        if (exception is SqlException sqlException)
        {
            var errorNumbers = sqlException.Errors.Cast<SqlError>().Select(error => error.Number).ToArray();
            return FromBackupErrorNumbers(errorNumbers);
        }

        return Confirmed(TargetSqlFailureCode.CommandRejected);
    }

    internal static TargetSqlClientException FromBackupErrorNumbers(IEnumerable<int> errorNumbers)
    {
        var numbers = errorNumbers.ToArray();
        if (numbers.Contains(-2))
        {
            return Indeterminate(TargetSqlFailureCode.TimedOut);
        }

        if (numbers.Any(NetworkErrorNumbers.Contains))
        {
            return Indeterminate(TargetSqlFailureCode.ConnectionInterrupted);
        }

        return Confirmed(ClassifySqlErrorNumbers(
            numbers,
            TargetSqlClientOperation.BackupCommand));
    }

    public static TargetSqlClientException InvalidResponse()
    {
        return Confirmed(TargetSqlFailureCode.InvalidResponse);
    }

    internal static TargetSqlClientException Classified(
        TargetSqlFailureCode failureCode,
        TargetSqlClientFailureCertainty certainty = TargetSqlClientFailureCertainty.Confirmed)
    {
        return certainty == TargetSqlClientFailureCertainty.Indeterminate
            ? Indeterminate(failureCode)
            : Confirmed(failureCode);
    }

    internal static TargetSqlFailureCode ClassifySqlErrorNumbers(
        IEnumerable<int> errorNumbers,
        TargetSqlClientOperation operation)
    {
        ArgumentNullException.ThrowIfNull(errorNumbers);
        var numbers = errorNumbers.ToArray();
        if (numbers.Contains(-2))
        {
            return TargetSqlFailureCode.TimedOut;
        }

        if (numbers.Any(AuthenticationErrorNumbers.Contains))
        {
            return TargetSqlFailureCode.AuthenticationFailed;
        }

        if (numbers.Any(AuthorizationErrorNumbers.Contains))
        {
            return TargetSqlFailureCode.AuthorizationDenied;
        }

        if (numbers.Any(DatabaseNotFoundErrorNumbers.Contains))
        {
            return TargetSqlFailureCode.DatabaseNotFound;
        }

        if (numbers.Any(DatabaseUnavailableErrorNumbers.Contains))
        {
            return TargetSqlFailureCode.DatabaseUnavailable;
        }

        if (operation == TargetSqlClientOperation.BackupCommand
            && numbers.Contains(3035))
        {
            return TargetSqlFailureCode.DifferentialBaseMissing;
        }

        if (operation == TargetSqlClientOperation.BackupCommand
            && numbers.Any(BackupDestinationErrorNumbers.Contains))
        {
            return TargetSqlFailureCode.BackupDestinationUnavailable;
        }

        if (operation == TargetSqlClientOperation.ReadOnlyCommand
            && numbers.Any(NetworkErrorNumbers.Contains))
        {
            return TargetSqlFailureCode.ConnectionInterrupted;
        }

        return operation == TargetSqlClientOperation.OpenConnection
            ? TargetSqlFailureCode.ConnectionFailed
            : TargetSqlFailureCode.CommandRejected;
    }

    private static bool ContainsAuthenticationException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }

    private static TargetSqlClientException Confirmed(TargetSqlFailureCode failureCode)
    {
        return new TargetSqlClientException(
            failureCode,
            TargetSqlClientFailureCertainty.Confirmed);
    }

    private static TargetSqlClientException Indeterminate(TargetSqlFailureCode failureCode)
    {
        return new TargetSqlClientException(
            failureCode,
            TargetSqlClientFailureCertainty.Indeterminate);
    }
}
