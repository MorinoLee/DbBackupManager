using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Tests;

/// <summary>
/// 手动执行入口的幂等与安全：重放须按原版本返回原任务、绑定完整请求身份，并在返回前复核管理员安全戳。
/// </summary>
public sealed class BackupPlanManualExecutionSqlServerTests(PlatformDatabaseSqlServerFixture platform)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Fact]
    public async Task ReplaySurvivesPlanRevisionAndReturnsOriginalVersion()
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
        var (store, command) = await ManualAsync(test.DatabaseId);
        var first = await store.CreateAsync(command);
        Assert.Equal(BackupPlanTaskCreationCode.Created, first.Code);
        await using (var db = platform.CreateContext())
        {
            var plan = await db.BackupPlans.Include(x => x.Versions).AsTracking().SingleAsync(x => x.DatabaseId == test.DatabaseId);
            var version = plan.CurrentVersion;
            var definition = new BackupPlanDefinition(version.Mode, version.FullSchedule, version.DifferentialSchedule,
                version.LogSchedule is { } log ? new LogBackupInterval(log.IntervalMinutes) : null,
                version.TimeZoneId, version.StorageMode, version.StorageTargetId, version.LocalRecoveryWindowDays,
                version.RemoteRecoveryWindowDays, version.UseChecksum, version.UseCompression,
                version.BackupTimeoutMinutes + 1, version.VerifyTimeoutMinutes, version.TransferTimeoutMinutes);
            Assert.True(plan.Revise(Guid.NewGuid(), definition, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var replay = await store.CreateAsync(command);
        Assert.Equal(BackupPlanTaskCreationCode.AlreadyApplied, replay.Code);
        Assert.Equal(first.Task, replay.Task);
    }

    [Fact]
    public async Task ReplayByTaskIdBindsItsMutationBeforeAnotherTaskCanUseIt()
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
        var (store, command) = await ManualAsync(test.DatabaseId);
        Assert.Equal(BackupPlanTaskCreationCode.Created, (await store.CreateAsync(command)).Code);

        var secondMutation = command with { Request = command.Request with { MutationId = Guid.NewGuid() } };
        Assert.Equal(BackupPlanTaskCreationCode.AlreadyApplied, (await store.CreateAsync(secondMutation)).Code);

        var otherTask = secondMutation with { Request = secondMutation.Request with { TaskId = Guid.NewGuid() } };
        Assert.Equal(BackupPlanTaskCreationCode.RequestConflict, (await store.CreateAsync(otherTask)).Code);
    }

    [Fact]
    public async Task SuccessfulReplayStillValidatesTheAdministratorSecurityStamp()
    {
        await using var test = await PlanExecutionTestEnvironment.CreateAsync(platform, BackupStorageMode.LocalOnly);
        var (store, command) = await ManualAsync(test.DatabaseId);
        Assert.Equal(BackupPlanTaskCreationCode.Created, (await store.CreateAsync(command)).Code);

        var replay = await store.CreateAsync(command with { ActorSecurityStamp = "synthetic-revoked-stamp" });
        Assert.Equal(BackupPlanTaskCreationCode.AuthenticationRequired, replay.Code);
    }

    private async Task<(BackupPlanManualExecutionStore Store, CreateBackupPlanTaskCommand Command)> ManualAsync(Guid databaseId)
    {
        await using var db = platform.CreateContext();
        var plan = await db.BackupPlans.SingleAsync(x => x.DatabaseId == databaseId);
        var id = Guid.NewGuid();
        var admin = new AdminUser(id, $"review-{id:N}", $"REVIEW-{id:N}", "synthetic-hash", "synthetic-valid-stamp");
        db.AdminUsers.Add(admin);
        await db.SaveChangesAsync();
        return (new BackupPlanManualExecutionStore(
                new BackupExecutionContractSqlServerTests.Factory(platform), TimeProvider.System),
            new(new(Guid.NewGuid(), Guid.NewGuid(), plan.Id, plan.CurrentVersionId, BackupRunPurpose.PlanFull),
                BackupTaskTriggerType.Manual, DateTimeOffset.UtcNow, admin.Id, admin.SecurityStamp));
    }
}
