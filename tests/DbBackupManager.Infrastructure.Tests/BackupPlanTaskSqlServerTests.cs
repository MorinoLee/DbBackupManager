using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupPlanTaskSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    private static readonly DateTimeOffset Slot = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(BackupRunPurpose.PlanFull, BackupType.Full, false)]
    [InlineData(BackupRunPurpose.PlanDifferential, BackupType.Differential, false)]
    [InlineData(BackupRunPurpose.PlanLog, BackupType.Log, false)]
    [InlineData(BackupRunPurpose.AdHocCopyOnlyFull, BackupType.Full, true)]
    public async Task IdentityPurposeAndVersionParametersRoundTrip(BackupRunPurpose purpose, BackupType type, bool copyOnly)
    {
        var graph = await GraphAsync(database);
        var task = Task(graph.Plan, purpose);
        var snapshot = Snapshot(graph, task, purpose);
        await SaveAsync(database, task, snapshot);
        await using var db = database.CreateContext();
        var read = await db.BackupTasks.AsNoTracking().SingleAsync(x => x.Id == task.Id);
        var frozen = await db.BackupTaskSnapshots.AsNoTracking().SingleAsync(x => x.TaskId == task.Id);
        Assert.Equal(graph.Plan.Id, read.PlanId);
        Assert.Equal(graph.Plan.CurrentVersionId, read.PlanVersionId);
        Assert.Equal(type, read.BackupType);
        Assert.Equal(type, frozen.BackupType);
        Assert.Equal(purpose, frozen.Purpose);
        Assert.Equal(copyOnly, frozen.UseCopyOnly);
        Assert.Equal("v3", frozen.FileNameRuleVersion);
        Assert.Equal(14, frozen.LocalRetentionDays);
        var mapped = frozen.ToSnapshotModel();
        Assert.Equal(type, mapped.BackupType);
        Assert.Equal(purpose, mapped.Purpose);
        var plan = await db.BackupPlans.Include(x => x.Versions).SingleAsync(x => x.Id == graph.Plan.Id);
        plan.Revise(Guid.NewGuid(), Definition(28), Slot.AddHours(1));
        await db.SaveChangesAsync();
        await using var later = database.CreateContext();
        Assert.Equal(read.PlanVersionId, (await later.BackupTasks.SingleAsync(x => x.Id == task.Id)).PlanVersionId);
        Assert.Equal(14, (await later.BackupTaskSnapshots.SingleAsync(x => x.TaskId == task.Id)).LocalRetentionDays);
    }

    [Fact]
    public async Task PlanSlotUniquenessSeparatesTypesVersionsAndManualRuns()
    {
        var graph = await GraphAsync(database);
        var full = Task(graph.Plan, BackupRunPurpose.PlanFull, Slot);
        await SaveAsync(database, full, Snapshot(graph, full, BackupRunPurpose.PlanFull));
        var diff = Task(graph.Plan, BackupRunPurpose.PlanDifferential, Slot);
        await SaveAsync(database, diff, Snapshot(graph, diff, BackupRunPurpose.PlanDifferential));
        var log = Task(graph.Plan, BackupRunPurpose.PlanLog, Slot);
        await SaveAsync(database, log, Snapshot(graph, log, BackupRunPurpose.PlanLog));
        var other = await GraphAsync(database);
        var otherFull = Task(other.Plan, BackupRunPurpose.PlanFull, Slot);
        await SaveAsync(database, otherFull, Snapshot(other, otherFull, BackupRunPurpose.PlanFull));
        var duplicate = Task(graph.Plan, BackupRunPurpose.PlanFull, Slot);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(database, duplicate,
            Snapshot(graph, duplicate, BackupRunPurpose.PlanFull)));
        Assert.Contains("UX_BackupTasks_PlanVersion_Type_Slot", error.InnerException!.Message, StringComparison.Ordinal);
        graph.Plan.Revise(Guid.NewGuid(), Definition(28), Slot.AddHours(1));
        await using (var db = database.CreateContext())
        {
            // 已创建的计划只追加新版本；任务仍指向各自的不可变版本。
            db.BackupPlanVersions.Add(graph.Plan.CurrentVersion);
            await db.SaveChangesAsync();
        }
        var revised = Task(graph.Plan, BackupRunPurpose.PlanFull, Slot);
        await SaveAsync(database, revised, Snapshot(graph, revised, BackupRunPurpose.PlanFull));
        foreach (var purpose in new[] { BackupRunPurpose.PlanFull, BackupRunPurpose.PlanFull, BackupRunPurpose.AdHocCopyOnlyFull })
        {
            var manual = Task(graph.Plan, purpose);
            await SaveAsync(database, manual, Snapshot(graph, manual, purpose));
        }
    }

    [Fact]
    public async Task ConcurrentSameSlotCreatesExactlyOneTaskAndSnapshot()
    {
        var graph = await GraphAsync(database);
        var results = await System.Threading.Tasks.Task.WhenAll(InsertAsync(), InsertAsync());
        Assert.Equal(1, results.Count(x => x));
        await using var db = database.CreateContext();
        var tasks = await db.BackupTasks.Where(x => x.PlanId == graph.Plan.Id && x.ScheduledSlotAtUtc == Slot).ToArrayAsync();
        var only = Assert.Single(tasks);
        Assert.Equal(1, await db.BackupTaskSnapshots.CountAsync(x => x.TaskId == only.Id));
        async Task<bool> InsertAsync()
        {
            var task = Task(graph.Plan, BackupRunPurpose.PlanDifferential, Slot);
            try { await SaveAsync(database, task, Snapshot(graph, task, BackupRunPurpose.PlanDifferential)); return true; }
            catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 }) { return false; }
        }
    }

    [Fact]
    public async Task CompositeVersionForeignKeyRejectsAnotherPlansVersionAndDeletionIsRestrict()
    {
        var graph = await GraphAsync(database);
        var other = await GraphAsync(database);
        var task = Task(graph.Plan, BackupRunPurpose.PlanFull);
        await SaveAsync(database, task, Snapshot(graph, task, BackupRunPurpose.PlanFull));
        await using var db = database.CreateContext();
        var cross = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [BackupTasks] SET [PlanVersionId]={other.Plan.CurrentVersionId} WHERE [Id]={task.Id}"));
        Assert.Equal(547, cross.Number);
        Assert.Contains("FK_BackupTasks_BackupPlanVersions", cross.Message, StringComparison.Ordinal);
        var plan = await db.BackupPlans.Include(x => x.Versions).SingleAsync(x => x.Id == graph.Plan.Id);
        plan.Revise(Guid.NewGuid(), Definition(28), Slot.AddHours(1));
        await db.SaveChangesAsync();
        var versionDeletion = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM [BackupPlanVersions] WHERE [Id]={task.PlanVersionId}"));
        Assert.Equal(547, versionDeletion.Number);
        Assert.Contains("FK_BackupTasks_BackupPlanVersions", versionDeletion.Message, StringComparison.Ordinal);
        var deletion = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM [BackupTasks] WHERE [Id]={task.Id}"));
        Assert.Equal(547, deletion.Number);
        Assert.All(db.Model.FindEntityType(typeof(BackupTask))!.GetForeignKeys(), x => Assert.Equal(DeleteBehavior.Restrict, x.DeleteBehavior));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("purpose")]
    [InlineData("copyOnly")]
    [InlineData("database")]
    public async Task WriteBoundaryRejectsInconsistentSnapshotsAtomically(string violation)
    {
        var graph = await GraphAsync(database);
        var task = Task(graph.Plan, BackupRunPurpose.PlanDifferential);
        var snapshot = Snapshot(graph, task, BackupRunPurpose.PlanDifferential);
        await using var db = database.CreateContext();
        db.AddRange(task, snapshot);
        switch (violation)
        {
            case "type": db.Entry(snapshot).Property(x => x.BackupType).CurrentValue = BackupType.Full; break;
            case "purpose": db.Entry(snapshot).Property(x => x.Purpose).CurrentValue = null; break;
            case "copyOnly": db.Entry(snapshot).Property(x => x.UseCopyOnly).CurrentValue = true; break;
            case "database": db.Entry(snapshot).Property(x => x.DatabaseId).CurrentValue = Guid.NewGuid(); break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        await using var verify = database.CreateContext();
        Assert.False(await verify.BackupTasks.AnyAsync(x => x.Id == task.Id));
        Assert.False(await verify.BackupTaskSnapshots.AnyAsync(x => x.TaskId == task.Id));
    }

    [Fact]
    public async Task PlanTaskRequiresAtomicSnapshotAndBothIdentitiesRemainImmutable()
    {
        var graph = await GraphAsync(database);
        var task = Task(graph.Plan, BackupRunPurpose.PlanFull);
        await using (var missing = database.CreateContext())
        {
            missing.Add(task);
            await Assert.ThrowsAsync<InvalidOperationException>(() => missing.SaveChangesAsync());
        }
        await SaveAsync(database, task, Snapshot(graph, task, BackupRunPurpose.PlanFull));
        foreach (var id in new[] { task.Id, graph.Legacy.TaskId })
        {
            await using var db = database.CreateContext();
            var stored = await db.BackupTasks.SingleAsync(x => x.Id == id);
            db.Entry(stored).Property(x => x.BackupType).CurrentValue = BackupType.Differential;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using var snapshotDb = database.CreateContext();
        var frozen = await snapshotDb.BackupTaskSnapshots.SingleAsync(x => x.TaskId == task.Id);
        snapshotDb.Entry(frozen).Property(x => x.Purpose).CurrentValue = BackupRunPurpose.AdHocCopyOnlyFull;
        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshotDb.SaveChangesAsync());
    }

    [Fact]
    public async Task ExistingExecutionEntryDoesNotClaimPlanTasks()
    {
        var graph = await GraphAsync(database);
        var planTask = Task(graph.Plan, BackupRunPurpose.PlanFull);
        await SaveAsync(database, planTask, Snapshot(graph, planTask, BackupRunPurpose.PlanFull));
        Assert.DoesNotContain(
            typeof(IBackupTaskExecutionStore).GetMethods().Select(method => method.Name),
            name => name is "ClaimNextAsync" or "ClaimTaskAsync" or "RetryFailedAsync"
                or "RequestReconciliationAsync" or "ConfirmNeedsAttentionAsync");
        await using var db = database.CreateContext();
        Assert.Equal(BackupTaskStatus.Pending, (await db.BackupTasks.SingleAsync(x => x.Id == planTask.Id)).Status);
        Assert.False(await db.BackupAttempts.AnyAsync(x => x.TaskId == planTask.Id));
    }

    public static TheoryData<string, string, string, int> ConstraintViolations => new()
    {
        { "BackupTasks", "Identity", "[PlanId]=NULL", 515 },
        { "BackupTasks", "Identity", "[PlanVersionId]=NULL", 515 },
        { "BackupTasks", "Identity", "[PlanId]=NULL,[PlanVersionId]=NULL", 515 },
        { "BackupTasks", "BackupType", "[BackupType]=NULL", 515 },
        { "BackupTasks", "BackupType", "[BackupType]='Other'", 547 },
        { "BackupTasks", "CoveredDifferentialSlot", "[BackupType]='Differential',[CoveredDifferentialSlotUtc]='2026-10-08T00:00:00+00:00'", 547 },
        { "BackupTasks", "CoveredDifferentialSlot", "[CoveredDifferentialSlotUtc]='2026-10-08T08:00:00+08:00'", 547 },
        { "BackupTaskSnapshots", "BackupType", "[BackupType]=NULL", 515 },
        { "BackupTaskSnapshots", "BackupType", "[BackupType]='Other'", 547 },
        { "BackupTaskSnapshots", "Purpose", "[Purpose]=NULL,[BackupType]='Differential'", 547 },
        { "BackupTaskSnapshots", "Purpose", "[Purpose]='Other'", 547 },
        { "BackupTaskSnapshots", "Purpose", "[UseCopyOnly]=NULL", 515 },
        { "BackupTaskSnapshots", "Purpose", "[UseCopyOnly]=1", 547 },
        { "BackupTaskSnapshots", "Purpose", "[Purpose]='AdHocCopyOnlyFull',[UseCopyOnly]=0", 547 },
        { "BackupTaskSnapshots", "PlanPathVersion", "[FileNameRuleVersion]=NULL", 515 },
        { "BackupTaskSnapshots", "PlanPathVersion", "[FileNameRuleVersion]='v2'", 547 }
    };

    [Theory]
    [MemberData(nameof(ConstraintViolations))]
    public async Task NewConstraintsRejectInvalidAndRequiredNullValues(string table, string constraint, string assignments, int number)
    {
        var graph = await GraphAsync(database);
        var task = Task(graph.Plan, BackupRunPurpose.PlanFull);
        await SaveAsync(database, task, Snapshot(graph, task, BackupRunPurpose.PlanFull));
        await using var db = database.CreateContext();
        var sql = $"UPDATE [{table}] SET {assignments} WHERE [{(table == "BackupTasks" ? "Id" : "TaskId")}] = @id";
        var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(sql,
            new SqlParameter("@id", task.Id)));
        Assert.Equal(number, error.Number);
        if (number == 547) Assert.Contains($"CK_{table}_{constraint}", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigrationPreservesLegacyRowsAndActuallyRejectsDownAfterPlanTasksExist(bool legacyCopyOnly)
    {
        var temporary = new PlatformDatabaseSqlServerFixture();
        try
        {
            await temporary.InitializeAsync();
            var host = await BackupSetTestData.HostSnapshotAsync(temporary);
            await using var db = temporary.CreateContext();
            var migrator = db.GetService<IMigrator>();
            var beforeRemoval = db.Database.GetMigrations().Single(x =>
                x.EndsWith("_AddRemoteGapProjectionPendings", StringComparison.Ordinal));
            var previous = db.Database.GetMigrations().Single(x => x.EndsWith("_AddBackupSets", StringComparison.Ordinal));
            await migrator.MigrateAsync(beforeRemoval);
            var legacyTaskId = await InsertLegacyPolicyTaskAsync(db, host, legacyCopyOnly);
            await migrator.MigrateAsync(previous);
            Assert.Equal(0, await NewColumnCountAsync(db));
            await migrator.MigrateAsync(beforeRemoval);
            Assert.Equal(5, await NewColumnCountAsync(db));
            Assert.Equal(1, await LegacyIdentityCountAsync(db, legacyTaskId, legacyCopyOnly));
            var graph = await GraphAsync(temporary);
            var task = Task(graph.Plan, BackupRunPurpose.PlanFull, Slot, Slot.AddMinutes(-30));
            await SaveAsync(temporary, task, Snapshot(graph, task, BackupRunPurpose.PlanFull));
            // 停在删表迁移之前。后续迁移各自拥有事务；先退到本项要验证的迁移。
            var identityMigration = db.Database.GetMigrations().Single(x =>
                x.EndsWith("_AddBackupTaskPlanIdentity", StringComparison.Ordinal));
            await migrator.MigrateAsync(identityMigration);
            var history = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            var error = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(previous));
            Assert.Equal(51003, error.Number);
            Assert.Equal(5, await NewColumnCountAsync(db));
            Assert.Equal(history, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Equal(Slot.AddMinutes(-30), (await db.BackupTasks.SingleAsync(x => x.Id == task.Id)).CoveredDifferentialSlotUtc);
            Assert.Equal(BackupRunPurpose.PlanFull, (await db.BackupTaskSnapshots.SingleAsync(x => x.TaskId == task.Id)).Purpose);
            Assert.Equal(1, await LegacyIdentityCountAsync(db, legacyTaskId, legacyCopyOnly));
        }
        finally { await temporary.DisposeAsync(); }
    }

    [Fact]
    public async Task EmptyDatabaseMigratesToLatestWithoutBackupPolicies()
    {
        var temporary = new PlatformDatabaseSqlServerFixture();
        try
        {
            await temporary.InitializeAsync();
            await using var db = temporary.CreateContext();
            Assert.Equal(0, await TableCountAsync(db, "BackupPolicies"));
            Assert.Equal(0, await ColumnCountAsync(db, "BackupTasks", "PolicyId"));
            Assert.Equal(0, await PlanIdentityDefaultCountAsync(db));
            Assert.Contains("[PlanId] IS NOT NULL AND [PlanVersionId] IS NOT NULL",
                await CheckDefinitionAsync(db, "CK_BackupTasks_Identity"), StringComparison.Ordinal);
            Assert.DoesNotContain("PolicyId", await CheckDefinitionAsync(db, "CK_BackupTasks_Identity"), StringComparison.Ordinal);
        }
        finally { await temporary.DisposeAsync(); }
    }

    [Fact]
    public async Task RemoveBackupPoliciesDownRestoresSchemaAndMigratesUpAgain()
    {
        var temporary = new PlatformDatabaseSqlServerFixture();
        try
        {
            await temporary.InitializeAsync();
            var graph = await GraphAsync(temporary);
            var task = Task(graph.Plan, BackupRunPurpose.PlanDifferential);
            await SaveAsync(temporary, task, Snapshot(graph, task, BackupRunPurpose.PlanDifferential));
            await using var db = temporary.CreateContext();
            var migrator = db.GetService<IMigrator>();
            var beforeRemoval = db.Database.GetMigrations().Single(x =>
                x.EndsWith("_AddRemoteGapProjectionPendings", StringComparison.Ordinal));
            await migrator.MigrateAsync(beforeRemoval);
            Assert.Equal(18, await RestoredPolicyShapeCountAsync(db));
            Assert.Contains("[PolicyId] IS NOT NULL", await CheckDefinitionAsync(db, "CK_BackupTasks_Identity"), StringComparison.Ordinal);
            Assert.Equal(task.PlanId, await db.Database.SqlQueryRaw<Guid?>("""
                SELECT [PlanId] AS [Value] FROM [BackupTasks] WHERE [Id] = {0}
                """, task.Id).SingleAsync());
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""
                SELECT COUNT(*) AS [Value] FROM [BackupTasks]
                WHERE [Id] = {0} AND [PolicyId] IS NULL AND [PlanVersionId] IS NOT NULL
                """, task.Id).SingleAsync());
            await migrator.MigrateAsync();
            Assert.Equal(0, await TableCountAsync(db, "BackupPolicies"));
            Assert.Equal(0, await ColumnCountAsync(db, "BackupTasks", "PolicyId"));
            Assert.Equal(BackupType.Differential, (await db.BackupTasks.SingleAsync(x => x.Id == task.Id)).BackupType);
        }
        finally { await temporary.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveBackupPoliciesUpRejectsUnmigratedPolicyData(bool includePolicyTask)
    {
        var temporary = new PlatformDatabaseSqlServerFixture();
        try
        {
            await temporary.InitializeAsync();
            var host = await BackupSetTestData.HostSnapshotAsync(temporary);
            await using var db = temporary.CreateContext();
            var migrator = db.GetService<IMigrator>();
            var beforeRemoval = db.Database.GetMigrations().Single(x =>
                x.EndsWith("_AddRemoteGapProjectionPendings", StringComparison.Ordinal));
            await migrator.MigrateAsync(beforeRemoval);
            if (includePolicyTask)
            {
                await InsertLegacyPolicyTaskAsync(db, host, copyOnly: false);
            }
            else
            {
                await InsertLegacyPolicyAsync(db, host);
            }

            var history = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            var error = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync());
            Assert.Equal(51008, error.Number);
            Assert.Contains("已拒绝删除", error.Message, StringComparison.Ordinal);
            Assert.Equal(history, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Equal(1, await TableCountAsync(db, "BackupPolicies"));
            Assert.Equal(1, await ColumnCountAsync(db, "BackupTasks", "PolicyId"));
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [BackupPolicies]").SingleAsync());
        }
        finally { await temporary.DisposeAsync(); }
    }

    private static Task<int> NewColumnCountAsync(PlatformDbContext db) => db.Database.SqlQueryRaw<int>("""
        SELECT COUNT(*) AS [Value] FROM sys.columns
        WHERE (object_id=OBJECT_ID(N'BackupTasks') AND name IN ('PlanId','PlanVersionId','BackupType','CoveredDifferentialSlotUtc'))
           OR (object_id=OBJECT_ID(N'BackupTaskSnapshots') AND name='Purpose')
        """).SingleAsync();

    private static async Task<Guid> InsertLegacyPolicyAsync(PlatformDbContext db, BackupTaskSnapshot host)
    {
        var policyId = Guid.NewGuid();
        var name = $"legacy-policy-{policyId:N}";
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [BackupPolicies] (
                [Id], [Name], [NormalizedName], [DatabaseId], [BackupType], [StorageMode], [StorageTargetId],
                [ScheduleType], [LocalTime], [DaysOfWeek], [TimeZoneId], [LocalRetentionDays], [RemoteRetentionDays],
                [UseChecksum], [UseCompression], [UseCopyOnly], [BackupTimeoutMinutes], [VerifyTimeoutMinutes],
                [TransferTimeoutMinutes], [IsEnabled], [IsManualOnly], [ScheduleEffectiveFromUtc],
                [CreatedAtUtc], [UpdatedAtUtc])
            VALUES (
                {policyId}, {name}, {name}, {host.DatabaseId}, {"Full"}, {"LocalOnly"}, {(Guid?)null},
                {"Daily"}, {new TimeOnly(2, 0)}, {0}, {"UTC"}, {7}, {(int?)null},
                {true}, {true}, {false}, {120}, {60},
                {180}, {true}, {false}, {now},
                {now}, {now})
            """);
        return policyId;
    }

    private static async Task<Guid> InsertLegacyPolicyTaskAsync(
        PlatformDbContext db, BackupTaskSnapshot host, bool copyOnly)
    {
        var policyId = await InsertLegacyPolicyAsync(db, host);
        var taskId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [BackupTasks] (
                [Id], [PolicyId], [PlanId], [PlanVersionId], [BackupType], [CoveredDifferentialSlotUtc],
                [TriggerType], [ScheduledSlotAtUtc], [Status], [CurrentStage], [RetryCount],
                [ReconciliationAttemptCount], [CreatedAtUtc], [UpdatedAtUtc])
            VALUES (
                {taskId}, {policyId}, {(Guid?)null}, {(Guid?)null}, {"Full"}, {(DateTimeOffset?)null},
                {"Manual"}, {(DateTimeOffset?)null}, {"Pending"}, {"Backup"}, {0},
                {0}, {now}, {now})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [BackupTaskSnapshots] (
                [TaskId], [PolicyName], [ServerId], [ServerName], [InstanceId], [InstanceName],
                [DatabaseId], [DatabaseName], [ConnectionAddress], [SqlCredentialReferenceId],
                [EncryptConnection], [TrustServerCertificate], [CertificateTrustReason],
                [AllowLegacyTls], [LegacyTlsReason], [ConnectionTimeoutSeconds],
                [UseChecksum], [UseCompression], [UseCopyOnly],
                [BackupTimeoutMinutes], [VerifyTimeoutMinutes], [TransferTimeoutMinutes],
                [LocalSqlBackupRootPath], [FileNameRuleVersion], [SourceAccessProtocol], [SourceAccessHost],
                [SourceAccessPort], [SourceAccessBasePath], [SourceCredentialReferenceId],
                [SourceSftpHostKeyFingerprint], [StorageMode], [StorageTargetId], [LocalRetentionDays],
                [RemoteRetentionDays], [TimeZoneId], [Purpose], [BackupType])
            VALUES (
                {taskId}, {"合成旧策略"}, {host.ServerId}, {host.ServerName}, {host.InstanceId}, {host.InstanceName},
                {host.DatabaseId}, {host.DatabaseName}, {host.ConnectionAddress}, {host.SqlCredentialReferenceId},
                {host.EncryptConnection}, {host.TrustServerCertificate}, {host.CertificateTrustReason},
                {host.AllowLegacyTls}, {host.LegacyTlsReason}, {host.ConnectionTimeoutSeconds},
                {true}, {true}, {copyOnly},
                {120}, {60}, {180},
                {host.LocalSqlBackupRootPath}, {"v2"}, {host.SourceAccessProtocol.ToString()}, {host.SourceAccessHost},
                {host.SourceAccessPort}, {host.SourceAccessBasePath}, {host.SourceCredentialReferenceId},
                {host.SourceSftpHostKeyFingerprint}, {"LocalOnly"}, {(Guid?)null}, {7},
                {(int?)null}, {host.TimeZoneId}, {(string?)null}, {"Full"})
            """);
        return taskId;
    }

    private static Task<int> LegacyIdentityCountAsync(PlatformDbContext db, Guid taskId, bool copyOnly) =>
        db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS [Value]
            FROM [BackupTasks] AS [task]
            INNER JOIN [BackupTaskSnapshots] AS [snapshot] ON [snapshot].[TaskId] = [task].[Id]
            WHERE [task].[Id] = {0}
              AND [task].[PolicyId] IS NOT NULL
              AND [task].[PlanId] IS NULL
              AND [task].[PlanVersionId] IS NULL
              AND [task].[BackupType] = 'Full'
              AND [snapshot].[Purpose] IS NULL
              AND [snapshot].[FileNameRuleVersion] = 'v2'
              AND [snapshot].[UseCopyOnly] = {1}
            """, taskId, copyOnly).SingleAsync();

    private static Task<int> TableCountAsync(PlatformDbContext db, string name) =>
        db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS [Value] FROM sys.tables WHERE [name] = {0}
            """, name).SingleAsync();

    private static Task<int> ColumnCountAsync(PlatformDbContext db, string table, string column) =>
        db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS [Value] FROM sys.columns
            WHERE object_id = OBJECT_ID({0}) AND [name] = {1}
            """, table, column).SingleAsync();

    private static Task<int> PlanIdentityDefaultCountAsync(PlatformDbContext db) =>
        db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS [Value]
            FROM sys.default_constraints AS [constraint]
            INNER JOIN sys.columns AS [column]
                ON [constraint].[parent_object_id] = [column].[object_id]
               AND [constraint].[parent_column_id] = [column].[column_id]
            WHERE [constraint].[parent_object_id] = OBJECT_ID(N'BackupTasks')
              AND [column].[name] IN ('PlanId', 'PlanVersionId')
            """).SingleAsync();

    private static Task<int> RestoredPolicyShapeCountAsync(PlatformDbContext db) =>
        db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS [Value] FROM (
                SELECT [name] FROM sys.tables WHERE [name] = 'BackupPolicies'
                UNION ALL
                SELECT [name] FROM sys.columns
                WHERE object_id = OBJECT_ID(N'BackupTasks') AND [name] = 'PolicyId'
                UNION ALL
                SELECT [name] FROM sys.indexes WHERE [name] IN (
                    'UX_BackupPolicies_NormalizedName', 'IX_BackupPolicies_StorageTargetId',
                    'UX_BackupPolicies_DatabaseId_BackupType_Enabled',
                    'UX_BackupTasks_PolicyId_ScheduledSlotAtUtc')
                UNION ALL
                SELECT [name] FROM sys.foreign_keys WHERE [name] IN (
                    'FK_BackupPolicies_ManagedDatabases_DatabaseId',
                    'FK_BackupPolicies_StorageTargets_StorageTargetId',
                    'FK_BackupTasks_BackupPolicies_PolicyId')
                UNION ALL
                SELECT [name] FROM sys.check_constraints WHERE [name] IN (
                    'CK_BackupPolicies_BackupType', 'CK_BackupPolicies_Name_NotEmpty',
                    'CK_BackupPolicies_NormalizedName_NotEmpty', 'CK_BackupPolicies_Schedule',
                    'CK_BackupPolicies_ScheduleEffectiveFrom', 'CK_BackupPolicies_Storage',
                    'CK_BackupPolicies_TimeZoneId_NotEmpty', 'CK_BackupPolicies_Timeouts',
                    'CK_BackupTasks_Identity')
            ) AS [expected]
            """).SingleAsync();

    private static Task<string> CheckDefinitionAsync(PlatformDbContext db, string name) =>
        db.Database.SqlQueryRaw<string>("""
            SELECT [definition] AS [Value] FROM sys.check_constraints WHERE [name] = {0}
            """, name).SingleAsync();
    private static async Task SaveAsync(PlatformDatabaseSqlServerFixture database, BackupTask task, BackupTaskSnapshot snapshot)
    {
        await using var db = database.CreateContext();
        db.AddRange(task, snapshot);
        await db.SaveChangesAsync();
    }
    private static BackupTask Task(BackupPlan plan, BackupRunPurpose purpose, DateTimeOffset? slot = null,
        DateTimeOffset? covered = null) => BackupTask.ForPlan(Guid.NewGuid(), plan.Id, plan.CurrentVersionId,
            purpose, slot is null ? BackupTaskTriggerType.Manual : BackupTaskTriggerType.Scheduled, slot, covered);
    private static BackupTaskSnapshot Snapshot(Graph graph, BackupTask task, BackupRunPurpose purpose)
    {
        var old = graph.Legacy;
        return BackupTaskSnapshot.ForPlan(task, graph.Plan.CurrentVersion, graph.Plan.Name, purpose,
            new(old.ServerId, old.ServerName, old.InstanceId, old.InstanceName, old.DatabaseId, old.DatabaseName),
            new(old.ConnectionAddress, old.SqlCredentialReferenceId, old.EncryptConnection, old.TrustServerCertificate,
                old.CertificateTrustReason, old.ConnectionTimeoutSeconds, old.AllowLegacyTls, old.LegacyTlsReason),
            new(old.LocalSqlBackupRootPath, "v3", new(old.SourceAccessProtocol, old.SourceAccessHost, old.SourceAccessPort,
                old.SourceAccessBasePath, old.SourceCredentialReferenceId, old.SourceSftpHostKeyFingerprint)));
    }
    private static async Task<Graph> GraphAsync(PlatformDatabaseSqlServerFixture database)
    {
        var host = await BackupSetTestData.HostSnapshotAsync(database);
        var plan = BackupPlan.Create(Guid.NewGuid(), host.DatabaseId, "合成计划", Guid.NewGuid(), Definition(14), Slot);
        using var provider = database.CreateServiceProvider();
        await new BackupPlanStore(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>()).AddAsync(plan);
        var task = Task(plan, BackupRunPurpose.PlanFull);
        var snapshot = BackupTaskSnapshot.ForPlan(task, plan.CurrentVersion, plan.Name, BackupRunPurpose.PlanFull,
            new(host.ServerId, host.ServerName, host.InstanceId, host.InstanceName, host.DatabaseId, host.DatabaseName),
            new(host.ConnectionAddress, host.SqlCredentialReferenceId, host.EncryptConnection, host.TrustServerCertificate,
                host.CertificateTrustReason, host.ConnectionTimeoutSeconds, host.AllowLegacyTls, host.LegacyTlsReason),
            new(host.LocalSqlBackupRootPath, "v3", new(host.SourceAccessProtocol, host.SourceAccessHost, host.SourceAccessPort,
                host.SourceAccessBasePath, host.SourceCredentialReferenceId, host.SourceSftpHostKeyFingerprint)));
        await SaveAsync(database, task, snapshot);
        return new(plan, snapshot);
    }
    private static BackupPlanDefinition Definition(int days) => new(BackupPlanMode.FullAndDifferentialAndLog,
        new(BackupScheduleType.Daily, new(2, 0), BackupWeekdays.None),
        new(BackupScheduleType.Daily, new(3, 0), BackupWeekdays.None), new(15), "UTC", BackupStorageMode.LocalOnly,
        null, days, null, true, false, 120, 60, 60);
    private sealed record Graph(BackupPlan Plan, BackupTaskSnapshot Legacy);
}
