using System.Data.Common;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupPlanTaskCreationSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 4, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(BackupRunPurpose.PlanFull, BackupType.Full, false)]
    [InlineData(BackupRunPurpose.PlanDifferential, BackupType.Differential, false)]
    [InlineData(BackupRunPurpose.AdHocCopyOnlyFull, BackupType.Full, true)]
    public async Task ManualCreationFreezesPlatformConfigurationAndNeverCreatesAnAttempt(
        BackupRunPurpose purpose, BackupType type, bool copyOnly)
    {
        var graph = await GraphAsync();
        using var provider = database.CreateServiceProvider();
        var service = new BackupPlanTaskCreationService(provider.GetRequiredService<IBackupPlanTaskCreationStore>(), new FrozenTime());
        var request = Request(graph, purpose);
        var created = await service.CreateManualAsync(request, graph.Actor);
        Assert.Equal(BackupPlanTaskCreationCode.Created, created.Code);
        Assert.Equal(request.TaskId, created.Task!.TaskId);
        Assert.Equal(graph.Plan.Id, created.Task.PlanId);
        Assert.Equal(graph.Plan.CurrentVersionId, created.Task.PlanVersionId);
        Assert.Equal(graph.Managed.Id, created.Task.DatabaseId);
        Assert.Equal(purpose, created.Task.Purpose);
        Assert.Equal(type, created.Task.BackupType);
        Assert.Equal(BackupTaskTriggerType.Manual, created.Task.TriggerType);
        await using var db = database.CreateContext();
        var task = await db.BackupTasks.SingleAsync(x => x.Id == request.TaskId);
        var frozen = await db.BackupTaskSnapshots.SingleAsync(x => x.TaskId == task.Id);
        Assert.Null(task.PolicyId);
        Assert.Equal(BackupTaskStatus.Pending, task.Status);
        Assert.Equal(BackupTaskStage.Backup, task.CurrentStage);
        Assert.Null(task.CurrentBackupAttemptId);
        Assert.False(await db.BackupAttempts.AnyAsync(x => x.TaskId == task.Id));
        Assert.Equal(type, frozen.BackupType);
        Assert.Equal(purpose, frozen.Purpose);
        Assert.Equal(copyOnly, frozen.UseCopyOnly);
        Assert.Equal("v3", frozen.FileNameRuleVersion);
        Assert.Equal(graph.Instance.ConnectionAddress, frozen.ConnectionAddress);
        Assert.Equal(graph.SqlCredential.Id, frozen.SqlCredentialReferenceId);
        Assert.Equal(graph.Server.LocalBackupRootPath, frozen.LocalSqlBackupRootPath);
        Assert.Equal(graph.Target.BasePath, frozen.RemoteBasePath);
        Assert.Equal(14, frozen.LocalRetentionDays);
        Assert.Equal(28, frozen.RemoteRetentionDays);
        Assert.True(frozen.UseChecksum);
        Assert.False(frozen.UseCompression);
        Assert.Equal(120, frozen.BackupTimeoutMinutes);
        Assert.Equal(60, frozen.VerifyTimeoutMinutes);
        Assert.Equal(90, frozen.TransferTimeoutMinutes);
        Assert.Equal("UTC", frozen.TimeZoneId);
        Assert.Equal(new Counts(1, 1, 1, 1, 1), await CountsAsync(request.TaskId));
        var paths = BackupTaskPathFactory.Create(new BackupPlanPathInput(new(graph.Server.Id, graph.Server.Name, graph.Instance.Id,
            graph.Instance.Name, graph.Managed.Id, graph.Managed.DatabaseName),
            new(frozen.LocalSqlBackupRootPath, frozen.FileNameRuleVersion,
                new(frozen.SourceAccessProtocol, frozen.SourceAccessHost, frozen.SourceAccessPort,
                    frozen.SourceAccessBasePath, frozen.SourceCredentialReferenceId, frozen.SourceSftpHostKeyFingerprint)),
            new(graph.Target.Id, new(graph.Target.Protocol, graph.Target.Host, graph.Target.Port, graph.Target.BasePath,
                graph.Target.CredentialReferenceId, graph.Target.SftpHostKeyFingerprint)), purpose), Guid.NewGuid(), Now);
        Assert.EndsWith(copyOnly ? "_COPYONLY.bak" : type == BackupType.Full ? "_FULL.bak" : "_DIFF.bak", paths.LocalSqlFilePath);
        Assert.Contains(type == BackupType.Differential ? @"\diff\" : @"\full\", paths.LocalSqlFilePath, StringComparison.Ordinal);
        var old = provider.GetRequiredService<IBackupTaskExecutionStore>();
        Assert.Equal(BackupTaskStoreResultCode.NotFound, (await old.ClaimTaskAsync(task.Id, Claim())).Code);
        Assert.Equal(BackupTaskStoreResultCode.NotFound, (await old.ClaimNextAsync(Claim())).Code);
    }

    [Theory]
    [InlineData(BackupRunPurpose.PlanFull, 2)]
    [InlineData(BackupRunPurpose.PlanDifferential, 3)]
    public async Task ScheduledCreationPersistsRequestedSlotAndPurpose(BackupRunPurpose purpose, int hour)
    {
        var graph = await GraphAsync();
        var request = Request(graph, purpose) with { ScheduledSlotAtUtc = Now.AddHours(hour - 4) };
        var result = await StoreAsync(new(request, BackupTaskTriggerType.Scheduled, Now));
        Assert.Equal(BackupPlanTaskCreationCode.Created, result.Code);
        Assert.Equal(request.ScheduledSlotAtUtc, result.Task!.ScheduledSlotAtUtc);
        Assert.Equal(BackupTaskTriggerType.Scheduled, result.Task.TriggerType);
        await using var db = database.CreateContext();
        Assert.Null((await db.AuditRecords.SingleAsync(x => x.TargetId == request.TaskId.ToString("N"))).ActorAdminUserId);
    }

    [Fact]
    public async Task CoveredSlotIsPreservedWithoutComputingACombinedSchedule()
    {
        var graph = await GraphAsync();
        var request = Request(graph) with { ScheduledSlotAtUtc = Now.AddHours(-2), CoveredDifferentialSlotUtc = Now.AddHours(-1) };
        var result = await StoreAsync(new(request, BackupTaskTriggerType.Scheduled, Now));
        Assert.Equal(BackupPlanTaskCreationCode.Created, result.Code);
        Assert.Equal(request.CoveredDifferentialSlotUtc, result.Task!.CoveredDifferentialSlotUtc);
    }

    [Theory]
    [InlineData("paused", BackupPlanTaskCreationCode.PlanPaused)]
    [InlineData("offset", BackupPlanTaskCreationCode.InvalidSlot)]
    [InlineData("future", BackupPlanTaskCreationCode.InvalidSlot)]
    [InlineData("wrong_time", BackupPlanTaskCreationCode.InvalidSlot)]
    [InlineData("effective_boundary", BackupPlanTaskCreationCode.InvalidSlot)]
    [InlineData("copy_only", BackupPlanTaskCreationCode.InvalidRequest)]
    [InlineData("covered_future", BackupPlanTaskCreationCode.InvalidSlot)]
    public async Task InvalidScheduledRequestsLeaveNoPartialRows(string violation, BackupPlanTaskCreationCode expected)
    {
        var graph = await GraphAsync();
        if (violation == "paused")
        {
            await using var db = database.CreateContext();
            (await db.BackupPlans.SingleAsync(x => x.Id == graph.Plan.Id)).Pause();
            await db.SaveChangesAsync();
        }
        var request = Request(graph) with { ScheduledSlotAtUtc = Now.AddHours(-2) };
        request = violation switch
        {
            "offset" => request with { ScheduledSlotAtUtc = Now.AddHours(-2).ToOffset(TimeSpan.FromHours(8)) },
            "future" => request with { ScheduledSlotAtUtc = Now.AddDays(1).AddHours(-2) },
            "wrong_time" => request with { ScheduledSlotAtUtc = Now.AddMinutes(-1) },
            "effective_boundary" => request with { ScheduledSlotAtUtc = graph.Plan.CurrentVersion.EffectiveFromUtc },
            "copy_only" => request with { Purpose = BackupRunPurpose.AdHocCopyOnlyFull },
            "covered_future" => request with { CoveredDifferentialSlotUtc = Now.AddMinutes(1) },
            _ => request
        };
        Assert.Equal(expected, (await StoreAsync(new(request, BackupTaskTriggerType.Scheduled, Now))).Code);
        Assert.Equal(Counts.Empty, await CountsAsync(request.TaskId));
    }

    [Fact]
    public async Task PausedPlanAllowsAuthenticatedManualCreationAndLogIsNotOpen()
    {
        var graph = await GraphAsync();
        await using (var db = database.CreateContext())
        {
            (await db.BackupPlans.SingleAsync(x => x.Id == graph.Plan.Id)).Pause();
            await db.SaveChangesAsync();
        }
        Assert.Equal(BackupPlanTaskCreationCode.Created, (await StoreAsync(Manual(graph, Request(graph)))).Code);
        var log = Request(graph, BackupRunPurpose.PlanLog);
        Assert.Equal(BackupPlanTaskCreationCode.StageNotOpen, (await StoreAsync(Manual(graph, log))).Code);
        Assert.Equal(Counts.Empty, await CountsAsync(log.TaskId));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stamp")]
    [InlineData("locked")]
    [InlineData("disabled")]
    [InlineData("scheduled_actor")]
    public async Task AdministratorIdentityAndStampAreChecked(string violation)
    {
        var graph = await GraphAsync();
        var command = Manual(graph, Request(graph));
        if (violation == "missing") command = command with { ActorAdminUserId = null, ActorSecurityStamp = null };
        if (violation == "stamp") command = command with { ActorSecurityStamp = "other-synthetic-stamp" };
        if (violation == "scheduled_actor") command = command with { TriggerType = BackupTaskTriggerType.Scheduled };
        if (violation is "locked" or "disabled")
        {
            await using var db = database.CreateContext();
            var admin = await db.AdminUsers.SingleAsync(x => x.Id == graph.Actor.AdminUserId);
            if (violation == "disabled") db.Entry(admin).Property(x => x.IsEnabled).CurrentValue = false;
            else admin.RecordFailedLogin(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), 1, TimeSpan.FromHours(1));
            await db.SaveChangesAsync();
        }
        Assert.Equal(BackupPlanTaskCreationCode.AuthenticationRequired, (await StoreAsync(command)).Code);
        Assert.Equal(Counts.Empty, await CountsAsync(command.Request.TaskId));
    }

    [Theory]
    [InlineData("database")]
    [InlineData("unavailable")]
    [InlineData("instance")]
    [InlineData("server")]
    [InlineData("sql_credential")]
    [InlineData("source_credential")]
    [InlineData("target")]
    [InlineData("remote_credential")]
    [InlineData("path")]
    public async Task InvalidConfigurationIsRejectedAtomically(string violation)
    {
        var graph = await GraphAsync();
        await using (var db = database.CreateContext())
        {
            switch (violation)
            {
                case "database": (await db.ManagedDatabases.SingleAsync(x => x.Id == graph.Managed.Id)).SetManaged(false); break;
                case "unavailable":
                    (await db.ManagedDatabases.SingleAsync(x => x.Id == graph.Managed.Id))
                    .RefreshDiscovery(Now, false, false, "SIMPLE", "OFFLINE"); break;
                case "instance": (await db.DatabaseInstances.SingleAsync(x => x.Id == graph.Instance.Id)).SetEnabled(false); break;
                case "server": (await db.DatabaseServers.SingleAsync(x => x.Id == graph.Server.Id)).SetEnabled(false); break;
                case "target": (await db.StorageTargets.SingleAsync(x => x.Id == graph.Target.Id)).SetEnabled(false); break;
                case "path":
                    db.Entry(await db.DatabaseServers.SingleAsync(x => x.Id == graph.Server.Id))
                    .Property(x => x.LocalBackupRootPath).CurrentValue = @"D:\" + new string('a', 260); break;
                default:
                    var credentialId = violation == "sql_credential" ? graph.SqlCredential.Id
                        : violation == "source_credential" ? graph.SourceCredential.Id : graph.RemoteCredential.Id;
                    (await db.CredentialReferences.SingleAsync(x => x.Id == credentialId)).SetEnabled(false); break;
            }
            await db.SaveChangesAsync();
        }
        var request = Request(graph);
        Assert.Equal(BackupPlanTaskCreationCode.ConfigurationUnavailable, (await StoreAsync(Manual(graph, request))).Code);
        Assert.Equal(Counts.Empty, await CountsAsync(request.TaskId));
    }

    [Fact]
    public async Task CrossPlanVersionAndModeMismatchAreRejected()
    {
        var graph = await GraphAsync();
        var other = await GraphAsync(BackupPlanMode.Full);
        var cross = Request(graph) with { PlanVersionId = other.Plan.CurrentVersionId };
        Assert.Equal(BackupPlanTaskCreationCode.VersionConflict, (await StoreAsync(Manual(graph, cross))).Code);
        Assert.Equal(Counts.Empty, await CountsAsync(cross.TaskId));
        var diff = Request(other, BackupRunPurpose.PlanDifferential);
        Assert.Equal(BackupPlanTaskCreationCode.PurposeNotInMode, (await StoreAsync(Manual(other, diff))).Code);
        Assert.Equal(Counts.Empty, await CountsAsync(diff.TaskId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedVersionConflictsWithoutSwitchingSnapshotDuringSubmission(bool holdUncommittedRevision)
    {
        var graph = await GraphAsync();
        var command = Manual(graph, Request(graph));
        await using var writer = database.CreateContext();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        var plan = await writer.BackupPlans.Include(x => x.Versions).SingleAsync(x => x.Id == graph.Plan.Id);
        plan.Revise(Guid.NewGuid(), Definition(graph.Target.Id, 29), Now.AddMinutes(-30));
        await writer.SaveChangesAsync();
        if (!holdUncommittedRevision) await transaction.CommitAsync();
        var signal = new PlanReadSignal();
        var factory = new TestFactory(database, signal);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var create = new BackupPlanTaskCreationStore(factory).CreateAsync(command, timeout.Token);
        await signal.Entered.Task.WaitAsync(timeout.Token);
        if (holdUncommittedRevision) await transaction.CommitAsync();
        Assert.Equal(BackupPlanTaskCreationCode.VersionConflict, (await create).Code);
        Assert.Equal(Counts.Empty, await CountsAsync(command.Request.TaskId));
        AssertDisposed(factory);
    }

    [Fact]
    public async Task HistoricalScheduledVersionOwnsBoundaryButNotLaterSlots()
    {
        var graph = await GraphAsync();
        var old = Request(graph) with { ScheduledSlotAtUtc = Now.AddHours(-2) };
        await using (var db = database.CreateContext())
        {
            var plan = await db.BackupPlans.Include(x => x.Versions).SingleAsync(x => x.Id == graph.Plan.Id);
            plan.Revise(Guid.NewGuid(), Definition(graph.Target.Id, 29), old.ScheduledSlotAtUtc!.Value);
            await db.SaveChangesAsync();
        }
        var created = await StoreAsync(new(old, BackupTaskTriggerType.Scheduled, Now));
        Assert.Equal(BackupPlanTaskCreationCode.Created, created.Code);
        Assert.Equal(old.PlanVersionId, created.Task!.PlanVersionId);
        var later = old with { TaskId = Guid.NewGuid(), MutationId = Guid.NewGuid(), ScheduledSlotAtUtc = Now.AddDays(1).AddHours(-2) };
        Assert.Equal(BackupPlanTaskCreationCode.InvalidSlot,
            (await StoreAsync(new(later, BackupTaskTriggerType.Scheduled, Now.AddDays(1)))).Code);
        Assert.Equal(Counts.Empty, await CountsAsync(later.TaskId));
    }

    [Fact]
    public async Task ReplaySurvivesConfigurationAndVersionChangesButRechecksAuthentication()
    {
        var graph = await GraphAsync();
        var command = Manual(graph, Request(graph));
        var created = await StoreAsync(command);
        Assert.Equal(BackupPlanTaskCreationCode.Created, created.Code);
        await using (var db = database.CreateContext())
        {
            var plan = await db.BackupPlans.Include(x => x.Versions).SingleAsync(x => x.Id == graph.Plan.Id);
            plan.Revise(Guid.NewGuid(), Definition(graph.Target.Id, 29), Now.AddMinutes(-30));
            plan.Pause();
            var server = await db.DatabaseServers.SingleAsync(x => x.Id == graph.Server.Id);
            server.Update("新合成源", @"D:\Changed", new(FileTransferProtocol.Smb, "changed-host", null,
                "changed-share", graph.SourceCredential.Id, null), null);
            server.SetEnabled(false);
            (await db.DatabaseInstances.SingleAsync(x => x.Id == graph.Instance.Id)).UpdateConnection(
                "changed-sql", graph.SqlCredential.Id, true, false, null, 50);
            var target = await db.StorageTargets.SingleAsync(x => x.Id == graph.Target.Id);
            target.UpdateEndpoint(new(FileTransferProtocol.Smb, "changed-remote", null, "changed-folder", graph.RemoteCredential.Id, null));
            target.SetEnabled(false);
            (await db.ManagedDatabases.SingleAsync(x => x.Id == graph.Managed.Id)).SetManaged(false);
            await db.SaveChangesAsync();
        }
        var replay = await StoreAsync(command with { NowUtc = Now.AddDays(1) });
        Assert.Equal(BackupPlanTaskCreationCode.AlreadyApplied, replay.Code);
        Assert.Equal(created.Task, replay.Task);
        Assert.Equal(new Counts(1, 1, 1, 1, 1), await CountsAsync(command.Request.TaskId));
        await using var verify = database.CreateContext();
        var frozen = await verify.BackupTaskSnapshots.SingleAsync(x => x.TaskId == command.Request.TaskId);
        Assert.Equal(graph.Server.Name, frozen.ServerName);
        Assert.Equal(graph.Server.LocalBackupRootPath, frozen.LocalSqlBackupRootPath);
        Assert.Equal(graph.Instance.ConnectionAddress, frozen.ConnectionAddress);
        Assert.Equal(graph.Target.BasePath, frozen.RemoteBasePath);
        Assert.Equal(14, frozen.LocalRetentionDays);
        (await verify.AdminUsers.SingleAsync(x => x.Id == graph.Actor.AdminUserId))
            .ChangePassword("synthetic-new-hash", "synthetic-new-stamp");
        await verify.SaveChangesAsync();
        Assert.Equal(BackupPlanTaskCreationCode.AuthenticationRequired, (await StoreAsync(command)).Code);
        Assert.Equal(BackupPlanTaskCreationCode.AlreadyApplied,
            (await StoreAsync(command with { ActorSecurityStamp = "synthetic-new-stamp" })).Code);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("plan")]
    [InlineData("version")]
    [InlineData("purpose")]
    [InlineData("slot")]
    [InlineData("covered")]
    [InlineData("trigger")]
    [InlineData("actor")]
    public async Task SameMutationWithChangedLogicalRequestIsAConflict(string field)
    {
        var graph = await GraphAsync();
        var command = Manual(graph, Request(graph));
        Assert.Equal(BackupPlanTaskCreationCode.Created, (await StoreAsync(command)).Code);
        var changed = Change(command, field);
        if (field == "actor")
        {
            var other = await GraphAsync();
            changed = command with { ActorAdminUserId = other.Actor.AdminUserId, ActorSecurityStamp = other.Actor.SecurityStamp };
        }
        Assert.Equal(BackupPlanTaskCreationCode.RequestConflict, (await StoreAsync(changed)).Code);
        Assert.Equal(new Counts(1, 1, 1, 1, 1), await CountsAsync(command.Request.TaskId));
        if (changed.Request.TaskId != command.Request.TaskId) Assert.Equal(Counts.Empty, await CountsAsync(changed.Request.TaskId));
    }

    [Fact]
    public async Task ConcurrentSameSlotAndSameMutationPersistExactlyOneTaskAndBindEveryRequest()
    {
        var graph = await GraphAsync();
        var first = new CreateBackupPlanTaskCommand(Request(graph) with { ScheduledSlotAtUtc = Now.AddHours(-2) },
            BackupTaskTriggerType.Scheduled, Now);
        var second = first with { Request = first.Request with { TaskId = Guid.NewGuid(), MutationId = Guid.NewGuid() } };
        var results = await Task.WhenAll(StoreAsync(first), StoreAsync(second));
        Assert.Single(results, x => x.Code == BackupPlanTaskCreationCode.Created);
        Assert.Single(results, x => x.Code == BackupPlanTaskCreationCode.AlreadyExists);
        Assert.Equal(results[0].Task, results[1].Task);
        var id = results[0].Task!.TaskId;
        Assert.Equal(new Counts(1, 1, 2, 2, 2), await CountsAsync(id));
        foreach (var command in new[] { first, second })
        {
            Assert.Equal(BackupPlanTaskCreationCode.AlreadyApplied, (await StoreAsync(command)).Code);
            Assert.Equal(BackupPlanTaskCreationCode.RequestConflict, (await StoreAsync(Change(command, "task"))).Code);
            Assert.Equal(BackupPlanTaskCreationCode.RequestConflict, (await StoreAsync(Change(command, "covered"))).Code);
        }
        var same = Manual(graph, Request(graph));
        var sameResults = await Task.WhenAll(StoreAsync(same), StoreAsync(same));
        Assert.Single(sameResults, x => x.Code == BackupPlanTaskCreationCode.Created);
        Assert.Single(sameResults, x => x.Code == BackupPlanTaskCreationCode.AlreadyApplied);
        Assert.Equal(new Counts(1, 1, 1, 1, 1), await CountsAsync(same.Request.TaskId));
        await using var db = database.CreateContext();
        Assert.Equal(2, await db.BackupTasks.CountAsync(x => x.PlanId == graph.Plan.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualUniqueKeyRecoveryUsesTheSameRequestComparison(bool conflictingRequest)
    {
        var graph = await GraphAsync();
        var original = Manual(graph, Request(graph));
        Assert.Equal(BackupPlanTaskCreationCode.Created, (await StoreAsync(original)).Code);
        if (conflictingRequest)
        {
            // 首次查询人为模拟尚未看到并发提交，INSERT 必须真正撞到 MutationId 唯一索引。
            var hidden = new HideOneLookup("BackupTaskStateChanges", original.Request.MutationId);
            var failures = new SaveFailureRecorder();
            var factory = new TestFactory(database, hidden, failures);
            var changed = original with { Request = original.Request with { TaskId = Guid.NewGuid(), Purpose = BackupRunPurpose.PlanDifferential } };
            Assert.Equal(BackupPlanTaskCreationCode.RequestConflict,
                (await new BackupPlanTaskCreationStore(factory).CreateAsync(changed)).Code);
            Assert.Equal(Counts.Empty, await CountsAsync(changed.Request.TaskId));
            Assert.Contains(failures.SqlFailures, x => x is 2601 or 2627);
            AssertDisposed(factory);
        }
        else
        {
            var scheduled = new CreateBackupPlanTaskCommand(Request(graph) with { ScheduledSlotAtUtc = Now.AddHours(-2) },
                BackupTaskTriggerType.Scheduled, Now);
            var created = await StoreAsync(scheduled);
            var next = scheduled with { Request = scheduled.Request with { TaskId = Guid.NewGuid(), MutationId = Guid.NewGuid() } };
            var hidden = new HideOneLookup("BackupTasks", graph.Plan.Id);
            var failures = new SaveFailureRecorder();
            var factory = new TestFactory(database, hidden, failures);
            var recovered = await new BackupPlanTaskCreationStore(factory).CreateAsync(next);
            Assert.Equal(BackupPlanTaskCreationCode.AlreadyExists, recovered.Code);
            Assert.Equal(created.Task, recovered.Task);
            Assert.Contains(failures.SqlFailures, x => x is 2601 or 2627);
            Assert.Equal(BackupPlanTaskCreationCode.AlreadyApplied, (await StoreAsync(next)).Code);
            Assert.Equal(BackupPlanTaskCreationCode.RequestConflict, (await StoreAsync(Change(next, "task"))).Code);
            AssertDisposed(factory);
        }
        Assert.Equal(new Counts(1, 1, 1, 1, 1), await CountsAsync(original.Request.TaskId));
    }

    [Fact]
    public async Task SameSlotWithDifferentCoveredSlotConflictsAndManualTasksDoNotCollapse()
    {
        var graph = await GraphAsync();
        var command = new CreateBackupPlanTaskCommand(Request(graph) with { ScheduledSlotAtUtc = Now.AddHours(-2) },
            BackupTaskTriggerType.Scheduled, Now);
        Assert.Equal(BackupPlanTaskCreationCode.Created, (await StoreAsync(command)).Code);
        var changed = command with { Request = command.Request with { TaskId = Guid.NewGuid(), MutationId = Guid.NewGuid(), CoveredDifferentialSlotUtc = Now.AddHours(-1) } };
        Assert.Equal(BackupPlanTaskCreationCode.RequestConflict, (await StoreAsync(changed)).Code);
        Assert.Equal(Counts.Empty, await CountsAsync(changed.Request.TaskId));
        var manual = Manual(graph, Request(graph));
        var another = Manual(graph, Request(graph));
        Assert.Equal(BackupPlanTaskCreationCode.Created, (await StoreAsync(manual)).Code);
        Assert.Equal(BackupPlanTaskCreationCode.Created, (await StoreAsync(another)).Code);
        Assert.Equal(BackupPlanTaskCreationCode.RequestConflict,
            (await StoreAsync(manual with { Request = manual.Request with { MutationId = Guid.NewGuid() } })).Code);
    }

    [Fact]
    public async Task ConcurrentDifferentRequestsSharingMutationReturnOneSuccessAndOneConflict()
    {
        var graph = await GraphAsync();
        var first = Manual(graph, Request(graph));
        var second = first with { Request = first.Request with { TaskId = Guid.NewGuid(), Purpose = BackupRunPurpose.PlanDifferential } };
        var results = await Task.WhenAll(StoreAsync(first), StoreAsync(second));
        Assert.Single(results, x => x.Code == BackupPlanTaskCreationCode.Created);
        Assert.Single(results, x => x.Code == BackupPlanTaskCreationCode.RequestConflict);
        var winner = results.Single(x => x.Task is not null).Task!;
        Assert.Equal(new Counts(1, 1, 1, 1, 1), await CountsAsync(winner.TaskId));
        var loserId = winner.TaskId == first.Request.TaskId ? second.Request.TaskId : first.Request.TaskId;
        Assert.Equal(Counts.Empty, await CountsAsync(loserId));
    }

    [Fact]
    public async Task OptimisticConcurrencyFailureRollsBackAllCreationRecords()
    {
        var graph = await GraphAsync();
        var command = Manual(graph, Request(graph));
        var factory = new TestFactory(database, new ConcurrencyFailure());
        Assert.Equal(BackupPlanTaskCreationCode.ConcurrencyConflict,
            (await new BackupPlanTaskCreationStore(factory).CreateAsync(command)).Code);
        Assert.Equal(Counts.Empty, await CountsAsync(command.Request.TaskId));
        AssertDisposed(factory);
    }

    [Fact]
    public async Task EntrypointResolvesWithOnlyPlatformServicesAndCreatesNoExternalWork()
    {
        var graph = await GraphAsync();
        using var provider = database.CreateServiceProvider();
        // 这里没有注册凭据解密器、目标 SQL 或文件访问端口；创建必须仍能完成。
        var service = provider.GetRequiredService<BackupPlanTaskCreationService>();
        var request = Request(graph, BackupRunPurpose.PlanDifferential);
        Assert.Equal(BackupPlanTaskCreationCode.Created, (await service.CreateManualAsync(request, graph.Actor)).Code);
        await using var db = database.CreateContext();
        Assert.Equal(BackupTaskStatus.Pending, (await db.BackupTasks.SingleAsync(x => x.Id == request.TaskId)).Status);
        Assert.False(await db.BackupAttempts.AnyAsync(x => x.TaskId == request.TaskId));
        var receipt = await db.BackupTaskStateChanges.SingleAsync(x => x.MutationId == request.MutationId);
        Assert.StartsWith("plan.create.v1|", receipt.Message);
        Assert.DoesNotContain(graph.Actor.SecurityStamp, receipt.Message!, StringComparison.Ordinal);
        Assert.Null(receipt.FromStatus);
        Assert.Equal(BackupTaskStatus.Pending, receipt.ToStatus);
        Assert.Null(receipt.BackupAttemptId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitFailureOrLostResponseCanBeReplayedWithoutPartialWrites(bool responseLost)
    {
        var graph = await GraphAsync();
        var command = Manual(graph, Request(graph));
        var factory = new TestFactory(database, new CommitFailure(responseLost));
        Assert.Equal(BackupPlanTaskCreationCode.ConcurrencyConflict,
            (await new BackupPlanTaskCreationStore(factory).CreateAsync(command)).Code);
        Assert.Equal(responseLost ? new Counts(1, 1, 1, 1, 1) : Counts.Empty, await CountsAsync(command.Request.TaskId));
        Assert.Equal(responseLost ? BackupPlanTaskCreationCode.AlreadyApplied : BackupPlanTaskCreationCode.Created,
            (await StoreAsync(command)).Code);
        Assert.Equal(new Counts(1, 1, 1, 1, 1), await CountsAsync(command.Request.TaskId));
        AssertDisposed(factory);
    }

    [Fact]
    public async Task CancellationPropagatesAndAllShortContextsAreDisposed()
    {
        var graph = await GraphAsync();
        var command = Manual(graph, Request(graph));
        using var cancellation = new CancellationTokenSource();
        var factory = new TestFactory(database, new CancelBeforeCommit(cancellation));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BackupPlanTaskCreationStore(factory).CreateAsync(command, cancellation.Token));
        Assert.Equal(Counts.Empty, await CountsAsync(command.Request.TaskId));
        AssertDisposed(factory);
    }

    private async Task<Graph> GraphAsync(BackupPlanMode mode = BackupPlanMode.FullAndDifferential)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sql = new CredentialReference(Guid.NewGuid(), $"SQL-{suffix}", CredentialKind.SqlPassword, "synthetic-user", "protected:synthetic", "dp-v1");
        var source = new CredentialReference(Guid.NewGuid(), $"SRC-{suffix}", CredentialKind.SmbPassword, "synthetic-user", "protected:synthetic", "dp-v1");
        var remote = new CredentialReference(Guid.NewGuid(), $"DST-{suffix}", CredentialKind.SmbPassword, "synthetic-user", "protected:synthetic", "dp-v1");
        var server = new DatabaseServer(Guid.NewGuid(), $"SRC-{suffix}", @"D:\Synthetic", new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", source.Id, null));
        var instance = new DatabaseInstance(Guid.NewGuid(), server.Id, $"SQL-{suffix}", $"synthetic-sql-{suffix}", sql.Id, true, false, null, 30);
        var managed = new ManagedDatabase(Guid.NewGuid(), instance.Id, $"DB_{suffix}", false, true, Now, "SIMPLE", "ONLINE");
        managed.SetManaged(true);
        var target = new StorageTarget(Guid.NewGuid(), $"DST-{suffix}", new(FileTransferProtocol.Smb, "synthetic-remote", null, "synthetic-folder", remote.Id, null));
        var admin = new AdminUser(Guid.NewGuid(), $"admin-{suffix}", $"ADMIN-{suffix}", "synthetic-hash", "synthetic-stamp");
        await using (var db = database.CreateContext())
        {
            db.AddRange(sql, source, remote, server, instance, managed, target, admin);
            await db.SaveChangesAsync();
        }
        var plan = BackupPlan.Create(Guid.NewGuid(), managed.Id, $"合成计划-{suffix}", Guid.NewGuid(), Definition(target.Id, 14, mode), Now.AddDays(-2));
        using var provider = database.CreateServiceProvider();
        await new BackupPlanStore(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>()).AddAsync(plan);
        return new(plan, server, instance, managed, target, sql, source, remote, new(admin.Id, admin.Username, admin.SecurityStamp));
    }

    private static BackupPlanDefinition Definition(Guid targetId, int days, BackupPlanMode mode = BackupPlanMode.FullAndDifferential) =>
        new(mode, new(BackupScheduleType.Daily, new(2, 0), BackupWeekdays.None),
            mode == BackupPlanMode.Full ? null : new(BackupScheduleType.Daily, new(3, 0), BackupWeekdays.None),
            null, "UTC", BackupStorageMode.LocalAndRemote, targetId, days, 28, true, false, 120, 60, 90);
    private static CreateBackupPlanTaskRequest Request(Graph graph, BackupRunPurpose purpose = BackupRunPurpose.PlanFull) =>
        new(Guid.NewGuid(), Guid.NewGuid(), graph.Plan.Id, graph.Plan.CurrentVersionId, purpose);
    private static CreateBackupPlanTaskCommand Manual(Graph graph, CreateBackupPlanTaskRequest request) =>
        new(request, BackupTaskTriggerType.Manual, Now, graph.Actor.AdminUserId, graph.Actor.SecurityStamp);
    private static ClaimNextBackupTaskCommand Claim() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "合成测试", Now, Now.AddMinutes(10));
    private async Task<BackupPlanTaskCreationResult> StoreAsync(CreateBackupPlanTaskCommand command)
    {
        using var provider = database.CreateServiceProvider();
        return await provider.GetRequiredService<IBackupPlanTaskCreationStore>().CreateAsync(command);
    }
    private async Task<Counts> CountsAsync(Guid taskId)
    {
        await using var db = database.CreateContext();
        return new(await db.BackupTasks.CountAsync(x => x.Id == taskId), await db.BackupTaskSnapshots.CountAsync(x => x.TaskId == taskId),
            await db.BackupTaskStateChanges.CountAsync(x => x.TaskId == taskId),
            await db.AuditRecords.CountAsync(x => x.TargetId == taskId.ToString("N")), await db.TaskEvents.CountAsync(x => x.TaskId == taskId));
    }
    private static CreateBackupPlanTaskCommand Change(CreateBackupPlanTaskCommand command, string field) => command with
    {
        Request = field switch
        {
            "task" => command.Request with { TaskId = Guid.NewGuid() },
            "plan" => command.Request with { PlanId = Guid.NewGuid() },
            "version" => command.Request with { PlanVersionId = Guid.NewGuid() },
            "purpose" => command.Request with { Purpose = BackupRunPurpose.AdHocCopyOnlyFull },
            "slot" => command.Request with { ScheduledSlotAtUtc = Now.AddHours(-2) },
            "covered" => command.Request with { CoveredDifferentialSlotUtc = Now.AddHours(-1) },
            _ => command.Request
        },
        TriggerType = field == "trigger" ? BackupTaskTriggerType.Scheduled : command.TriggerType,
        ActorAdminUserId = field == "trigger" ? null : command.ActorAdminUserId,
        ActorSecurityStamp = field == "trigger" ? null : command.ActorSecurityStamp
    };
    private static void AssertDisposed(TestFactory factory) => Assert.All(factory.Contexts,
        context => Assert.Throws<ObjectDisposedException>(() => context.ChangeTracker.Entries().ToArray()));
    private sealed record Counts(int Tasks, int Snapshots, int History, int Audits, int Events)
    {
        internal static Counts Empty { get; } = new(0, 0, 0, 0, 0);
    }
    private sealed record Graph(BackupPlan Plan, DatabaseServer Server, DatabaseInstance Instance, ManagedDatabase Managed,
        StorageTarget Target, CredentialReference SqlCredential, CredentialReference SourceCredential,
        CredentialReference RemoteCredential, AdminSession Actor);
    private sealed class FrozenTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class TestFactory(PlatformDatabaseSqlServerFixture database, params IInterceptor[] interceptors) : IDbContextFactory<PlatformDbContext>
    {
        internal List<PlatformDbContext> Contexts { get; } = [];
        public PlatformDbContext CreateDbContext()
        {
            var context = database.CreateContext(interceptors);
            Contexts.Add(context);
            return context;
        }
        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class PlanReadSignal : DbCommandInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDLOCK", StringComparison.Ordinal)) Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
    private sealed class HideOneLookup(string table, Guid soughtId) : DbCommandInterceptor
    {
        private bool _hidden;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!_hidden && command.CommandText.Contains($"FROM [{table}]", StringComparison.Ordinal)
                && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal))
            {
                var parameter = command.Parameters.Cast<DbParameter>().FirstOrDefault(x => x.Value is Guid id && id == soughtId);
                if (parameter is not null) { parameter.Value = Guid.NewGuid(); _hidden = true; }
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class SaveFailureRecorder : SaveChangesInterceptor
    {
        internal List<int> SqlFailures { get; } = [];
        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            // SQL 批处理可在读取后续结果时抛错，须从 SaveChanges 失败观察完整的 EF 异常。
            if (eventData.Exception is DbUpdateException { InnerException: SqlException sql }) SqlFailures.Add(sql.Number);
            return Task.CompletedTask;
        }
    }
    private sealed class CommitFailure(bool responseLost) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            responseLost ? ValueTask.FromResult(result) : throw new DbUpdateException("合成提交失败。");
        public override Task TransactionCommittedAsync(DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            responseLost ? throw new DbUpdateException("合成提交响应丢失。") : Task.CompletedTask;
    }
    private sealed class CancelBeforeCommit(CancellationTokenSource cancellation) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }
    private sealed class ConcurrencyFailure : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateConcurrencyException("合成并发版本冲突。");
    }
}
