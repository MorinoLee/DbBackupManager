using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Infrastructure.TargetSql;
using Xunit.Abstractions;

namespace DbBackupManager.Infrastructure.Tests.TargetSql;

public sealed class BackupMetadataReaderFailureTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CancelledRequestReturnsExplicitUnknownAndDoesNotOpenConnection()
    {
        var factory = new FailingFactory(TargetSqlFailureCode.TimedOut);
        var reader = new SqlClientTargetSqlBackupMetadataReader(new MetadataTestCredentialResolver(), factory);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var evidence = await reader.ReadAsync(Connection(), new(Guid.NewGuid(), "synthetic", @"D:\Synthetic\attempt.bak", 1), cancelled.Token);
        Assert.Equal(TargetSqlFailureCode.Cancelled, evidence.Header.FailureCode);
        Assert.Equal(BaselineEvidenceStatus.MissingFields, evidence.Active.Baseline.Status);
        Assert.Equal(0, factory.Calls);
    }

    [Theory]
    [InlineData(TargetSqlFailureCode.TimedOut, BaselineEvidenceStatus.MissingFields)]
    [InlineData(TargetSqlFailureCode.AuthorizationDenied, BaselineEvidenceStatus.PermissionDenied)]
    public async Task ReadOnlyBoundaryRetainsSafeFailureCode(TargetSqlFailureCode code, BaselineEvidenceStatus status)
    {
        var reader = new SqlClientTargetSqlBackupMetadataReader(new MetadataTestCredentialResolver(), new FailingFactory(code));
        var evidence = await reader.ReadAsync(Connection(), new(Guid.NewGuid(), "synthetic", @"D:\Synthetic\attempt.bak", 1));
        Assert.Equal(code, evidence.History.FailureCode);
        Assert.Equal(status, evidence.History.Status);
        Assert.Equal(BackupMetadataState.Unknown, evidence.Header.Metadata.BackupSetGuid.State);
    }

    [Fact]
    public async Task MissingHistoryDoesNotHideReadableHeaderAndCurrentState()
    {
        await using var scope = new LocalDbBackupMetadataProofScope(output);
        await scope.InitializeAsync();
        var full = await scope.BackupAsync("synthetic", Domain.BackupPlans.BackupRunPurpose.PlanFull);
        await using var command = scope.Connection.CreateCommand();
        command.CommandText = "EXEC msdb.dbo.sp_delete_database_backuphistory @database_name=@name;";
        command.Parameters.AddWithValue("@name", scope.DatabaseName);
        await command.ExecuteNonQueryAsync();
        var evidence = await scope.ReadAdapterAsync(full.Path);
        Assert.Equal(BaselineEvidenceStatus.Complete, evidence.Header.Status);
        Assert.Equal(BaselineEvidenceStatus.HistoryNotFound, evidence.History.Status);
        Assert.Contains(new("record", BackupMetadataReadProblem.NotFound), evidence.History.Issues);
        Assert.Equal(full.Header.BackupSetGuid, evidence.Active.Baseline.DataFileBases[0].BaseBackupSetGuid);
        Assert.Equal(BaselineEvidenceStatus.HistoryNotFound, Assert.Single(evidence.ReferencedFulls).Status);
    }

    [Fact]
    public async Task AppendedSetsMakeBothFileAndHistoryAmbiguousRatherThanSelectingFirst()
    {
        await using var scope = new LocalDbBackupMetadataProofScope(output);
        await scope.InitializeAsync();
        var first = await scope.BackupAsync("synthetic", Domain.BackupPlans.BackupRunPurpose.PlanFull);
        await using var command = scope.Connection.CreateCommand();
        command.CommandText = $"BACKUP DATABASE [{scope.DatabaseName}] TO DISK=@path WITH CHECKSUM;";
        command.Parameters.AddWithValue("@path", first.Path);
        await command.ExecuteNonQueryAsync();
        var evidence = await scope.ReadAdapterAsync(first.Path);
        foreach (var source in new[] { evidence.Header, evidence.History })
        {
            Assert.Equal(BaselineEvidenceStatus.MissingFields, source.Status);
            Assert.Null(source.Metadata.BackupSetGuid.Value);
            Assert.Contains(new("record", BackupMetadataReadProblem.NotUnique), source.Issues);
            Assert.Equal(2, source.AmbiguousRecords!.Count);
        }
    }

    [Fact]
    public async Task SyntheticRestrictedUserCannotReadHeaderWhileReadableHistorySurvives()
    {
        await using var scope = new LocalDbBackupMetadataProofScope(output);
        await scope.InitializeAsync();
        var full = await scope.BackupAsync("synthetic", Domain.BackupPlans.BackupRunPurpose.PlanFull);
        await LocalDbBackupMetadataProofScope.ExecuteAsync(scope.Connection, "CREATE USER [SyntheticMetadataReader] WITHOUT LOGIN;");
        await using var restricted = scope.NewConnection();
        await restricted.OpenAsync();
        await LocalDbBackupMetadataProofScope.ExecuteAsync(restricted, "EXECUTE AS USER=N'SyntheticMetadataReader';");
        // 此处直接走同一个生产只读会话，测试仅改变连接上的合成身份。
        await using var session = new SqlClientTargetSqlSession(restricted);
        var server = await session.ReadServerInfoAsync(default);
        var evidence = await session.ReadMetadataAsync(new(Guid.NewGuid(), scope.DatabaseName, full.Path, 60), server, default);
        Assert.Equal(TargetSqlFailureCode.AuthorizationDenied, evidence.Header.FailureCode);
        Assert.Equal(BaselineEvidenceStatus.PermissionDenied, evidence.Header.Status);
        Assert.Equal(BaselineEvidenceStatus.Complete, evidence.History.Status);
        Assert.Equal(full.Header.BackupSetGuid, evidence.History.Metadata.BackupSetGuid.Value);
        Assert.Null(evidence.Header.Metadata.BackupSetGuid.Value);
    }

    private static TargetSqlConnectionInput Connection() => new("synthetic-sql", Guid.NewGuid(), true, false, null, 30);

    [Fact]
    public async Task HistoryReadDeadlineIsTimedOutAndPreservesAlreadyReadHeader()
    {
        await using var scope = new LocalDbBackupMetadataProofScope(output);
        await scope.InitializeAsync();
        var full = await scope.BackupAsync("synthetic", Domain.BackupPlans.BackupRunPurpose.PlanFull);
        await using var locker = scope.NewConnection();
        await locker.OpenAsync();
        await using var transaction = (Microsoft.Data.SqlClient.SqlTransaction)await locker.BeginTransactionAsync();
        try
        {
            await using var command = locker.CreateCommand();
            command.Transaction = transaction;
            // 只锁本次随机数据库自己的备份记录；不修改任何已部署或其他测试记录。
            command.CommandText = "UPDATE msdb.dbo.backupset WITH (ROWLOCK) SET backup_finish_date=backup_finish_date WHERE backup_set_uuid=@guid AND database_name=@database;";
            command.Parameters.AddWithValue("@guid", full.Header.BackupSetGuid);
            command.Parameters.AddWithValue("@database", scope.DatabaseName);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            var evidence = await scope.ReadAdapterAsync(full.Path, timeoutSeconds: 1);
            Assert.Equal(BaselineEvidenceStatus.Complete, evidence.Header.Status);
            Assert.Equal(full.Header.BackupSetGuid, evidence.Header.Metadata.BackupSetGuid.Value);
            Assert.Equal(TargetSqlFailureCode.TimedOut, evidence.History.FailureCode);
            Assert.Equal(BaselineEvidenceStatus.MissingFields, evidence.History.Status);
        }
        finally { await transaction.RollbackAsync(); }
    }

    [Fact]
    public async Task ContradictorySyntheticHistoryIsReturnedAlongsideOriginalHeader()
    {
        await using var scope = new LocalDbBackupMetadataProofScope(output);
        await scope.InitializeAsync();
        var full = await scope.BackupAsync("synthetic", Domain.BackupPlans.BackupRunPurpose.PlanFull);
        await using var command = scope.Connection.CreateCommand();
        command.CommandText = "UPDATE msdb.dbo.backupset SET checkpoint_lsn=@lsn WHERE backup_set_uuid=@guid AND database_name=@database;";
        command.Parameters.AddWithValue("@lsn", full.Header.CheckpointLsn!.Value.Value + 1);
        command.Parameters.AddWithValue("@guid", full.Header.BackupSetGuid);
        command.Parameters.AddWithValue("@database", scope.DatabaseName);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        var evidence = await scope.ReadAdapterAsync(full.Path);
        _ = evidence.Header.Metadata.Merge(evidence.History.Metadata, out var conflict);
        Assert.True(conflict);
        Assert.Equal(full.Header.CheckpointLsn, evidence.Header.Metadata.CheckpointLsn.Value);
        Assert.Equal(new BackupLsn(full.Header.CheckpointLsn.Value.Value + 1), evidence.History.Metadata.CheckpointLsn.Value);
    }
    private sealed class FailingFactory(TargetSqlFailureCode code) : ITargetSqlClientSessionFactory
    {
        public int Calls { get; private set; }
        public Task<ITargetSqlClientSession> OpenAsync(TargetSqlConnectionInput input, TargetSqlCredentialLease credential, CancellationToken cancellationToken)
        {
            Calls++;
            throw code == TargetSqlFailureCode.TimedOut
                ? TargetSqlClientException.From(new TimeoutException("合成超时"), TargetSqlClientOperation.ReadOnlyCommand)
                : TargetSqlClientException.FromBackupErrorNumbers([229]);
        }
    }
}
