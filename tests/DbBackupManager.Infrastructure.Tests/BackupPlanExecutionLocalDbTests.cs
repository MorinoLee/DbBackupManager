using DbBackupManager.Application.BackupSets;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.FileStorage;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.TargetSql;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupPlanExecutionLocalDbTests(PlatformDatabaseSqlServerFixture platform)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Theory]
    [InlineData(BackupStorageMode.LocalOnly)]
    [InlineData(BackupStorageMode.LocalAndRemote)]
    public async Task FullAndDifferentialCompleteUsingRealSqlAndProtectedFiles(BackupStorageMode mode)
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, mode);
        var full = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        var fullCode = await test.Runner.RunTaskOnceAsync(new(full, Guid.NewGuid()));
        Assert.True(fullCode == BackupExecutionContractCode.Succeeded, $"code={fullCode}; {await test.DiagnosticAsync(full)}");
        await test.AssertCompletedAsync(full, BackupType.Full);
        var diff = await test.CreateTaskAsync(BackupRunPurpose.PlanDifferential);
        Assert.Equal(BackupExecutionContractCode.Succeeded, await test.Runner.RunTaskOnceAsync(new(diff, Guid.NewGuid())));
        await test.AssertCompletedAsync(diff, BackupType.Differential);
        await using var db = platform.CreateContext();
        var f = await db.BackupSets.SingleAsync(x => x.TaskId == full);
        var d = await db.BackupSets.SingleAsync(x => x.TaskId == diff);
        Assert.Equal(f.Id, d.BaseBackupSetId);
        Assert.Equal(f.Metadata.BackupSetGuid.Value, d.Metadata.DifferentialBaseGuid.Value);
        Assert.Equal(f.Metadata.CheckpointLsn.Value, d.Metadata.DifferentialBaseLsn.Value);
        Assert.Equal(d.Metadata.DifferentialBaseLsn.Value, d.Metadata.DatabaseBackupLsn.Value);
        Assert.Equal(2, test.Invocations);
        Assert.Equal(0, test.PlanDeletions);
    }

    [Fact]
    public async Task RemoteOnlyReleasesDifferentialStagingOnlyAfterItsBaseIsConfirmed()
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.RemoteOnly);
        var full = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        var fullCode = await test.Runner.RunTaskOnceAsync(new(full, Guid.NewGuid()));
        Assert.True(fullCode == BackupExecutionContractCode.Succeeded, $"code={fullCode}; {await test.DiagnosticAsync(full)}");
        await test.AssertCompletedAsync(full, BackupType.Full);
        var before = await test.AssessAsync();
        var fullLocal = before.Single(item => item.Location == BackupFileLocation.Local);
        Assert.Contains(fullLocal.Copies, copy => copy.Reason == RecoveryProtectionReason.BaselineUnresolved);
        Assert.All(fullLocal.Copies, copy => Assert.False(PlanLocationRecovery.IsStagingReleasable(copy)));
        await using (var db = platform.CreateContext())
        {
            var attempt = await db.BackupAttempts.SingleAsync(item => item.TaskId == full);
            Assert.NotNull(attempt.RemoteValidatedAtUtc);
            Assert.Null(attempt.LocalCleanupCompletedAtUtc);
            Assert.Equal(BackupTaskStage.Cleanup, (await db.BackupTasks.SingleAsync(item => item.Id == full)).CurrentStage);
        }

        var diff = await test.CreateTaskAsync(BackupRunPurpose.PlanDifferential);
        var diffCode = await test.Runner.RunTaskOnceAsync(new(diff, Guid.NewGuid()));
        Assert.True(diffCode == BackupExecutionContractCode.Succeeded, $"code={diffCode}; {await test.DiagnosticAsync(diff)}");
        await using (var db = platform.CreateContext())
        {
            var diffAttempt = await db.BackupAttempts.SingleAsync(item => item.TaskId == diff);
            Assert.NotNull(diffAttempt.LocalCleanupCompletedAtUtc);
            Assert.NotNull(diffAttempt.RemoteValidatedAtUtc);
            Assert.True(diffAttempt.LocalCleanupCompletedAtUtc >= diffAttempt.RemoteValidatedAtUtc);
            var diffFiles = await db.BackupFiles.Where(item => item.TaskId == diff).ToArrayAsync();
            Assert.Equal(BackupFileStatus.Deleted, Assert.Single(diffFiles, item => item.Location == BackupFileLocation.Local).Status);
            Assert.Equal(BackupFileStatus.Available, Assert.Single(diffFiles, item => item.Location == BackupFileLocation.Remote).Status);
            var fullAttempt = await db.BackupAttempts.SingleAsync(item => item.TaskId == full);
            Assert.Null(fullAttempt.LocalCleanupCompletedAtUtc);
            Assert.Equal(BackupFileStatus.Available, (await db.BackupFiles.SingleAsync(item => item.TaskId == full && item.Location == BackupFileLocation.Local)).Status);
            var fullSet = await db.BackupSets.SingleAsync(item => item.TaskId == full);
            Assert.NotNull(await test.Store.FindQualifiedFullAsync(test.DatabaseId, fullSet.Metadata.BackupSetGuid.Value!.Value));
        }

        Assert.False(await test.Staging.RunOnceAsync(test.DatabaseId));
        var after = await test.AssessAsync();
        Assert.Contains(after.Single(item => item.Location == BackupFileLocation.Local).Copies, copy => copy.Reason == RecoveryProtectionReason.ActiveDifferentialBaseline);
        Assert.Equal(1, test.PlanDeletions);
        Assert.Equal(2, test.Invocations);
    }

    [Theory]
    [InlineData("between_gates", 1, BackupTaskStatus.Failed)]
    [InlineData("after_g1", 2, BackupTaskStatus.NeedsAttention)]
    [InlineData("after_diff", 2, BackupTaskStatus.Succeeded)]
    public async Task ExternalFullTimingDoesNotInventAdmissionOrOverwriteHistoricalDependency(string timing, int invocations, BackupTaskStatus expected)
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
        var full = await test.CreateTaskAsync(BackupRunPurpose.PlanFull);
        await test.Runner.RunTaskOnceAsync(new(full, Guid.NewGuid()));
        if (timing == "between_gates") test.AfterFirstGate = test.ExternalFullAsync;
        if (timing == "after_g1") test.BeforeDifferential = test.ExternalFullAsync;
        if (timing == "after_diff") test.AfterDifferential = test.ExternalFullAsync;
        var diff = await test.CreateTaskAsync(BackupRunPurpose.PlanDifferential);
        await test.Runner.RunTaskOnceAsync(new(diff, Guid.NewGuid()));
        await using var db = platform.CreateContext();
        Assert.True(expected == (await db.BackupTasks.SingleAsync(x => x.Id == diff)).Status, await test.DiagnosticAsync(diff));
        Assert.Equal(invocations, test.Invocations);
        var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == diff);
        if (timing == "between_gates")
        {
            Assert.Equal(BackupInvocationStatus.Prepared, attempt.BackupInvocationStatus);
            Assert.False(await db.BackupInvocationAuthorizations.AnyAsync(x => x.AttemptId == attempt.Id));
        }
        else
        {
            Assert.Equal(BackupInvocationStatus.Succeeded, attempt.BackupInvocationStatus);
            var set = await db.BackupSets.SingleAsync(x => x.TaskId == diff);
            Assert.Equal(timing == "after_g1" ? DifferentialBaselineConclusion.Unmanaged : DifferentialBaselineConclusion.Verified, set.Assessment.Conclusion);
            Assert.Contains(await db.BackupSetEvidence.Where(x => x.AttemptId == attempt.Id && x.Kind == BackupSetEvidenceKind.ActiveBaseline).ToArrayAsync(),
                x => x.Assessment.Conclusion == DifferentialBaselineConclusion.Unmanaged);
            Assert.Equal(2, await db.BackupSets.CountAsync(x => x.DatabaseId == test.DatabaseId));
        }
    }
}

/// <summary>使用本机 LocalDB 和指向本次临时目录的回环 SMB；SQL、摘要、句柄和平台事务均为真实适配器。</summary>
internal sealed class PlanExecutionTestEnvironment : IAsyncDisposable, ITargetSqlInvocationPreparation, ITargetSqlActiveBaselineReader,
    ITargetSqlInvocationTerminationProbe
{
    private readonly PlatformDatabaseSqlServerFixture _platform;
    private readonly SqlConnection _master;
    private readonly string _databaseName;
    private readonly string _root;
    private readonly TrackingFactory _factory;
    private readonly SqlClientTargetSqlInvocation _sql;
    private readonly LocalFiles _local;
    private BackupPlan _plan = null!;
    private int _gates;
    private PlanExecutionTestEnvironment(PlatformDatabaseSqlServerFixture platform, SqlConnection master, string databaseName, string root,
        TrackingFactory factory, SqlClientTargetSqlInvocation sql, LocalFiles local)
    { _platform = platform; _master = master; _databaseName = databaseName; _root = root; _factory = factory; _sql = sql; _local = local; }
    public BackupPlanTaskRunner Runner { get; private set; } = null!;
    public PlanRemoteStagingCleaner Staging { get; private set; } = null!;
    public BackupPlanTaskReconciler Reconciler { get; private set; } = null!;
    public IBackupPlanExecutionStore Store { get; private set; } = null!;
    public IBackupFileContentVerifier FileVerifier { get; private set; } = null!;
    public int Invocations { get; private set; }
    public int PlanDeletions => _local.PlanDeletions;
    public int PlanWrites => _local.PlanWrites;
    public Guid DatabaseId => _plan.DatabaseId;
    public string TargetDatabaseName => _databaseName;
    public string NewSqlPath() => Path.Combine(_root, $"adapter-{Guid.NewGuid():N}.bak");
    public SqlClientTargetSqlInvocation SqlAdapter(bool allowDifferential = true) => allowDifferential ? _sql
        : new(new SyntheticCredentials(), new LocalSqlConnections(_master.ConnectionString, _factory),
            new DifferentialBackupAllowance(new ConfigurationBuilder().Build(), NullLogger<DifferentialBackupAllowance>.Instance), TimeProvider.System);
    public TargetSqlConnectionInput SqlInput => new("synthetic-" + _databaseName, Guid.NewGuid(), true, true, "合成 LocalDB 测试", 30);
    public Func<Task>? AfterFirstGate { get; set; }
    public Func<Task>? BeforeDifferential { get; set; }
    public Func<Task>? AfterDifferential { get; set; }
    public Func<Task>? AfterBackupResponse { get; set; }
    public bool MetadataUnavailable { get; set; }
    public ITargetSqlInvocationTerminationProbe? RestrictedTerminationProbe { get; set; }
    public int TerminationReads { get; private set; }
    public int MetadataReads { get; private set; }
    internal PlatformDbContext CreateTrackedContext() => _factory.CreateDbContext();
    public async Task ExpireLeaseAsync(Guid taskId)
    {
        await using var db = _platform.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [BackupTasks] SET [LeaseAcquiredAtUtc] = {DateTimeOffset.UtcNow.AddMinutes(-5)}, [LeaseExpiresAtUtc] = {DateTimeOffset.UtcNow.AddSeconds(-1)} WHERE [Id] = {taskId} AND [LeaseToken] IS NOT NULL");
    }

    public static async Task<PlanExecutionTestEnvironment> CreateAsync(PlatformDatabaseSqlServerFixture platform, BackupStorageMode mode,
        PlanExecutionCommitFault? fault = null, params IInterceptor[] interceptors)
    {
        var name = $"DbBPE_{Guid.NewGuid():N}";
        var root = Path.Combine(Path.GetTempPath(), $"DbBPE_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "source"));
        Directory.CreateDirectory(Path.Combine(root, "remote"));
        var master = new SqlConnection(new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB",
            InitialCatalog = "master",
            IntegratedSecurity = true,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = true,
            Pooling = false,
            ConnectRetryCount = 0
        }.ConnectionString);
        PlanExecutionTestEnvironment? environment = null;
        try
        {
            await master.OpenAsync();
            await using (var create = new SqlCommand($"CREATE DATABASE [{name}];", master)) await create.ExecuteNonQueryAsync();
            await WaitUntilRecoveryIdentityIsVisibleAsync(master, name);
            var factory = new TrackingFactory(platform, interceptors);
            var connections = new LocalSqlConnections(master.ConnectionString, factory);
            var credentials = new SyntheticCredentials();
            var version = Version.Parse((string)(await new SqlCommand("SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'));", master).ExecuteScalarAsync())!);
            var allowance = new DifferentialBackupAllowance(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{DifferentialBackupAllowance.ConfigurationSection}:0:Version"] = $"{version.Major}.{version.Minor}",
                [$"{DifferentialBackupAllowance.ConfigurationSection}:0:Reason"] = "仅限本次随机 LocalDB 合成数据测试，不登记目标版本验收"
            }).Build(), NullLogger<DifferentialBackupAllowance>.Instance);
            var sql = new SqlClientTargetSqlInvocation(credentials, connections, allowance, TimeProvider.System);
            var local = new LocalFiles(factory);
            environment = new(platform, master, name, root, factory, sql, local);
            await environment.SeedAsync(mode);
            IBackupExecutionOperationStore operations = new BackupExecutionOperationStore(factory, TimeProvider.System);
            IBackupInvocationAuthorizationStore authorizations = new BackupInvocationAuthorizationStore(factory, TimeProvider.System);
            IBackupPlanExecutionStore store = new RenewalAwareStore(new BackupPlanExecutionStore(factory, TimeProvider.System), factory);
            if (fault is not null)
            { operations = fault.Wrap(operations); authorizations = fault.Wrap(authorizations); store = fault.Wrap(store); }
            ITargetSqlBackupMetadataReader reader = new ObservedMetadataReader(environment, new SqlClientTargetSqlBackupMetadataReader(credentials, connections));
            var reconciliation = new BackupMetadataReconciliationService(reader, new BackupMetadataReconciliationLookup(factory), new BackupSetRegistrationStore(factory));
            var adapter = new BackupPlanFileAdapter([local]);
            var simple = new BackupFileStorageAdapter([local]);
            var stages = new BackupPlanReadOnlyStages(store, operations, reconciliation, new SqlClientTargetSqlReadOnlyProbe(credentials, connections),
                adapter, simple, environment, authorizations, TimeProvider.System);
            environment.Store = store;
            environment.FileVerifier = adapter;
            IBackupPlanFileTransferExecutor planTransfer = adapter;
            if (fault is not null) planTransfer = fault.Wrap(planTransfer);
            var deletion = new BackupFileStorageAdapter([local], new BackupArtifactDeletionGuard(factory, TimeProvider.System), new PlanCopyDeletionCoordinator(factory, TimeProvider.System));
            var staging = new PlanRemoteStagingCleaner(new PlanLocationRetentionStore(factory), store, simple, deletion, TimeProvider.System);
            environment.Runner = new(store, operations, authorizations, environment, environment, simple, simple, adapter, planTransfer, stages, staging, TimeProvider.System);
            environment.Staging = staging;
            environment.Reconciler = new(store, stages, TimeProvider.System);
            return environment;
        }
        catch
        {
            if (environment is not null) await environment.DisposeAsync();
            else
            {
                try { if (master.State == System.Data.ConnectionState.Open) await DropAsync(master, name); }
                finally { await master.DisposeAsync(); DeleteTestDirectory(root); }
            }
            throw;
        }
    }
    private async Task SeedAsync(BackupStorageMode mode)
    {
        var credential = new CredentialReference(Guid.NewGuid(), $"合成凭据-{Guid.NewGuid():N}", CredentialKind.SmbPassword,
            "synthetic-user", "synthetic-protected", "dp-v1");
        var sqlCredential = new CredentialReference(Guid.NewGuid(), $"合成SQL-{Guid.NewGuid():N}", CredentialKind.SqlPassword,
            "synthetic-user", "synthetic-protected", "dp-v1");
        var server = new DatabaseServer(Guid.NewGuid(), $"ss-{Guid.NewGuid():N}"[..15], Path.Combine(_root, "source"),
            new(FileTransferProtocol.Smb, "localhost", null, SmbRoot("source"), credential.Id, null));
        var instance = new DatabaseInstance(Guid.NewGuid(), server.Id, "SyntheticInstance", $"synthetic-sql-{Guid.NewGuid():N}", sqlCredential.Id, true, true, "合成测试", 30);
        var database = new ManagedDatabase(Guid.NewGuid(), instance.Id, _databaseName, false, true, DateTimeOffset.UtcNow, "SIMPLE", "ONLINE");
        database.SetManaged(true);
        var remote = new StorageTarget(Guid.NewGuid(), $"合成远端-{Guid.NewGuid():N}", new(FileTransferProtocol.Smb, "localhost", null, SmbRoot("remote"), credential.Id, null));
        await using var db = _platform.CreateContext();
        db.AddRange(credential, sqlCredential, server, instance, database, remote);
        await db.SaveChangesAsync();
        var definition = new BackupPlanDefinition(BackupPlanMode.FullAndDifferentialAndLog,
            new(BackupScheduleType.Daily, new(2, 0), BackupWeekdays.None), new(BackupScheduleType.Daily, new(3, 0), BackupWeekdays.None), new(15),
            "UTC", mode, mode == BackupStorageMode.LocalOnly ? null : remote.Id, mode == BackupStorageMode.RemoteOnly ? null : 14, mode == BackupStorageMode.LocalOnly ? null : 14,
            true, false, 1, 1, 1);
        _plan = BackupPlan.Create(Guid.NewGuid(), database.Id, "合成显式执行计划", Guid.NewGuid(), definition, DateTimeOffset.UtcNow);
        await new BackupPlanStore(_factory).AddAsync(_plan);
    }
    public async Task<Guid> CreateTaskAsync(BackupRunPurpose purpose)
    {
        var id = Guid.NewGuid();
        await using var db = _platform.CreateContext();
        var managed = await db.ManagedDatabases.SingleAsync(x => x.Id == DatabaseId);
        var instance = await db.DatabaseInstances.SingleAsync(x => x.Id == managed.InstanceId);
        var source = await db.DatabaseServers.SingleAsync(x => x.Id == instance.ServerId);
        var remote = _plan.CurrentVersion.StorageTargetId is { } target ? await db.StorageTargets.SingleAsync(x => x.Id == target) : null;
        var task = BackupTask.ForPlan(id, _plan.Id, _plan.CurrentVersionId, purpose, BackupTaskTriggerType.Manual, null);
        var snapshot = BackupTaskSnapshot.ForPlan(task, _plan.CurrentVersion, _plan.Name, purpose,
            new(source.Id, source.Name, instance.Id, instance.Name, DatabaseId, _databaseName),
            new(instance.ConnectionAddress, instance.SqlCredentialReferenceId, true, true, "合成测试", 30),
            new(source.LocalBackupRootPath, "v3", new(FileTransferProtocol.Smb, "localhost", null, SmbRoot("source"), source.StagingCredentialReferenceId, null)),
            _plan.CurrentVersion.StorageTargetId is null ? null : new(remote!.Id, new(FileTransferProtocol.Smb, "localhost", null, SmbRoot("remote"), remote.CredentialReferenceId, null)));
        db.AddRange(task, snapshot);
        await db.SaveChangesAsync();
        return id;
    }
    public Task<IReadOnlyList<PlanLocationAssessment>> AssessAsync() =>
        new PlanLocationRetentionStore(_factory).AssessAsync(DatabaseId, DateTimeOffset.UtcNow);

    public async Task<string> DiagnosticAsync(Guid taskId)
    {
        await using var db = _platform.CreateContext();
        var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
        var operations = await db.BackupPlanExecutionOperations.Where(x => x.TaskId == taskId).ToArrayAsync();
        var set = await db.BackupSets.SingleOrDefaultAsync(x => x.TaskId == taskId);
        var evidence = await db.BackupSetEvidence.Where(x => x.TaskId == taskId).ToArrayAsync();
        return $"task={task.Status}/{task.CurrentStage}/{task.ErrorCode}; file={_local.LastOperation}; ops={string.Join(',', operations.Select(x => $"{x.Kind}:{x.State}:{x.Facts.Outcome}"))}; metadata={(set is not null ? BackupPlanExecutionFactRules.MetadataPassed(set, evidence, task.BackupType) : false)}";
    }
    public async Task AssertCompletedAsync(Guid taskId, BackupType type)
    {
        await using var db = _platform.CreateContext();
        var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
        Assert.True(task.Status == BackupTaskStatus.Succeeded, await DiagnosticAsync(taskId));
        var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == taskId);
        Assert.Equal(BackupInvocationStatus.Succeeded, attempt.BackupInvocationStatus);
        Assert.NotNull(attempt.LocalVerifiedAtUtc);
        var set = await db.BackupSets.SingleAsync(x => x.TaskId == taskId);
        Assert.True(set.SqlSuccessObserved);
        Assert.False(set.HasMetadataConflict);
        Assert.Equal(type, set.Metadata.Type.Value);
        var originalSql = await db.BackupPlanExecutionOperations.SingleAsync(x => x.AttemptId == attempt.Id && x.Kind == BackupExecutionOperationKind.SqlResult);
        if (originalSql.Facts.PlatformCompletedAtUtc is { } platformCompletion)
        {
            Assert.Equal(BackupCompletionTimeSource.PlatformObserved, set.Completion.Source);
            Assert.Equal(platformCompletion, set.Completion.CompletedAtUtc);
            Assert.Equal(BackupCompletionTimeReason.PlatformObserved, set.Completion.ReasonCode);
        }
        else
        {
            Assert.Equal(BackupExecutionOperationState.Reserved, originalSql.State);
            Assert.NotEqual(BackupCompletionTimeSource.PlatformObserved, set.Completion.Source);
            if (set.Completion.Source == BackupCompletionTimeSource.SqlLocalTime)
            {
                Assert.Equal(BackupCompletionTimeReason.SqlLocalConverted, set.Completion.ReasonCode);
                Assert.Equal(TimeSpan.Zero, set.Completion.CompletedAtUtc!.Value.Offset);
            }
            else
            {
                Assert.Equal(BackupCompletionTimeSource.Unknown, set.Completion.Source);
                Assert.Null(set.Completion.CompletedAtUtc);
                Assert.StartsWith("completion.", set.Completion.ReasonCode, StringComparison.Ordinal);
            }
        }
        Assert.Equal(DateTimeKind.Unspecified, set.Metadata.SqlFinishedLocal.Value!.Value.Kind);
        var copies = await db.BackupFiles.Where(x => x.TaskId == taskId).ToArrayAsync();
        if (type == BackupType.Full)
        {
            var rows = await db.BackupSetEvidence.Where(x => x.AttemptId == attempt.Id).ToArrayAsync();
            var localOp = await db.BackupPlanExecutionOperations.SingleAsync(x => x.AttemptId == attempt.Id && x.Kind == BackupExecutionOperationKind.VerifyLocal && x.State == BackupExecutionOperationState.Applied);
            var qualified = await Store.FindQualifiedFullAsync(DatabaseId, set.Metadata.BackupSetGuid.Value!.Value);
            Assert.True(qualified is not null, $"health={BackupPlanExecutionFactRules.MetadataPassed(set, rows)}; sql={attempt.BackupInvocationStatus}; length={attempt.SourceLengthBytes}; content={localOp.Facts.Content.State}/{localOp.Facts.Content.LengthBytes}; local={copies.First().Status}/{copies.First().LengthBytes}; pathMatches={copies.First().Path == attempt.WorkerSourceFilePath}");
        }
        Assert.Equal(_plan.CurrentVersion.StorageMode == BackupStorageMode.LocalOnly ? 1 : 2, copies.Length);
        Assert.All(copies, x => { Assert.Equal(set.Id, x.BackupSetId); Assert.Equal(BackupFileStatus.Available, x.Status); });
        var authorization = await db.BackupInvocationAuthorizations.SingleAsync(x => x.AttemptId == attempt.Id);
        Assert.NotNull(authorization.TerminalObservedAtUtc);
        Assert.Equal(BackupInvocationBindingState.Known, authorization.Binding.State);
        Assert.True(await db.BackupPlanExecutionOperations.AnyAsync(x => x.AttemptId == attempt.Id && x.Kind == BackupExecutionOperationKind.VerifyLocal
            && x.State == BackupExecutionOperationState.Applied && x.Facts.Content.DigestAlgorithm == "SHA256"));
    }
    public async Task ExternalFullAsync()
    {
        await using var connection = new SqlConnection(_master.ConnectionString);
        await connection.OpenAsync();
        await using var session = new SqlClientTargetSqlBackupSession(connection);
        await session.ExecuteBackupAsync(new(_databaseName, Path.Combine(_root, $"external-{Guid.NewGuid():N}.bak"), BackupRunPurpose.PlanFull, true, false, 60), CancellationToken.None);
    }
    public async Task<TargetSqlResult<IPreparedTargetSqlBackupInvocation>> PrepareAsync(TargetSqlConnectionInput connection, TargetSqlBackupRequest request,
        Guid callerIncarnationId, CancellationToken cancellationToken = default)
    {
        var result = await _sql.PrepareAsync(connection, request, callerIncarnationId, cancellationToken);
        return result.IsSucceeded ? TargetSqlResult.Succeeded<IPreparedTargetSqlBackupInvocation>(new CountedInvocation(this, result.Value!, request.BackupType)) : result;
    }
    public async Task<BackupActiveMetadataEvidence> ReadAsync(TargetSqlConnectionInput connection, TargetSqlActiveBaselineRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _sql.ReadAsync(connection, request, cancellationToken);
        if (++_gates == 1 && AfterFirstGate is { } action) await action();
        return result;
    }
    public Task<TargetSqlInvocationTerminationEvidence> ReadAsync(TargetSqlConnectionInput connection, Guid invocationPermitId,
        BackupInvocationBinding originalBinding, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        TerminationReads++;
        return (RestrictedTerminationProbe ?? _sql).ReadAsync(connection, invocationPermitId, originalBinding, timeoutSeconds, cancellationToken);
    }
    public async Task UseRestrictedTerminationVisibilityAsync()
    {
        // 权限反例只在本次随机数据库内创建无登录用户，随数据库一起清理。
        await using var connection = new SqlConnection(_master.ConnectionString);
        await connection.OpenAsync();
        await using var create = new SqlCommand($"USE [{_databaseName}]; CREATE USER [DbBPE_TerminationReader] WITHOUT LOGIN;", connection);
        await create.ExecuteNonQueryAsync();
        RestrictedTerminationProbe = new SqlClientTargetSqlInvocation(new SyntheticCredentials(),
            new LocalSqlConnections(_master.ConnectionString, _factory, _databaseName),
            new DifferentialBackupAllowance(new ConfigurationBuilder().Build(), NullLogger<DifferentialBackupAllowance>.Instance), TimeProvider.System);
    }
    public async ValueTask DisposeAsync()
    {
        try { await DropAsync(_master, _databaseName); }
        finally
        {
            await _master.DisposeAsync();
            DeleteTestDirectory(_root);
        }
    }
    // CREATE DATABASE 返回后，sys.database_recovery_status 的 GUID 可能仍为空；准备探针把空值当成无效响应。
    private static async Task WaitUntilRecoveryIdentityIsVisibleAsync(SqlConnection master, string databaseName)
    {
        await using var command = new SqlCommand("""
            SELECT rs.database_guid, rs.family_guid
            FROM sys.database_recovery_status AS rs
            WHERE rs.database_id = DB_ID(@database);
            """, master);
        command.Parameters.Add("@database", System.Data.SqlDbType.NVarChar, 128).Value = databaseName;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var ready = false;
            await using (var reader = await command.ExecuteReaderAsync())
                ready = await reader.ReadAsync() && !reader.IsDBNull(0) && !reader.IsDBNull(1);
            if (ready) return;
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException("合成数据库的 database_guid 与 family_guid 在限定时间内没有就绪。");
            await Task.Delay(200);
        }
    }
    private static void DeleteTestDirectory(string root)
    {
        var full = Path.GetFullPath(root);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("DbBPE_", StringComparison.Ordinal))
            throw new InvalidOperationException("拒绝清理非本次测试目录。");
        Directory.Delete(full, true);
    }
    private static async Task DropAsync(SqlConnection connection, string database)
    {
        if (!database.StartsWith("DbBPE_", StringComparison.Ordinal) || database.Length != 38) throw new InvalidOperationException("拒绝清理非随机合成数据库。");
        await using var drop = new SqlCommand($"IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END; EXEC msdb.dbo.sp_delete_database_backuphistory @database_name=@name;", connection);
        drop.Parameters.AddWithValue("@name", database);
        await drop.ExecuteNonQueryAsync();
    }
    private sealed class CountedInvocation(PlanExecutionTestEnvironment test, IPreparedTargetSqlBackupInvocation inner, BackupType type) : IPreparedTargetSqlBackupInvocation
    {
        public BackupInvocationBinding Binding => inner.Binding;
        public BackupDatabaseIdentity Database => inner.Database;
        public async Task<TargetSqlResult<TargetSqlBackupCompletion>> InvokeOnceAsync(Guid invocationPermitId, CancellationToken cancellationToken = default)
        {
            test._factory.AssertNoLiveContext();
            test.Invocations++;
            if (type == BackupType.Differential && test.BeforeDifferential is { } before) await before();
            var result = await inner.InvokeOnceAsync(invocationPermitId, cancellationToken);
            if (type == BackupType.Differential && test.AfterDifferential is { } after) await after();
            if (test.AfterBackupResponse is { } response) await response();
            return result;
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class SyntheticCredentials : ITargetSqlCredentialResolver
    {
        public ValueTask<TargetSqlCredentialResolution> ResolveSqlPasswordAsync(Guid credentialReferenceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(TargetSqlCredentialResolution.Succeeded(TargetSqlCredentialLease.CreateAndClear("synthetic", "synthetic".ToCharArray())));
    }
    private sealed class ObservedMetadataReader(PlanExecutionTestEnvironment test, ITargetSqlBackupMetadataReader inner) : ITargetSqlBackupMetadataReader
    {
        public Task<TargetSqlBackupMetadataEvidence> ReadAsync(TargetSqlConnectionInput connection, TargetSqlBackupMetadataRequest request,
            CancellationToken cancellationToken = default)
        {
            test.MetadataReads++;
            return test.MetadataUnavailable ? Task.FromResult(SqlClientTargetSqlBackupMetadataReader.Failed(TargetSqlFailureCode.AuthorizationDenied))
                : inner.ReadAsync(connection, request, cancellationToken);
        }
    }
    private sealed class LocalSqlConnections(string connectionString, TrackingFactory platform, string? restrictedDatabase = null)
        : ITargetSqlInvocationConnectionFactory, ITargetSqlClientSessionFactory
    {
        public async Task<SqlConnection> OpenAsync(TargetSqlConnectionInput input, TargetSqlCredentialLease credential, CancellationToken token)
        {
            platform.AssertNoLiveContext();
            var connection = new SqlConnection(connectionString);
            try
            {
                await connection.OpenAsync(token);
                if (restrictedDatabase is not null)
                {
                    await using var impersonate = new SqlCommand($"USE [{restrictedDatabase}]; EXECUTE AS USER = 'DbBPE_TerminationReader';", connection);
                    await impersonate.ExecuteNonQueryAsync(token);
                }
                return connection;
            }
            catch { await connection.DisposeAsync(); throw; }
        }
        async Task<ITargetSqlClientSession> ITargetSqlClientSessionFactory.OpenAsync(TargetSqlConnectionInput input, TargetSqlCredentialLease credential, CancellationToken token) =>
            new SqlClientTargetSqlSession(await OpenAsync(input, credential, token));
    }
    private sealed class TrackingFactory(PlatformDatabaseSqlServerFixture platform, params IInterceptor[] interceptors) : IDbContextFactory<PlatformDbContext>
    {
        private readonly List<(PlatformDbContext Context, Guid? Renewal)> _created = [];
        private readonly AsyncLocal<Guid?> _renewal = new();
        public PlatformDbContext CreateDbContext()
        {
            var context = platform.CreateContext(interceptors);
            lock (_created) _created.Add((context, _renewal.Value));
            return context;
        }
        public void AssertNoLiveContext()
        {
            lock (_created) foreach (var entry in _created)
                // 外部 I/O 不持有自己的 Context；独立续租允许同时使用短 Context。
                if (entry.Renewal is null || entry.Renewal == _renewal.Value)
                    Assert.Throws<ObjectDisposedException>(() => entry.Context.ChangeTracker.Entries().ToArray());
        }
        public IDisposable BeginRenewal()
        {
            var previous = _renewal.Value;
            var id = Guid.NewGuid();
            _renewal.Value = id;
            return new RenewalScope(this, id, previous);
        }
        private sealed class RenewalScope(TrackingFactory owner, Guid id, Guid? previous) : IDisposable
        {
            public void Dispose()
            {
                try
                {
                    lock (owner._created) foreach (var entry in owner._created.Where(x => x.Renewal == id))
                        Assert.Throws<ObjectDisposedException>(() => entry.Context.ChangeTracker.Entries().ToArray());
                }
                finally { owner._renewal.Value = previous; }
            }
        }
    }
    private sealed class RenewalAwareStore(IBackupPlanExecutionStore inner, TrackingFactory factory) : IBackupPlanExecutionStore
    {
        public Task<BackupExecutionContractResult<BackupPlanExecutionSession>> OpenAsync(Guid taskId, Guid requestMutationId,
            bool reconciliationOnly, Guid leaseToken, string owner, DateTimeOffset now, DateTimeOffset expires,
            CancellationToken cancellationToken = default) =>
            inner.OpenAsync(taskId, requestMutationId, reconciliationOnly, leaseToken, owner, now, expires, cancellationToken);
        public Task<BackupPlanExecutionSession?> RefreshAsync(Guid taskId, Guid leaseToken, CancellationToken cancellationToken = default) =>
            inner.RefreshAsync(taskId, leaseToken, cancellationToken);
        public async Task<BackupExecutionContractCode> RenewAsync(Guid taskId, Guid leaseToken, DateTimeOffset expires,
            CancellationToken cancellationToken = default)
        {
            using var scope = factory.BeginRenewal();
            return await inner.RenewAsync(taskId, leaseToken, expires, cancellationToken);
        }
        public Task<QualifiedManagedFull?> FindQualifiedFullAsync(Guid databaseId, Guid backupSetGuid, CancellationToken cancellationToken = default) =>
            inner.FindQualifiedFullAsync(databaseId, backupSetGuid, cancellationToken);
        public Task<BackupExecutionContractCode> ApplyAsync(LeaseHandle lease, ReserveBackupExecutionOperation identity,
            IVerifiedBackupFileHandle? protectedFile = null, IVerifiedBackupFileHandle? protectedDependency = null,
            CancellationToken cancellationToken = default) => inner.ApplyAsync(lease, identity, protectedFile, protectedDependency, cancellationToken);
        public Task<BackupExecutionContractCode> SuspendAsync(LeaseHandle lease, Guid mutationId, string reason,
            CancellationToken cancellationToken = default) => inner.SuspendAsync(lease, mutationId, reason, cancellationToken);
        public Task<BackupExecutionContractCode> CompleteRemoteCleanupAsync(LeaseHandle lease, bool retained,
            CancellationToken cancellationToken = default) => inner.CompleteRemoteCleanupAsync(lease, retained, cancellationToken);
        public Task<BackupExecutionContractCode> CloseDeferredRemoteCleanupAsync(Guid taskId,
            CancellationToken cancellationToken = default) => inner.CloseDeferredRemoteCleanupAsync(taskId, cancellationToken);
    }
    private string SmbRoot(string child)
    {
        var path = Path.Combine(_root, child);
        return $"{path[0]}$\\{path[3..]}";
    }
    private sealed class LocalFiles(TrackingFactory platform) : IFileStorageProtocolSessionFactory
    {
        public string? LastOperation { get; private set; }
        public int PlanDeletions { get; private set; }
        public int PlanWrites { get; private set; }
        public FileTransferProtocol Protocol => FileTransferProtocol.Smb;
        public async ValueTask<IFileStorageProtocolSession> OpenAsync(BackupFileEndpointInput endpoint, CancellationToken cancellationToken)
        {
            platform.AssertNoLiveContext();
            LastOperation = $"open:{endpoint.Protocol}/{endpoint.Host}/{endpoint.RootPath}";
            var factory = new FileStorage.BackupPlanSmbAdapterTests.CurrentIdentitySmbFactory();
            return new Session(this, await factory.OpenAsync(endpoint, cancellationToken));
        }
        private sealed class Session(LocalFiles owner, IFileStorageProtocolSession inner) : IFileStorageProtocolSession, IProtectedPlanFileSession
        {
            public NativePlanFileGuard OpenGuarded(string path, bool create, bool rename)
            {
                owner.LastOperation = $"guard:{create}/{rename}";
                if (create && !Path.GetFileName(path).StartsWith(".dbbackup-capability-", StringComparison.Ordinal)) owner.PlanWrites++;
                try { return ((IProtectedPlanFileSession)inner).OpenGuarded(path, create, rename); }
                catch (Exception exception) { Assert.Fail($"guard: {exception.GetType().Name}/{exception.Message}; inner={exception.InnerException?.Message}; length={path.Length}"); throw; }
            }
            public void RenameGuarded(NativePlanFileGuard file, string finalPath)
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                owner.LastOperation = "rename";
                if (!Path.GetFileName(file.Path).StartsWith(".dbbackup-capability-", StringComparison.Ordinal)) owner.PlanWrites++;
                try { ((IProtectedPlanFileSession)inner).RenameGuarded(file, finalPath); }
                catch (Exception exception) { Assert.Fail($"rename: {exception.GetType().Name}/{exception.Message}; inner={exception.InnerException?.Message}; length={finalPath.Length}"); throw; }
            }
            public async ValueTask PrepareParentDirectoryAsync(string path, CancellationToken token)
            {
                owner.LastOperation = "prepare";
                try { await inner.PrepareParentDirectoryAsync(path, token); }
                catch (FileStorageAdapterException exception) { Assert.Fail($"prepare: {exception.Code}/{exception.Phase}; length={path.Length}"); throw; }
            }
            public ValueTask<BackupFileMetadata> InspectAsync(string path, CancellationToken token) => inner.InspectAsync(path, token);
            public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token) => inner.OpenReadAsync(path, token);
            public ValueTask<Stream> CreateNewAsync(string path, CancellationToken token) => inner.CreateNewAsync(path, token);
            public ValueTask RenameNoReplaceAsync(string from, string to, CancellationToken token) => inner.RenameNoReplaceAsync(from, to, token);
            public ValueTask DeleteAsync(string path, CancellationToken token)
            {
                if (!Path.GetFileName(path).StartsWith(".dbbackup-capability-", StringComparison.Ordinal)) owner.PlanDeletions++;
                return inner.DeleteAsync(path, token);
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();

            public void VerifyGuardedCapabilities(NativePlanFileGuard file) =>
                ((IProtectedPlanFileSession)inner).VerifyGuardedCapabilities(file);
        }
    }
}
