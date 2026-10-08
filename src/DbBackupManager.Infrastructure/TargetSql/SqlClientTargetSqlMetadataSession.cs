using System.Data;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;
using Microsoft.Data.SqlClient;

namespace DbBackupManager.Infrastructure.TargetSql;

internal sealed partial class SqlClientTargetSqlSession : ITargetSqlMetadataSession
{
    private const string HistorySelect = """
        SELECT bs.backup_set_uuid AS BackupSetGUID, bs.database_guid AS BindingID, bs.family_guid AS FamilyGUID,
            bs.first_recovery_fork_guid AS FirstRecoveryForkID, bs.last_recovery_fork_guid AS RecoveryForkID,
            bs.fork_point_lsn AS ForkPointLSN,
            CONVERT(smallint, CASE bs.type WHEN 'D' THEN 1 WHEN 'I' THEN 5 WHEN 'L' THEN 2 ELSE 0 END) AS BackupType,
            bs.first_lsn AS FirstLSN, bs.last_lsn AS LastLSN, bs.checkpoint_lsn AS CheckpointLSN,
            bs.database_backup_lsn AS DatabaseBackupLSN, bs.differential_base_lsn AS DifferentialBaseLSN,
            bs.differential_base_guid AS DifferentialBaseGUID, bs.is_copy_only AS IsCopyOnly,
            bs.has_backup_checksums AS HasBackupChecksums, media.is_compressed AS Compressed,
            bs.is_damaged AS IsDamaged, bs.is_snapshot AS IsSnapshot, bs.has_incomplete_metadata AS HasIncompleteMetadata,
            bs.backup_start_date AS BackupStartDate, bs.backup_finish_date AS BackupFinishDate
        FROM msdb.dbo.backupset AS bs JOIN msdb.dbo.backupmediaset AS media ON media.media_set_id = bs.media_set_id
        """;

    public async Task<TargetSqlBackupMetadataEvidence> ReadMetadataAsync(TargetSqlBackupMetadataRequest request,
        TargetSqlServerInfo server, CancellationToken cancellationToken)
    {
        var header = await ReadSingleAsync("RESTORE HEADERONLY FROM DISK = @path;", request,
            BackupSetEvidenceSource.BackupHeader, null, cancellationToken);
        // EXISTS 避免条带介质的 JOIN 重复；专属路径独立定位，不用头部 GUID 掩盖历史冲突。
        var history = await ReadSingleAsync(HistorySelect + """

            WHERE bs.database_name = @database COLLATE Latin1_General_100_BIN2
              AND EXISTS (SELECT 1 FROM msdb.dbo.backupmediafamily AS mf
                  WHERE mf.media_set_id = bs.media_set_id AND mf.physical_device_name = @path COLLATE Latin1_General_100_BIN2);
            """, request, BackupSetEvidenceSource.Msdb, null, cancellationToken);
        var active = await ReadActiveAsync(request, cancellationToken);
        var guids = new[] { header.Metadata.DifferentialBaseGuid.Value, history.Metadata.DifferentialBaseGuid.Value }
            .Concat(active.Baseline.DataFileBases.Select(x => x.BaseBackupSetGuid)).OfType<Guid>().Distinct().ToArray();
        var fulls = new List<BackupMetadataSourceEvidence>();
        foreach (var guid in guids)
            fulls.Add(await ReadSingleAsync(HistorySelect + " WHERE bs.backup_set_uuid = @guid;", request,
                BackupSetEvidenceSource.Msdb, guid, cancellationToken));
        TimeSpan? offset = null;
        TimeZoneInfo? zone = null;
        try
        {
            // 旧版本不调用不存在的时区函数；展示名称不作为可靠时区 ID。
            await using var clock = Command(server.MajorVersion >= 16
                ? "SELECT SYSDATETIMEOFFSET(), CURRENT_TIMEZONE_ID();" : "SELECT SYSDATETIMEOFFSET();", request);
            await using var clockReader = await clock.ExecuteReaderAsync(cancellationToken);
            if (await clockReader.ReadAsync(cancellationToken))
            {
                offset = clockReader.GetFieldValue<DateTimeOffset>(0).Offset;
                if (server.MajorVersion >= 16 && !clockReader.IsDBNull(1))
                {
                    try { zone = TimeZoneInfo.FindSystemTimeZoneById(clockReader.GetString(1)); }
                    catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException) { }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (TargetSqlClientException.CanClassify(exception)) { }
        catch (Exception exception) when (TargetSqlClientException.IsResponseShapeFailure(exception)) { offset = null; zone = null; }
        return new(header, history, active, fulls.AsReadOnly(), offset, zone);
    }

    private async Task<BackupMetadataSourceEvidence> ReadSingleAsync(string sql, TargetSqlBackupMetadataRequest request,
        BackupSetEvidenceSource source, Guid? requestedGuid, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = Command(sql, request);
            command.Parameters.Add("@path", SqlDbType.NVarChar, 4000).Value = request.AttemptFilePath;
            command.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = request.DatabaseName;
            if (requestedGuid is { } guid) command.Parameters.Add("@guid", SqlDbType.UniqueIdentifier).Value = guid;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<BackupMetadataSourceEvidence>();
            while (await reader.ReadAsync(cancellationToken)) rows.Add(new BackupMetadataRowMapper(reader).Map(source));
            if (rows.Count == 1) return rows[0] with { RequestedBackupSetGuid = requestedGuid };
            return new(source, new(), rows.Count == 0 ? BaselineEvidenceStatus.HistoryNotFound : BaselineEvidenceStatus.MissingFields,
                [new("record", rows.Count == 0 ? BackupMetadataReadProblem.NotFound : BackupMetadataReadProblem.NotUnique)],
                RequestedBackupSetGuid: requestedGuid, AmbiguousRecords: rows.Select(x => x.Metadata).ToArray());
        }
        catch (OperationCanceledException) { return Failure(TargetSqlFailureCode.Cancelled); }
        catch (Exception exception) when (TargetSqlClientException.CanClassify(exception))
        { return Failure(TargetSqlClientException.From(exception, TargetSqlClientOperation.ReadOnlyCommand).FailureCode); }
        catch (Exception exception) when (TargetSqlClientException.IsResponseShapeFailure(exception))
        { return Failure(TargetSqlFailureCode.InvalidResponse); }
        BackupMetadataSourceEvidence Failure(TargetSqlFailureCode code) =>
            SqlClientTargetSqlBackupMetadataReader.FailedSource(source, code) with { RequestedBackupSetGuid = requestedGuid };
    }

    private async Task<BackupActiveMetadataEvidence> ReadActiveAsync(TargetSqlBackupMetadataRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var database = request.DatabaseName.Replace("]", "]]", StringComparison.Ordinal);
            await using var command = Command($"""
                SELECT rs.database_guid AS BindingID, rs.family_guid AS FamilyGUID,
                    rs.recovery_fork_guid AS RecoveryForkID, rs.first_recovery_fork_guid AS FirstRecoveryForkID,
                    df.differential_base_guid AS DifferentialBaseGUID, df.differential_base_lsn AS DifferentialBaseLSN
                FROM [{database}].sys.database_files AS df CROSS JOIN sys.database_recovery_status AS rs
                WHERE df.type = 0 AND rs.database_id = DB_ID(@database) ORDER BY df.file_id;
                """, request);
            command.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = request.DatabaseName;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var files = new List<ActiveDataFileBaselineEvidence>();
            var issues = new List<BackupMetadataReadIssue>();
            BackupDatabaseIdentity? identity = null;
            Guid? fork = null, diagnosticFirst = null;
            var status = BaselineEvidenceStatus.Complete;
            while (await reader.ReadAsync(cancellationToken))
            {
                var mapper = new BackupMetadataRowMapper(reader);
                var rowIdentity = new BackupDatabaseIdentity(mapper.GuidField("BindingID").Value, mapper.GuidField("FamilyGUID").Value);
                var rowFork = mapper.GuidField("RecoveryForkID").Value;
                if (identity is not null && (identity != rowIdentity || fork != rowFork)) status = BaselineEvidenceStatus.SourceConflict;
                if (identity is null) { identity = rowIdentity; fork = rowFork; }
                // 目录起始分支只供诊断；缺失不影响活动分支的完整性。
                var diagnostic = new BackupMetadataRowMapper(reader);
                diagnosticFirst = diagnostic.Field("FirstRecoveryForkID", value => (Guid)value).Value;
                var guid = mapper.GuidField("DifferentialBaseGUID").Value;
                var lsn = mapper.Lsn("DifferentialBaseLSN").Value;
                files.Add(new(guid, lsn, mapper.Issues.Count == 0 ? BaselineEvidenceStatus.Complete : BaselineEvidenceStatus.MissingFields));
                issues.AddRange(mapper.Issues);
            }
            if (files.Count == 0) { status = BaselineEvidenceStatus.MissingFields; issues.Add(new("record", BackupMetadataReadProblem.NotFound)); }
            return new(new(identity ?? new(null, null), fork, files, status), diagnosticFirst, issues.AsReadOnly());
        }
        catch (OperationCanceledException) { return Failure(TargetSqlFailureCode.Cancelled); }
        catch (Exception exception) when (TargetSqlClientException.CanClassify(exception))
        { return Failure(TargetSqlClientException.From(exception, TargetSqlClientOperation.ReadOnlyCommand).FailureCode); }
        catch (Exception exception) when (TargetSqlClientException.IsResponseShapeFailure(exception))
        { return Failure(TargetSqlFailureCode.InvalidResponse); }
        static BackupActiveMetadataEvidence Failure(TargetSqlFailureCode code) => SqlClientTargetSqlBackupMetadataReader.Failed(code).Active;
    }

    private SqlCommand Command(string sql, TargetSqlBackupMetadataRequest request) => new(sql, _connection) { CommandTimeout = request.TimeoutSeconds };
}
