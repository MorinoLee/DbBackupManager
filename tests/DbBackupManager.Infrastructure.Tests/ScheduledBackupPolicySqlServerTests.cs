using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.BackupManagement;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class ScheduledBackupPolicySqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Fact]
    public async Task ScheduledPolicyPersistsBaselineAndDoesNotChangeManualContracts()
    {
        var seed = await SeedAsync();
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var clock = new FrozenTime(new DateTimeOffset(2026, 9, 18, 1, 0, 0, TimeSpan.Zero));
        var scheduled = new ScheduledBackupPolicyService(factory, clock);
        var manual = new BackupManagementService(factory, new BackupTaskExecutionStore(factory));

        var created = await scheduled.SaveAsync(seed.Actor, null, null, EnabledDaily(seed.DatabaseId, $"计划-启用-{Guid.NewGuid():N}"));
        Assert.Equal(BackupManagementCode.Succeeded, created.Code);
        Assert.Equal(clock.GetUtcNow(), created.Value!.ScheduleEffectiveFromUtc);
        Assert.Equal(0, created.Value.Settings.DaysOfWeek);

        var renamed = await scheduled.SaveAsync(
            seed.Actor,
            created.Value.Id,
            created.Value.Version,
            EnabledDaily(seed.DatabaseId, created.Value.Settings.Name) with { LocalRetentionDays = 14 });
        Assert.Equal(created.Value.ScheduleEffectiveFromUtc, renamed.Value!.ScheduleEffectiveFromUtc);

        var rescheduled = await scheduled.SaveAsync(
            seed.Actor,
            renamed.Value.Id,
            renamed.Value.Version,
            EnabledDaily(seed.DatabaseId, renamed.Value.Settings.Name) with { LocalTime = new TimeOnly(4, 30) });
        Assert.Equal(clock.GetUtcNow(), rescheduled.Value!.ScheduleEffectiveFromUtc);

        var disabled = await scheduled.SaveAsync(
            seed.Actor,
            rescheduled.Value.Id,
            rescheduled.Value.Version,
            EnabledDaily(seed.DatabaseId, rescheduled.Value.Settings.Name) with { IsEnabled = false, LocalTime = new TimeOnly(4, 30) });
        Assert.Null(disabled.Value!.ScheduleEffectiveFromUtc);

        var enabledAgain = await scheduled.SaveAsync(
            seed.Actor,
            disabled.Value.Id,
            disabled.Value.Version,
            EnabledDaily(seed.DatabaseId, disabled.Value.Settings.Name) with { IsEnabled = true, LocalTime = new TimeOnly(4, 30) });
        Assert.Equal(clock.GetUtcNow(), enabledAgain.Value!.ScheduleEffectiveFromUtc);

        var weekly = await scheduled.SaveAsync(
            seed.Actor,
            null,
            null,
            EnabledDaily(seed.DatabaseId, $"计划-每周-{Guid.NewGuid():N}") with
            {
                IsEnabled = false,
                ScheduleType = (int)BackupScheduleType.Weekly,
                DaysOfWeek = (int)(BackupWeekdays.Monday | BackupWeekdays.Friday)
            });
        Assert.Equal(BackupManagementCode.Succeeded, weekly.Code);

        var invalidZone = await scheduled.SaveAsync(
            seed.Actor,
            null,
            null,
            EnabledDaily(seed.DatabaseId, $"计划-坏时区-{Guid.NewGuid():N}") with { TimeZoneId = "Synthetic/Unknown" });
        Assert.Equal(BackupManagementCode.Invalid, invalidZone.Code);

        var stale = await scheduled.SaveAsync(
            seed.Actor,
            enabledAgain.Value.Id,
            "stale-version",
            EnabledDaily(seed.DatabaseId, enabledAgain.Value.Settings.Name) with { LocalTime = new TimeOnly(4, 30) });
        Assert.Equal(BackupManagementCode.Conflict, stale.Code);

        var manualSaved = await manual.SavePolicyAsync(
            seed.Actor,
            null,
            null,
            new(seed.DatabaseId, $"手动-{Guid.NewGuid():N}", 7, 120, 60, false, false));
        Assert.Equal(BackupManagementCode.Succeeded, manualSaved.Code);
        await using var db = database.CreateContext();
        var manualEntity = await db.BackupPolicies.SingleAsync(x => x.Id == manualSaved.Value!.Id);
        Assert.True(manualEntity.IsManualOnly);
        Assert.Null(manualEntity.ScheduleEffectiveFromUtc);
        Assert.False(await db.BackupPolicies.AnyAsync(x => x.Id == created.Value.Id && x.IsManualOnly));

        var duplicateEnabled = await scheduled.SaveAsync(
            seed.Actor,
            null,
            null,
            EnabledDaily(seed.DatabaseId, $"计划-重复启用-{Guid.NewGuid():N}"));
        Assert.Equal(BackupManagementCode.Conflict, duplicateEnabled.Code);
    }

    [Fact]
    public async Task SchedulerCreatesLatestDueSlotOnceAndRejectsManualOnly()
    {
        var seed = await SeedAsync();
        using var provider = database.CreateServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        var baseline = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.FromHours(8));
        var now = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.FromHours(8));
        var scheduled = new ScheduledBackupPolicyService(factory, new FrozenTime(baseline));
        var created = await scheduled.SaveAsync(
            seed.Actor,
            null,
            null,
            EnabledDaily(seed.DatabaseId, $"调度-{Guid.NewGuid():N}") with
            {
                LocalTime = new TimeOnly(2, 0)
            });
        Assert.Equal(BackupManagementCode.Succeeded, created.Code);
        var policyId = created.Value!.Id;

        var scheduler = new BackupTaskScheduler(
            new SchedulableBackupPolicyReader(factory),
            new BackupTaskExecutionStore(factory),
            new FrozenTime(now));
        var first = await scheduler.RunOnceAsync();
        var second = await scheduler.RunOnceAsync();
        Assert.Contains(first.Policies, item => item.PolicyId == policyId
            && item.Code == BackupSchedulePolicyResultCode.Created);
        Assert.Contains(second.Policies, item => item.PolicyId == policyId
            && item.Code == BackupSchedulePolicyResultCode.AlreadyExists);

        await using (var disable = database.CreateContext())
        {
            var policy = await disable.BackupPolicies.AsTracking().SingleAsync(x => x.Id == policyId);
            policy.SetEnabled(false);
            await disable.SaveChangesAsync();
        }

        var afterDisable = await scheduler.RunOnceAsync();
        Assert.DoesNotContain(afterDisable.Policies, item => item.PolicyId == policyId);

        var store = new BackupTaskExecutionStore(factory);
        var manual = await store.CreateTaskAsync(new CreateBackupTaskCommand(
            Guid.NewGuid(),
            seed.ManualPolicyId,
            BackupTaskTriggerType.Scheduled,
            now,
            Guid.NewGuid(),
            now));
        Assert.Equal(BackupTaskStoreResultCode.ConfigurationUnavailable, manual.Code);
    }

    private static ScheduledBackupPolicyInput EnabledDaily(Guid databaseId, string name) =>
        new(databaseId, name, 1, new TimeOnly(2, 0), 0, "Taipei Standard Time", 1, null, 7, null, 120, 60, 60, false, true);

    private async Task<(AdminSession Actor, Guid DatabaseId, Guid ManualPolicyId)> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var actor = new AdminSession(Guid.NewGuid(), $"admin-{suffix}", $"stamp-{suffix}");
        var file = new CredentialReference(Guid.NewGuid(), $"smb-{suffix}", CredentialKind.SmbPassword, "synthetic", "synthetic-cipher", "dp-smb-password-v1");
        var sql = new CredentialReference(Guid.NewGuid(), $"sql-{suffix}", CredentialKind.SqlPassword, "synthetic", "synthetic-cipher", "dp-sql-password-v1");
        var server = new DatabaseServer(Guid.NewGuid(), $"server-{suffix}", @"D:\Synthetic",
            new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", file.Id, null));
        var instance = new DatabaseInstance(Guid.NewGuid(), server.Id, "instance", $"synthetic-{suffix}", sql.Id, true, false, null, 15);
        instance.RecordConnectionSucceeded(DateTimeOffset.UtcNow, "15.0.synthetic", null, null);
        var managed = new ManagedDatabase(Guid.NewGuid(), instance.Id, $"db-{suffix}", false, true, DateTimeOffset.UtcNow);
        managed.SetManaged(true);
        var manual = new BackupPolicy(
            Guid.NewGuid(),
            $"manual-{suffix}",
            managed.Id,
            new BackupPolicySettings(
                BackupStorageMode.LocalOnly,
                null,
                BackupScheduleType.Daily,
                TimeOnly.MinValue,
                BackupWeekdays.None,
                "UTC",
                7,
                null,
                true,
                false,
                true,
                120,
                60,
                60),
            isEnabled: false,
            isManualOnly: true);
        await using var db = database.CreateContext();
        db.AddRange(
            file,
            sql,
            server,
            instance,
            managed,
            manual,
            new AdminUser(actor.AdminUserId, actor.Username, actor.Username.ToUpperInvariant(), "synthetic-hash", actor.SecurityStamp));
        await db.SaveChangesAsync();
        return (actor, managed.Id, manual.Id);
    }

    private sealed class FrozenTime(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
