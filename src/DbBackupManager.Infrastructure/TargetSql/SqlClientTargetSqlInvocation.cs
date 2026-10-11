using System.Data;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.Data.SqlClient;

namespace DbBackupManager.Infrastructure.TargetSql;

internal interface ITargetSqlInvocationConnectionFactory
{
    Task<SqlConnection> OpenAsync(TargetSqlConnectionInput input, TargetSqlCredentialLease credential, CancellationToken token);
}
internal sealed class TargetSqlInvocationConnectionFactory : ITargetSqlInvocationConnectionFactory
{
    public async Task<SqlConnection> OpenAsync(TargetSqlConnectionInput input, TargetSqlCredentialLease credential, CancellationToken token)
    {
        var connection = SqlClientTargetSqlConnectionFactory.Create(input, credential);
        connection.ConnectionString = new SqlConnectionStringBuilder(connection.ConnectionString)
        { ConnectRetryCount = 0, Pooling = false }.ConnectionString;
        try { await connection.OpenAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
}

internal sealed class SqlClientTargetSqlInvocation(
    ITargetSqlCredentialResolver resolver, ITargetSqlInvocationConnectionFactory factory,
    DifferentialBackupAllowance allowance, TimeProvider clock)
    : ITargetSqlInvocationPreparation, ITargetSqlInvocationTerminationProbe, ITargetSqlActiveBaselineReader
{
    public async Task<TargetSqlResult<IPreparedTargetSqlBackupInvocation>> PrepareAsync(TargetSqlConnectionInput connection,
        TargetSqlBackupRequest request, Guid callerIncarnationId, CancellationToken cancellationToken = default)
    {
        if (callerIncarnationId == Guid.Empty) throw new ArgumentException("调用进程标识不能为空。");
        SqlConnection? sql = null;
        try
        {
            using var credential = await resolver.ResolveSqlPasswordAsync(connection.CredentialReferenceId, cancellationToken);
            if (credential.FailureCode is { } code) return Failed(code);
            sql = await factory.OpenAsync(connection, credential.Credential!, cancellationToken);
            var session = new SqlClientTargetSqlBackupSession(sql);
            var server = await session.ReadServerInfoAsync(cancellationToken);
            if (!TargetSqlServerInfoMapper.IsSupported(server, connection.AllowLegacyTls)) return Failed(TargetSqlFailureCode.UnsupportedServerVersion);
            if (request.BackupType == BackupType.Differential && !allowance.Allows(server)) return Failed(TargetSqlFailureCode.BackupTypeNotValidated);
            if (request.UseCompression && !server.SupportsBackupCompression) return Failed(TargetSqlFailureCode.CompressionUnsupported);
            await using var command = new SqlCommand("""
                SELECT s.session_id, s.login_time, c.connection_id, rs.database_guid, rs.family_guid
                FROM sys.dm_exec_sessions AS s JOIN sys.dm_exec_connections AS c ON c.session_id = s.session_id
                CROSS JOIN sys.database_recovery_status AS rs
                WHERE s.session_id = @@SPID AND rs.database_id = DB_ID(@database);
                """, sql) { CommandTimeout = connection.ConnectionTimeoutSeconds };
            command.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = request.DatabaseName;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return Failed(TargetSqlFailureCode.InvalidResponse);
            for (var index = 0; index < 5; index++)
                if (reader.IsDBNull(index)) return Failed(TargetSqlFailureCode.InvalidResponse);
            var binding = new BackupInvocationBinding
            {
                State = BackupInvocationBindingState.Known,
                CallerIncarnationId = callerIncarnationId,
                SessionId = Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture),
                SessionEstablishedLocal = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Unspecified),
                ConnectionId = reader.GetGuid(2)
            };
            var identity = new BackupDatabaseIdentity(reader.GetGuid(3), reader.GetGuid(4));
            if (await reader.ReadAsync(cancellationToken)) return Failed(TargetSqlFailureCode.InvalidResponse);
            binding.Validate();
            var prepared = new Prepared(sql, request, binding, identity);
            sql = null; // 句柄独占这条连接；禁止重连或重用旧调用授权。
            return TargetSqlResult.Succeeded<IPreparedTargetSqlBackupInvocation>(prepared);
        }
        catch (OperationCanceledException) { return Failed(TargetSqlFailureCode.Cancelled); }
        catch (TargetSqlClientException exception) { return Failed(exception.FailureCode); }
        catch (Exception exception) when (TargetSqlClientException.CanClassify(exception))
        { return Failed(TargetSqlClientException.From(exception, TargetSqlClientOperation.ReadOnlyCommand).FailureCode); }
        finally { if (sql is not null) await sql.DisposeAsync(); }
        static TargetSqlResult<IPreparedTargetSqlBackupInvocation> Failed(TargetSqlFailureCode code) =>
            TargetSqlResult.ConfirmedFailure<IPreparedTargetSqlBackupInvocation>(code, TargetSqlFailurePhase.ServerProbe);
    }

    public async Task<BackupActiveMetadataEvidence> ReadAsync(TargetSqlConnectionInput connection,
        TargetSqlActiveBaselineRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.DatabaseName) || request.DatabaseName.Length > 128 || request.TimeoutSeconds is < 1 or > 3600)
            throw new ArgumentException("活动基线读取请求无效。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        try
        {
            using var credential = await resolver.ResolveSqlPasswordAsync(connection.CredentialReferenceId, deadline.Token);
            if (credential.FailureCode is { } code) return SqlClientTargetSqlBackupMetadataReader.Failed(code).Active;
            await using var sql = await factory.OpenAsync(connection, credential.Credential!, deadline.Token);
            return await new SqlClientTargetSqlSession(sql).ReadActiveAsync(request, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return SqlClientTargetSqlBackupMetadataReader.Failed(cancellationToken.IsCancellationRequested
            ? TargetSqlFailureCode.Cancelled : TargetSqlFailureCode.TimedOut).Active;
        }
        catch (Exception exception) when (TargetSqlClientException.CanClassify(exception))
        { return SqlClientTargetSqlBackupMetadataReader.Failed(TargetSqlClientException.From(exception, TargetSqlClientOperation.ReadOnlyCommand).FailureCode).Active; }
    }

    public async Task<TargetSqlInvocationTerminationEvidence> ReadAsync(TargetSqlConnectionInput connection, Guid invocationPermitId,
        BackupInvocationBinding originalBinding, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        if (invocationPermitId == Guid.Empty || timeoutSeconds is < 1 or > 3600) throw new ArgumentException("终止探测请求无效。");
        originalBinding.Validate();
        if (originalBinding.State != BackupInvocationBindingState.Known || originalBinding.ConnectionId is null)
            return Result(TargetSqlInvocationTerminationState.Unknown, "termination.binding_unknown");
        try
        {
            using var credential = await resolver.ResolveSqlPasswordAsync(connection.CredentialReferenceId, cancellationToken);
            if (credential.FailureCode is not null) return Result(TargetSqlInvocationTerminationState.Unknown, "termination.credential_unavailable");
            await using var sql = await factory.OpenAsync(connection, credential.Credential!, cancellationToken);
            await using var command = new SqlCommand("""
                DECLARE @permission nvarchar(64) = CASE WHEN CONVERT(int, SERVERPROPERTY('ProductMajorVersion')) >= 16
                    THEN N'VIEW SERVER PERFORMANCE STATE' ELSE N'VIEW SERVER STATE' END;
                IF HAS_PERMS_BY_NAME(NULL, NULL, @permission) <> 1 OR HAS_PERMS_BY_NAME(NULL, NULL, @permission) IS NULL
                    SELECT CONVERT(int, -1);
                ELSE SELECT COUNT(*) FROM sys.dm_exec_connections AS c JOIN sys.dm_exec_sessions AS s ON s.session_id = c.session_id
                    WHERE c.connection_id = @connection OR (s.session_id = @session AND s.login_time = @login);
                """, sql) { CommandTimeout = timeoutSeconds };
            command.Parameters.Add("@connection", SqlDbType.UniqueIdentifier).Value = originalBinding.ConnectionId.Value;
            command.Parameters.Add("@session", SqlDbType.Int).Value = originalBinding.SessionId!.Value;
            command.Parameters.Add("@login", SqlDbType.DateTime).Value = originalBinding.SessionEstablishedLocal!.Value;
            var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            // 完整可见性下原连接与原会话均不存在，已断开的不重连句柄不可能消费旧授权。
            return count == 0 ? Result(TargetSqlInvocationTerminationState.Terminated, "termination.original_connection_closed")
                : count < 0 ? Result(TargetSqlInvocationTerminationState.Unknown, "termination.permission_denied")
                : Result(TargetSqlInvocationTerminationState.Active, "termination.original_session_present");
        }
        catch (OperationCanceledException) { return Result(TargetSqlInvocationTerminationState.Unknown, "termination.cancelled"); }
        catch (Exception exception) when (TargetSqlClientException.CanClassify(exception))
        { return Result(TargetSqlInvocationTerminationState.Unknown, "termination.read_failed"); }
        TargetSqlInvocationTerminationEvidence Result(TargetSqlInvocationTerminationState state, string reason) => new(state, originalBinding, clock.GetUtcNow(), reason);
    }

    private sealed class Prepared(SqlConnection connection, TargetSqlBackupRequest request,
        BackupInvocationBinding binding, BackupDatabaseIdentity database) : IPreparedTargetSqlBackupInvocation
    {
        private int _consumed;
        public BackupInvocationBinding Binding => binding;
        public BackupDatabaseIdentity Database => database;
        public async Task<TargetSqlResult<TargetSqlBackupCompletion>> InvokeOnceAsync(Guid invocationPermitId, CancellationToken cancellationToken = default)
        {
            if (invocationPermitId == Guid.Empty || Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
                return TargetSqlResult.ConfirmedFailure<TargetSqlBackupCompletion>(TargetSqlFailureCode.CommandRejected, TargetSqlFailurePhase.RequestValidation);
            try
            {
                // 同一会话在 BACKUP 紧邻位置复核绑定，不能对同名重建数据库执行。
                await using var command = TargetSqlCommandFactory.CreateBackup(connection, request);
                command.CommandText = """
                    IF NOT EXISTS (SELECT 1 FROM sys.database_recovery_status WHERE database_id = DB_ID(@boundDatabase)
                        AND database_guid = @boundGuid AND family_guid = @boundFamily)
                    BEGIN RAISERROR(N'backup_identity_changed', 16, 1); RETURN; END;
                    DECLARE @path TABLE ([FileExists] int, [IsDirectory] int, [ParentExists] int);
                    INSERT INTO @path EXEC master.dbo.xp_fileexist @backupPath;
                    IF (SELECT COUNT(*) FROM @path) <> 1 OR EXISTS
                        (SELECT 1 FROM @path WHERE [FileExists] IS NULL OR [FileExists] <> 0
                            OR [IsDirectory] IS NULL OR [IsDirectory] <> 0 OR [ParentExists] IS NULL OR [ParentExists] <> 1)
                    BEGIN RAISERROR(N'backup_attempt_path_not_empty', 16, 1); RETURN; END;
                    """ + command.CommandText;
                command.Parameters.Add("@boundDatabase", SqlDbType.NVarChar, 128).Value = request.DatabaseName;
                command.Parameters.Add("@boundGuid", SqlDbType.UniqueIdentifier).Value = database.DatabaseGuid!.Value;
                command.Parameters.Add("@boundFamily", SqlDbType.UniqueIdentifier).Value = database.FamilyGuid!.Value;
                await command.ExecuteNonQueryAsync(cancellationToken);
                return TargetSqlResult.Succeeded(new TargetSqlBackupCompletion(request.BackupType, request.UseCopyOnly, request.UseChecksum, request.UseCompression));
            }
            catch (Exception exception) when (exception is OperationCanceledException || TargetSqlClientException.CanClassify(exception))
            {
                var failure = TargetSqlClientException.FromBackupExecution(exception);
                return failure.Certainty == TargetSqlClientFailureCertainty.Indeterminate
                    ? TargetSqlResult.Indeterminate<TargetSqlBackupCompletion>(failure.FailureCode, TargetSqlFailurePhase.BackupExecution)
                    : TargetSqlResult.ConfirmedFailure<TargetSqlBackupCompletion>(failure.FailureCode, TargetSqlFailurePhase.BackupExecution);
            }
        }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _consumed, 1);
            await connection.DisposeAsync();
        }
    }
}
