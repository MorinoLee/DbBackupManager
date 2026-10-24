using System.Data;
using System.Data.Common;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;
using Microsoft.Data.SqlClient;

namespace DbBackupManager.Infrastructure.TargetSql;

internal interface ITargetSqlLogReadinessQuery
{
    Task<TargetSqlDatabaseReadinessObservation> ReadDatabaseStateAsync(
        TargetSqlLogReadinessRequest request,
        CancellationToken cancellationToken);

    Task<TargetSqlRecoveryStatusObservation> ReadRecoveryStatusAsync(
        TargetSqlLogReadinessRequest request,
        int? databaseId,
        CancellationToken cancellationToken);
}

internal interface ITargetSqlLogReadinessSession
{
    Task<TargetSqlLogReadinessEvidence> ReadLogReadinessAsync(
        TargetSqlLogReadinessRequest request,
        CancellationToken cancellationToken);
}

internal sealed partial class SqlClientTargetSqlSession : ITargetSqlLogReadinessSession
{
    private const string DatabaseStateSql = """
        SELECT [database_id], [name], [state_desc], [recovery_model_desc]
        FROM [sys].[databases]
        WHERE [name] = @database;
        """;

    private const string RecoveryStatusSql = """
        SELECT [database_id], [database_guid], [family_guid], [recovery_fork_guid],
            [first_recovery_fork_guid], [last_log_backup_lsn]
        FROM [sys].[database_recovery_status]
        WHERE [database_id] = @databaseId AND DB_NAME([database_id]) = @database;
        """;

    public async Task<TargetSqlLogReadinessEvidence> ReadLogReadinessAsync(
        TargetSqlLogReadinessRequest request,
        CancellationToken cancellationToken)
    {
        var database = await ReadDatabaseStateAsync(request, cancellationToken);
        var recovery = database.IsOnline && database.QueryComplete
            ? await ReadRecoveryStatusAsync(request, database.SqlDatabaseId, cancellationToken)
            : NotAttemptedRecovery(database);
        TargetSqlDatabaseReadinessObservation? recheck = null;
        TargetSqlRecoveryStatusObservation? recoveryRecheck = null;
        bool? stable = null;
        if (database.IsOnline && database.QueryComplete)
        {
            recheck = await ReadDatabaseStateAsync(request, cancellationToken);
            if (recheck.QueryComplete)
            {
                stable = SameDatabaseState(database, recheck);
                if (stable == true)
                {
                    if (!recovery.QueryComplete)
                    {
                        // 首次恢复目录证据不足时，两次库状态一致不能证明恢复身份/fork 一致。
                        stable = null;
                    }
                    else
                    {
                        recoveryRecheck = await ReadRecoveryStatusAsync(
                            request,
                            database.SqlDatabaseId,
                            cancellationToken);
                        if (!recoveryRecheck.QueryComplete)
                        {
                            // 复核读取不足只表示无法判断；保留复核原证据，不伪造来源冲突。
                            stable = null;
                        }
                        else if (!SameRecoveryStatus(recovery, recoveryRecheck))
                        {
                            stable = false;
                            recovery = recovery with
                            {
                                Issues = recovery.Issues
                                    .Append(new("recovery_status", BackupMetadataReadProblem.SourceConflict))
                                    .ToArray(),
                            };
                        }
                    }
                }

                if (stable == false)
                {
                    database = database with
                    {
                        Issues = database.Issues.Append(new("state", BackupMetadataReadProblem.SourceConflict)).ToArray(),
                    };
                }
            }
        }

        return new(
            request.PlatformDatabaseId,
            request.DatabaseName,
            request.ExpectedDatabase,
            null,
            database,
            recovery,
            recheck,
            IdentityMatches(request.ExpectedDatabase, recovery.Database),
            stable,
            recoveryRecheck);
    }

    private Task<TargetSqlDatabaseReadinessObservation> ReadDatabaseStateAsync(
        TargetSqlLogReadinessRequest request,
        CancellationToken cancellationToken) =>
        _logReadinessQuery?.ReadDatabaseStateAsync(request, cancellationToken)
        ?? ReadDatabaseStateSqlAsync(request, cancellationToken);

    private Task<TargetSqlRecoveryStatusObservation> ReadRecoveryStatusAsync(
        TargetSqlLogReadinessRequest request,
        int? databaseId,
        CancellationToken cancellationToken) =>
        _logReadinessQuery?.ReadRecoveryStatusAsync(request, databaseId, cancellationToken)
        ?? ReadRecoveryStatusSqlAsync(request, databaseId, cancellationToken);

    private async Task<TargetSqlDatabaseReadinessObservation> ReadDatabaseStateSqlAsync(
        TargetSqlLogReadinessRequest request,
        CancellationToken cancellationToken)
    {
        var observedAt = _clock.GetUtcNow();
        try
        {
            await using var command = new SqlCommand(DatabaseStateSql, _connection)
            {
                CommandTimeout = request.TimeoutSeconds,
            };
            command.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = request.DatabaseName;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<TargetSqlDatabaseReadinessObservation>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var issues = new List<BackupMetadataReadIssue>();
                var id = ReadInt(reader, "database_id", issues);
                var name = ReadString(reader, "name", issues);
                var state = MapState(ReadString(reader, "state_desc", issues), issues);
                var recovery = MapRecoveryModel(ReadString(reader, "recovery_model_desc", issues), issues);
                rows.Add(new(id, name, state, recovery, issues.Count == 0, observedAt, issues.AsReadOnly()));
            }

            if (rows.Count == 1)
            {
                return rows[0] with { QueryComplete = rows[0].Issues.Count == 0 };
            }

            var reason = rows.Count == 0
                ? new BackupMetadataReadIssue("record", BackupMetadataReadProblem.NotFound)
                : new BackupMetadataReadIssue("record", BackupMetadataReadProblem.NotUnique);
            return new(
                rows.FirstOrDefault()?.SqlDatabaseId,
                rows.FirstOrDefault()?.Name,
                rows.FirstOrDefault()?.State,
                rows.FirstOrDefault()?.RecoveryModel,
                QueryComplete: false,
                observedAt,
                [reason],
                TargetSqlFailureCode.NotFoundOrNotVisible,
                TargetSqlFailurePhase.DatabaseReadiness);
        }
        catch (OperationCanceledException)
        {
            return FailedState(observedAt, TargetSqlFailureCode.Cancelled);
        }
        catch (Exception exception) when (TargetSqlClientException.CanClassify(exception))
        {
            return FailedState(observedAt, TargetSqlClientException.From(exception, TargetSqlClientOperation.ReadOnlyCommand).FailureCode);
        }
        catch (Exception exception) when (TargetSqlClientException.IsResponseShapeFailure(exception))
        {
            return FailedState(observedAt, TargetSqlFailureCode.InvalidResponse);
        }
    }

    private async Task<TargetSqlRecoveryStatusObservation> ReadRecoveryStatusSqlAsync(
        TargetSqlLogReadinessRequest request,
        int? databaseId,
        CancellationToken cancellationToken)
    {
        var observedAt = _clock.GetUtcNow();
        try
        {
            await using var command = new SqlCommand(RecoveryStatusSql, _connection)
            {
                CommandTimeout = request.TimeoutSeconds,
            };
            command.Parameters.Add("@databaseId", SqlDbType.Int).Value = databaseId ?? 0;
            command.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = request.DatabaseName;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<TargetSqlRecoveryStatusObservation>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var issues = new List<BackupMetadataReadIssue>();
                ReadInt(reader, "database_id", issues);
                var databaseGuid = ReadGuid(reader, "database_guid", issues);
                var familyGuid = ReadGuid(reader, "family_guid", issues);
                var currentFork = ReadGuid(reader, "recovery_fork_guid", issues);
                var firstFork = ReadGuid(reader, "first_recovery_fork_guid", issues);
                var (state, lsn) = ReadLastLogBackupLsn(reader, issues);
                rows.Add(new(
                    true,
                    new(databaseGuid, familyGuid),
                    currentFork,
                    firstFork,
                    state,
                    lsn,
                    issues.Count == 0,
                    observedAt,
                    issues.AsReadOnly()));
            }

            if (rows.Count == 1)
            {
                return rows[0] with { QueryComplete = rows[0].Issues.Count == 0 };
            }

            return new(
                true,
                null,
                null,
                null,
                TargetSqlLogLsnState.Unknown,
                null,
                false,
                observedAt,
                [new("record", rows.Count == 0 ? BackupMetadataReadProblem.NotFound : BackupMetadataReadProblem.NotUnique)],
                TargetSqlFailureCode.NotFoundOrNotVisible,
                TargetSqlFailurePhase.RecoveryStatusRead);
        }
        catch (OperationCanceledException)
        {
            return FailedRecovery(observedAt, TargetSqlFailureCode.Cancelled);
        }
        catch (Exception exception) when (TargetSqlClientException.CanClassify(exception))
        {
            return FailedRecovery(observedAt, TargetSqlClientException.From(exception, TargetSqlClientOperation.ReadOnlyCommand).FailureCode);
        }
        catch (Exception exception) when (TargetSqlClientException.IsResponseShapeFailure(exception))
        {
            return FailedRecovery(observedAt, TargetSqlFailureCode.InvalidResponse);
        }
    }

    private static TargetSqlRecoveryStatusObservation NotAttemptedRecovery(
        TargetSqlDatabaseReadinessObservation database) =>
        new(
            false,
            null,
            null,
            null,
            TargetSqlLogLsnState.Unknown,
            null,
            false,
            database.ObservedAtUtc,
            [new("recovery_status", BackupMetadataReadProblem.NotAttempted)]);

    private static TargetSqlDatabaseReadinessObservation FailedState(
        DateTimeOffset observedAt,
        TargetSqlFailureCode code) =>
        new(
            null,
            null,
            null,
            null,
            false,
            observedAt,
            [new("source", BackupMetadataReadProblem.ReadFailed)],
            code,
            TargetSqlFailurePhase.DatabaseReadiness);

    private static TargetSqlRecoveryStatusObservation FailedRecovery(
        DateTimeOffset observedAt,
        TargetSqlFailureCode code) =>
        new(
            true,
            null,
            null,
            null,
            TargetSqlLogLsnState.Unknown,
            null,
            false,
            observedAt,
            [new("source", BackupMetadataReadProblem.ReadFailed)],
            code,
            TargetSqlFailurePhase.RecoveryStatusRead);

    private static bool SameDatabaseState(
        TargetSqlDatabaseReadinessObservation before,
        TargetSqlDatabaseReadinessObservation after) =>
        before.SqlDatabaseId == after.SqlDatabaseId
        && string.Equals(before.Name, after.Name, StringComparison.Ordinal)
        && before.State == after.State
        && before.RecoveryModel == after.RecoveryModel;

    private static bool SameRecoveryStatus(
        TargetSqlRecoveryStatusObservation before,
        TargetSqlRecoveryStatusObservation after) =>
        before.Database == after.Database
        && before.CurrentRecoveryForkId == after.CurrentRecoveryForkId
        && before.LastLogBackupLsnState == after.LastLogBackupLsnState
        && before.LastLogBackupLsn == after.LastLogBackupLsn;

    private static bool? IdentityMatches(
        BackupDatabaseIdentity expected,
        BackupDatabaseIdentity? actual)
    {
        if (actual?.DatabaseGuid is not { } databaseGuid
            || actual.FamilyGuid is not { } familyGuid)
        {
            return null;
        }

        return expected.DatabaseGuid == databaseGuid && expected.FamilyGuid == familyGuid;
    }

    private static int? ReadInt(DbDataReader reader, string column, List<BackupMetadataReadIssue> issues)
    {
        if (!TryOrdinal(reader, column, issues, out var ordinal)) return null;
        if (reader.IsDBNull(ordinal))
        {
            issues.Add(new(column, BackupMetadataReadProblem.NullValue));
            return null;
        }

        try
        {
            return reader.GetInt32(ordinal);
        }
        catch (Exception exception) when (exception is InvalidCastException or OverflowException or FormatException)
        {
            issues.Add(new(column, BackupMetadataReadProblem.InvalidValue));
            return null;
        }
    }

    private static string? ReadString(DbDataReader reader, string column, List<BackupMetadataReadIssue> issues)
    {
        if (!TryOrdinal(reader, column, issues, out var ordinal)) return null;
        if (reader.IsDBNull(ordinal))
        {
            issues.Add(new(column, BackupMetadataReadProblem.NullValue));
            return null;
        }

        try
        {
            return reader.GetString(ordinal);
        }
        catch (InvalidCastException)
        {
            issues.Add(new(column, BackupMetadataReadProblem.InvalidValue));
            return null;
        }
    }

    private static Guid? ReadGuid(DbDataReader reader, string column, List<BackupMetadataReadIssue> issues)
    {
        if (!TryOrdinal(reader, column, issues, out var ordinal)) return null;
        if (reader.IsDBNull(ordinal)) return null;
        try
        {
            var value = reader.GetGuid(ordinal);
            if (value == Guid.Empty)
            {
                issues.Add(new(column, BackupMetadataReadProblem.InvalidValue));
                return null;
            }

            return value;
        }
        catch (InvalidCastException)
        {
            issues.Add(new(column, BackupMetadataReadProblem.InvalidValue));
            return null;
        }
    }

    internal static (TargetSqlLogLsnState State, BackupLsn? Lsn) ReadLastLogBackupLsn(
        DbDataReader reader,
        List<BackupMetadataReadIssue> issues)
    {
        if (!TryOrdinal(reader, "last_log_backup_lsn", issues, out var ordinal))
        {
            return (TargetSqlLogLsnState.Unknown, null);
        }

        if (reader.IsDBNull(ordinal))
        {
            return (TargetSqlLogLsnState.ObservedNull, null);
        }

        try
        {
            return (TargetSqlLogLsnState.Known, new BackupLsn(reader.GetDecimal(ordinal)));
        }
        catch (Exception exception) when (exception is InvalidCastException or ArgumentException
            or OverflowException or FormatException)
        {
            issues.Add(new("last_log_backup_lsn", BackupMetadataReadProblem.InvalidValue));
            return (TargetSqlLogLsnState.Unknown, null);
        }
    }

    private static bool TryOrdinal(
        DbDataReader reader,
        string column,
        List<BackupMetadataReadIssue> issues,
        out int ordinal)
    {
        try
        {
            ordinal = reader.GetOrdinal(column);
            return true;
        }
        catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentException)
        {
            issues.Add(new(column, BackupMetadataReadProblem.MissingColumn));
            ordinal = -1;
            return false;
        }
    }

    private static TargetSqlDatabaseState? MapState(
        string? value,
        List<BackupMetadataReadIssue> issues)
    {
        if (value is null) return null;
        var state = TargetSqlDatabaseInfoMapper.MapState(value);
        if (state == TargetSqlDatabaseState.Unknown)
        {
            issues.Add(new("state_desc", BackupMetadataReadProblem.InvalidValue));
            return null;
        }

        return state;
    }

    private static TargetSqlRecoveryModel? MapRecoveryModel(
        string? value,
        List<BackupMetadataReadIssue> issues)
    {
        if (value is null) return null;
        var model = TargetSqlDatabaseInfoMapper.MapRecoveryModel(value);
        if (model == TargetSqlRecoveryModel.Unknown)
        {
            issues.Add(new("recovery_model_desc", BackupMetadataReadProblem.InvalidValue));
            return null;
        }

        return model;
    }
}
