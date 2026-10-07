using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupPlanSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(BackupPlanMode.Full)]
    [InlineData(BackupPlanMode.FullAndDifferential)]
    [InlineData(BackupPlanMode.FullAndDifferentialAndLog)]
    public async Task PlanRoundTripsForEachMode(BackupPlanMode mode)
    {
        var databaseId = await AddManagedDatabaseAsync();
        var plan = CreatePlan(databaseId, mode);
        await Store().AddAsync(plan);

        await using var context = database.CreateContext();
        var stored = await context.BackupPlans.Include(item => item.Versions).SingleAsync(item => item.Id == plan.Id);
        var version = Assert.Single(stored.Versions);

        Assert.Equal(version.Id, stored.CurrentVersionId);
        Assert.Equal(mode, version.Mode);
        Assert.Equal(BackupScheduleType.Daily, version.FullSchedule.ScheduleType);
        Assert.Equal(mode != BackupPlanMode.Full, version.DifferentialSchedule.HasValue);
        Assert.Equal(mode == BackupPlanMode.FullAndDifferentialAndLog, version.LogSchedule.HasValue);
        if (version.LogSchedule is { } log)
        {
            Assert.Equal(15, log.IntervalMinutes);
            Assert.Equal(Now, log.AnchorUtc);
        }
    }

    [Fact]
    public async Task PausedPlanStillOccupiesTheDatabase()
    {
        var databaseId = await AddManagedDatabaseAsync();
        var store = Store();
        var first = CreatePlan(databaseId, BackupPlanMode.Full);
        first.Pause();
        await store.AddAsync(first);

        var duplicate = CreatePlan(databaseId, BackupPlanMode.Full);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.AddAsync(duplicate));
    }

    [Fact]
    public async Task DuplicateVersionNumberIsRejected()
    {
        var databaseId = await AddManagedDatabaseAsync();
        var plan = CreatePlan(databaseId, BackupPlanMode.Full);
        await Store().AddAsync(plan);

        await using var context = database.CreateContext();
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            context.Database.ExecuteSqlRawAsync(VersionInsert(plan.Id, number: 1)));
        Assert.True(exception.Number is 2601 or 2627);
    }

    [Fact]
    public async Task CircularInsertCompletesInsideOneTransaction()
    {
        var databaseId = await AddManagedDatabaseAsync();
        var plan = CreatePlan(databaseId, BackupPlanMode.FullAndDifferential);
        await Store().AddAsync(plan);

        await using var context = database.CreateContext();
        var currentVersionId = await context.Database
            .SqlQuery<Guid?>($"SELECT [CurrentVersionId] AS [Value] FROM [BackupPlans] WHERE [Id] = {plan.Id}")
            .SingleAsync();
        Assert.Equal(plan.CurrentVersionId, currentVersionId);
    }

    [Fact]
    public async Task FailedVersionInsertRollsBackThePlan()
    {
        var databaseId = await AddManagedDatabaseAsync();
        var plan = BackupPlan.Create(
            Guid.NewGuid(),
            databaseId,
            "远端计划",
            Guid.NewGuid(),
            RemoteDefinition(Guid.NewGuid()),
            Now);

        await Assert.ThrowsAsync<DbUpdateException>(() => Store().AddAsync(plan));
        await using var context = database.CreateContext();
        Assert.False(await context.BackupPlans.AnyAsync(item => item.Id == plan.Id));
    }

    [Fact]
    public async Task VersionUpdateIsRejected()
    {
        var databaseId = await AddManagedDatabaseAsync();
        var plan = CreatePlan(databaseId, BackupPlanMode.Full);
        await Store().AddAsync(plan);

        await using var context = database.CreateContext();
        var version = await context.BackupPlanVersions.SingleAsync(item => item.PlanId == plan.Id);
        context.Entry(version).Property(item => item.Number).CurrentValue = 2;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains("只能追加", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownIsRejectedAfterAPlanExists()
    {
        var databaseId = await AddManagedDatabaseAsync();
        await Store()
            .AddAsync(CreatePlan(databaseId, BackupPlanMode.Full));

        await using var context = database.CreateContext();
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            context.Database.ExecuteSqlRawAsync(BackupPlanMigrationGuard.RejectDownWhenPlansExist));
        Assert.Contains("已写入备份计划", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CK_BackupPlans_Name_NotEmpty", "Name")]
    [InlineData("CK_BackupPlans_NormalizedName_NotEmpty", "NormalizedName")]
    [InlineData("CK_BackupPlanVersions_TimeZoneId_NotEmpty", "TimeZone")]
    [InlineData("CK_BackupPlanVersions_ModeSchedules", "Mode")]
    [InlineData("CK_BackupPlanVersions_Schedule", "Schedule")]
    [InlineData("CK_BackupPlanVersions_Timeouts", "Timeout")]
    [InlineData("CK_BackupPlanVersions_LogInterval", "LogInterval")]
    [InlineData("CK_BackupPlanVersions_UtcOffset", "Utc")]
    [InlineData("CK_BackupPlanVersions_Windows", "Windows")]
    public async Task CheckConstraintRejectsOneIllegalRow(string constraint, string violation)
    {
        var databaseId = await AddManagedDatabaseAsync();
        var plan = CreatePlan(databaseId, BackupPlanMode.Full);
        await Store().AddAsync(plan);

        var targetDatabaseId = violation is "Name" or "NormalizedName"
            ? await AddManagedDatabaseAsync()
            : databaseId;
        await using var context = database.CreateContext();
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            context.Database.ExecuteSqlRawAsync(IllegalInsertSql(violation, plan.Id, targetDatabaseId)));
        Assert.Contains(constraint, exception.Message, StringComparison.Ordinal);
    }

    private BackupPlanStore Store()
    {
        return new BackupPlanStore(database.CreateServiceProvider().GetRequiredService<IDbContextFactory<PlatformDbContext>>());
    }

    private async Task<Guid> AddManagedDatabaseAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var staging = new CredentialReference(
            Guid.NewGuid(), $"合成暂存-{suffix}", CredentialKind.SmbPassword, "synthetic-user", "protected:staging", "dp-v1");
        var sql = new CredentialReference(
            Guid.NewGuid(), $"合成 SQL-{suffix}", CredentialKind.SqlPassword, "synthetic-sql", "protected:sql", "dp-v1");
        var server = new DatabaseServer(
            Guid.NewGuid(),
            $"合成服务器-{suffix}",
            "synthetic-root",
            new FileEndpointSettings(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", staging.Id, null));
        var instance = new DatabaseInstance(
            Guid.NewGuid(), server.Id, $"合成实例-{suffix}", $"synthetic-{suffix}", sql.Id, true, false, null, 30);
        var managed = new ManagedDatabase(
            Guid.NewGuid(), instance.Id, $"Synthetic_{suffix}", false, true, Now, "FULL", "ONLINE");
        await using var context = database.CreateContext();
        context.AddRange(staging, sql, server, instance, managed);
        await context.SaveChangesAsync();
        return managed.Id;
    }

    private static BackupPlan CreatePlan(Guid databaseId, BackupPlanMode mode)
    {
        return BackupPlan.Create(Guid.NewGuid(), databaseId, $"计划-{mode}", Guid.NewGuid(), Definition(mode), Now);
    }

    private static BackupPlanDefinition Definition(BackupPlanMode mode)
    {
        return new BackupPlanDefinition(
            mode,
            new RecurringBackupSchedule(BackupScheduleType.Daily, new TimeOnly(2, 0), BackupWeekdays.None),
            mode == BackupPlanMode.Full
                ? null
                : new RecurringBackupSchedule(BackupScheduleType.Weekly, new TimeOnly(3, 0), BackupWeekdays.Monday),
            mode == BackupPlanMode.FullAndDifferentialAndLog ? new LogBackupInterval(15) : null,
            "UTC",
            BackupStorageMode.LocalOnly,
            null,
            14,
            null,
            true,
            false,
            120,
            60,
            60);
    }

    private static BackupPlanDefinition RemoteDefinition(Guid storageTargetId)
    {
        return new BackupPlanDefinition(
            BackupPlanMode.Full,
            new RecurringBackupSchedule(BackupScheduleType.Daily, new TimeOnly(2, 0), BackupWeekdays.None),
            null,
            null,
            "UTC",
            BackupStorageMode.RemoteOnly,
            storageTargetId,
            null,
            14,
            true,
            false,
            120,
            60,
            60);
    }

    private static string IllegalInsertSql(string violation, Guid planId, Guid databaseId) => violation switch
    {
        "Name" => PlanInsert(databaseId, name: ""),
        "NormalizedName" => PlanInsert(databaseId, normalizedName: ""),
        "TimeZone" => VersionInsert(planId, timeZoneId: ""),
        "Mode" => VersionInsert(planId, mode: "Full", differentialType: "Daily"),
        "Schedule" => VersionInsert(planId, fullType: "Weekly", fullDays: 0),
        "Timeout" => VersionInsert(planId, backupTimeout: 0),
        "LogInterval" => VersionInsert(planId, mode: "FullAndDifferentialAndLog", differentialType: "Daily", logInterval: 0, logAnchor: "2026-10-07T00:00:00+00:00"),
        "Utc" => VersionInsert(planId, effectiveFrom: "2026-10-07T08:00:00+08:00"),
        "Windows" => VersionInsert(planId, remoteWindow: 7),
        _ => throw new ArgumentOutOfRangeException(nameof(violation))
    };

    private static string PlanInsert(Guid databaseId, string name = "计划", string normalizedName = "计划") =>
        $"""
        INSERT INTO [BackupPlans]
            ([Id], [DatabaseId], [Name], [NormalizedName], [IsPaused], [CurrentVersionId], [CreatedAtUtc], [UpdatedAtUtc])
        VALUES
            ('{Guid.NewGuid()}', '{databaseId}', N'{name}', N'{normalizedName}', 0, NULL,
             '2026-10-07T00:00:00+00:00', '2026-10-07T00:00:00+00:00');
        """;

    private static string VersionInsert(
        Guid planId,
        string mode = "Full",
        string timeZoneId = "UTC",
        string fullType = "Daily",
        int fullDays = 0,
        string? differentialType = null,
        int backupTimeout = 120,
        int? logInterval = null,
        string? logAnchor = null,
        string effectiveFrom = "2026-10-07T00:00:00+00:00",
        int? remoteWindow = null,
        int number = 2)
    {
        var differentialTypeSql = differentialType is null ? "NULL" : $"N'{differentialType}'";
        var differentialTimeSql = differentialType is null ? "NULL" : "'03:00:00'";
        var differentialDaysSql = differentialType is null ? "NULL" : "0";
        var logIntervalSql = logInterval?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
        var logAnchorSql = logAnchor is null ? "NULL" : $"'{logAnchor}'";
        var remoteSql = remoteWindow?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
        return $"""
            INSERT INTO [BackupPlanVersions]
                ([Id], [PlanId], [Number], [Mode], [TimeZoneId], [StorageMode], [StorageTargetId],
                 [LocalRecoveryWindowDays], [RemoteRecoveryWindowDays], [UseChecksum], [UseCompression],
                 [BackupTimeoutMinutes], [VerifyTimeoutMinutes], [TransferTimeoutMinutes], [EffectiveFromUtc],
                 [FullSchedule_ScheduleType], [FullSchedule_LocalTime], [FullSchedule_DaysOfWeek],
                 [DifferentialSchedule_ScheduleType], [DifferentialSchedule_LocalTime], [DifferentialSchedule_DaysOfWeek],
                 [LogSchedule_IntervalMinutes], [LogSchedule_AnchorUtc], [CreatedAtUtc], [UpdatedAtUtc])
            VALUES
                ('{Guid.NewGuid()}', '{planId}', {number.ToString(System.Globalization.CultureInfo.InvariantCulture)}, N'{mode}', N'{timeZoneId}', N'LocalOnly', NULL,
                 14, {remoteSql}, 1, 0,
                 {backupTimeout}, 60, 60, '{effectiveFrom}',
                 N'{fullType}', '02:00:00', {fullDays},
                 {differentialTypeSql}, {differentialTimeSql}, {differentialDaysSql},
                 {logIntervalSql}, {logAnchorSql}, '2026-10-07T00:00:00+00:00', '2026-10-07T00:00:00+00:00');
            """;
    }
}
