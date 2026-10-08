using System.Data.Common;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupPlanSchedulingSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 4, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(2, 3, 2)]
    [InlineData(3, 3, 1)]
    [InlineData(4, 3, 1)]
    public async Task DueWorkUsesImmutableSnapshotsAndRemainsIsolatedFromWorker(int full, int diff, int count)
    {
        var plan = await PlanAsync(full, diff);
        var result = await Schedule(plan.Id);
        Assert.Equal(BackupPlanSchedulingCode.Created, result.Code);
        Assert.Equal(count, result.Tasks.Count);
        await using var db = database.CreateContext();
        var tasks = await db.BackupTasks.Where(x => x.PlanId == plan.Id).ToArrayAsync();
        Assert.All(tasks, x =>
        {
            Assert.Equal(BackupTaskStatus.Pending, x.Status);
            Assert.Equal(plan.CurrentVersionId, x.PlanVersionId);
            Assert.Equal(TimeSpan.Zero, x.ScheduledSlotAtUtc!.Value.Offset);
            Assert.Null(x.PolicyId);
            Assert.Null(x.CurrentBackupAttemptId);
        });
        var ids = tasks.Select(x => x.Id).ToArray();
        Assert.False(await db.BackupAttempts.AnyAsync(x => ids.Contains(x.TaskId)));
        var snapshots = await db.BackupTaskSnapshots.Where(x => ids.Contains(x.TaskId)).ToArrayAsync();
        Assert.All(snapshots, x => { Assert.Equal("v3", x.FileNameRuleVersion); Assert.False(x.UseCopyOnly); Assert.Equal(14, x.LocalRetentionDays); });
        Assert.Contains(snapshots, x => x.Purpose == BackupRunPurpose.PlanFull);
        Assert.Equal(full >= diff ? Now.UtcDateTime.Date.AddHours(diff) : (DateTimeOffset?)null,
            Assert.Single(tasks, x => x.BackupType == BackupType.Full).CoveredDifferentialSlotUtc);
        using var provider = database.CreateServiceProvider();
        foreach (var task in tasks)
            Assert.Equal(BackupTaskStoreResultCode.NotFound, (await provider.GetRequiredService<IBackupTaskExecutionStore>()
                .ClaimTaskAsync(task.Id, new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "合成", Now, Now.AddMinutes(10)))).Code);
        var before = await Counts(plan.Id);
        Assert.Equal(BackupPlanSchedulingCode.NoWork, (await Schedule(plan.Id)).Code);
        Assert.Equal(before, await Counts(plan.Id));
    }

    [Theory]
    [InlineData("failed", true)]
    [InlineData("preparation_failed", true)]
    [InlineData("preparation_no_receipt", false)]
    [InlineData("uncertain", false)]
    [InlineData("pending", false)]
    [InlineData("remote_failed", false)]
    [InlineData("remote_uncertain", false)]
    [InlineData("metadata_missing", false)]
    [InlineData("source_conflict", false)]
    [InlineData("contradictory_sql", false)]
    [InlineData("metadata_against_failure", false)]
    public async Task PersistedFullFactsControlOlderDiffWithoutRetryingFull(string outcome, bool createDiff)
    {
        var plan = await PlanAsync(4, 3);
        var full = await SeedTask(plan, BackupRunPurpose.PlanFull, Now);
        if (outcome != "pending") await Finish(full.TaskId, outcome);
        var before = await Counts(plan.Id);
        var result = await Schedule(plan.Id);
        Assert.Equal(createDiff ? BackupPlanSchedulingCode.Created : BackupPlanSchedulingCode.NoWork, result.Code);
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.BackupTasks.CountAsync(x => x.PlanId == plan.Id && x.BackupType == BackupType.Full));
        Assert.Equal(createDiff ? 1 : 0, await db.BackupTasks.CountAsync(x => x.PlanId == plan.Id && x.BackupType == BackupType.Differential));
        if (!createDiff) Assert.Equal(before, await Counts(plan.Id));
        if (outcome == "remote_uncertain")
        {
            var task = await db.BackupTasks.SingleAsync(x => x.Id == full.TaskId);
            var attempt = await db.BackupAttempts.SingleAsync(x => x.TaskId == full.TaskId);
            var set = await db.BackupSets.SingleAsync(x => x.TaskId == full.TaskId);
            var evidence = await db.BackupSetEvidence.Where(x => x.TaskId == full.TaskId).ToArrayAsync();
            Assert.Equal(BackupTaskStatus.NeedsAttention, task.Status);
            Assert.Equal(BackupSlotDisposition.Succeeded, BackupPlanSlotDispositionRules.Evaluate(task.Status, attempt.Id,
                [new(attempt.Id, attempt.BackupInvocationStatus, BackupPlanSchedulingStore.MetadataPassed(set, evidence), attempt.LocalVerifiedAtUtc is not null)],
                currentStage: task.CurrentStage));
        }
        Assert.Equal(BackupPlanSchedulingCode.NoWork, (await Schedule(plan.Id)).Code);
    }

    [Fact]
    public async Task HistoricalSuccessNotNewestFullAndManualTasksAreNotSlotFacts()
    {
        var plan = await PlanAsync(4, 3, BackupWeekdays.Monday);
        var oldFull = await SeedTask(plan, BackupRunPurpose.PlanFull, Now.AddDays(-3));
        await Finish(oldFull.TaskId, "remote_failed");
        var latest = await SeedTask(plan, BackupRunPurpose.PlanFull, Now);
        await Finish(latest.TaskId, "failed");
        Assert.Equal(BackupPlanSchedulingCode.NoWork, (await Schedule(plan.Id)).Code);
        var separate = await PlanAsync(4, 3);
        var manual = await SeedTask(separate, BackupRunPurpose.PlanFull, null);
        await Finish(manual.TaskId, "remote_failed");
        var scheduled = await Schedule(separate.Id);
        Assert.Single(scheduled.Tasks);
        Assert.Equal(BackupTaskTriggerType.Scheduled, scheduled.Tasks[0].TriggerType);
        Assert.Equal(Now.AddHours(-1), scheduled.Tasks[0].CoveredDifferentialSlotUtc);
    }

    [Fact]
    public async Task FailedLatestFullDoesNotLetAbandonedOlderFullSlotsSuppressWeeklyDiff()
    {
        var plan = await PlanAsync(4, 3, BackupWeekdays.Monday);
        await Finish((await SeedTask(plan, BackupRunPurpose.PlanFull, Now)).TaskId, "failed");
        var diff = Assert.Single((await Schedule(plan.Id)).Tasks);
        Assert.Equal(BackupType.Differential, diff.BackupType);
        Assert.Equal(Now.AddDays(-3).AddHours(-1), diff.ScheduledSlotAtUtc);
    }

    [Fact]
    public async Task ExistingDiffDispositionIsNeverOverwrittenOrCovered()
    {
        var plan = await PlanAsync(4, 3);
        var diff = await SeedTask(plan, BackupRunPurpose.PlanDifferential, Now.AddHours(-1));
        var result = await Schedule(plan.Id);
        Assert.Null(Assert.Single(result.Tasks).CoveredDifferentialSlotUtc);
        await using var db = database.CreateContext();
        Assert.Equal(BackupTaskStatus.Pending, (await db.BackupTasks.SingleAsync(x => x.Id == diff.TaskId)).Status);
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(4, 3)]
    public async Task IndependentSchedulersSerializeWholeDecisionAndRestartAddsNothing(int full, int diff)
    {
        var plan = await PlanAsync(full, diff);
        using var first = database.CreateServiceProvider();
        using var second = database.CreateServiceProvider();
        var results = await Task.WhenAll(first.GetRequiredService<IBackupPlanSchedulingStore>().ScheduleAsync(plan.Id, Now),
            second.GetRequiredService<IBackupPlanSchedulingStore>().ScheduleAsync(plan.Id, Now));
        Assert.Single(results, x => x.Code == BackupPlanSchedulingCode.Created);
        Assert.Single(results, x => x.Code == BackupPlanSchedulingCode.NoWork);
        var counts = await Counts(plan.Id);
        Assert.Equal(full < diff ? new RecordCounts(2, 2, 2, 2, 2) : new RecordCounts(1, 1, 1, 1, 1), counts);
        Assert.Equal(BackupPlanSchedulingCode.NoWork, (await Schedule(plan.Id)).Code);
        Assert.Equal(counts, await Counts(plan.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseOrRevisionCommittedWhileSchedulerWaitsIsRecomputed(bool revise)
    {
        var plan = await PlanAsync(4, 3);
        await using var writer = database.CreateContext();
        await using var tx = await writer.Database.BeginTransactionAsync();
        var locked = await writer.BackupPlans.FromSqlInterpolated(
            $"SELECT * FROM [BackupPlans] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {plan.Id}").Include(x => x.Versions).SingleAsync();
        var signal = new ReadSignal("UPDLOCK");
        var factory = new TestFactory(database, signal);
        var scheduling = new BackupPlanSchedulingStore(factory).ScheduleAsync(plan.Id, Now);
        await signal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (revise) locked.Revise(Guid.NewGuid(), Definition(1, 2), Now.AddDays(-1));
        else locked.Pause();
        await writer.SaveChangesAsync();
        await tx.CommitAsync();
        var result = await scheduling.WaitAsync(TimeSpan.FromSeconds(20));
        if (revise)
        {
            Assert.Equal(2, result.Tasks.Count);
            Assert.All(result.Tasks, x => Assert.Equal(locked.CurrentVersionId, x.PlanVersionId));
        }
        else { Assert.Equal(BackupPlanSchedulingCode.Paused, result.Code); Assert.Equal(RecordCounts.Empty, await Counts(plan.Id)); }
        AssertDisposed(factory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SchedulerCommitBeforePauseOrRevisionMatchesOtherSerialOrder(bool revise)
    {
        var plan = await PlanAsync(4, 3);
        var gate = new SaveGate();
        var scheduling = new BackupPlanSchedulingStore(new TestFactory(database, gate)).ScheduleAsync(plan.Id, Now);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var readSignal = new ReadSignal("FROM [BackupPlans]");
        var modification = Modify();
        await readSignal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(modification.IsCompleted);
        gate.Release.TrySetResult();
        var result = await scheduling.WaitAsync(TimeSpan.FromSeconds(20));
        await modification.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(plan.CurrentVersionId, Assert.Single(result.Tasks).PlanVersionId);
        Assert.Equal(Now.AddHours(-1), result.Tasks[0].CoveredDifferentialSlotUtc);

        async Task Modify()
        {
            await using var db = database.CreateContext(readSignal);
            var current = await db.BackupPlans.Include(x => x.Versions).SingleAsync(x => x.Id == plan.Id);
            if (revise) current.Revise(Guid.NewGuid(), Definition(1, 2), Now.AddMinutes(1));
            else current.Pause();
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task FactsCommittedWhileTaskReadWaitsAreUsedRatherThanOldPendingDisposition()
    {
        var plan = await PlanAsync(4, 3);
        var full = await SeedTask(plan, BackupRunPurpose.PlanFull, Now);
        await using var writer = database.CreateContext();
        await using var tx = await writer.Database.BeginTransactionAsync();
        _ = await writer.BackupTasks.FromSqlInterpolated(
            $"SELECT * FROM [BackupTasks] WITH (XLOCK, HOLDLOCK) WHERE [Id] = {full.TaskId}").SingleAsync();
        var signal = new ReadSignal("FROM [BackupTasks]");
        var scheduling = new BackupPlanSchedulingStore(new TestFactory(database, signal)).ScheduleAsync(plan.Id, Now);
        await signal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Finish(full.TaskId, "failed", writer);
        await tx.CommitAsync();
        Assert.Equal(BackupType.Differential, Assert.Single((await scheduling.WaitAsync(TimeSpan.FromSeconds(20))).Tasks).BackupType);
    }

    [Fact]
    public async Task FactsCannotChangeBetweenSelectionAndCommit()
    {
        var plan = await PlanAsync(4, 3);
        var full = await SeedTask(plan, BackupRunPurpose.PlanFull, Now);
        var gate = new QueryGate("FROM [BackupSetEvidence]");
        var scheduling = new BackupPlanSchedulingStore(new TestFactory(database, gate)).ScheduleAsync(plan.Id, Now);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var saveSignal = new SaveSignal();
        var modification = Modify();
        await saveSignal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(modification.IsCompleted);
        gate.Release.TrySetResult();
        Assert.Equal(BackupPlanSchedulingCode.NoWork, (await scheduling.WaitAsync(TimeSpan.FromSeconds(20))).Code);
        await modification.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(BackupType.Differential, Assert.Single((await Schedule(plan.Id)).Tasks).BackupType);

        async Task Modify()
        {
            await using var writer = database.CreateContext(saveSignal);
            await Finish(full.TaskId, "failed", writer);
        }
    }

    [Fact]
    public async Task InclusiveVersionBoundarySurvivesDowntimeAndUsesOldSnapshot()
    {
        var plan = await PlanAsync(2, 3);
        var old = plan.CurrentVersionId;
        await using (var db = database.CreateContext())
        {
            var current = await db.BackupPlans.Include(x => x.Versions).SingleAsync(x => x.Id == plan.Id);
            current.Revise(Guid.NewGuid(), Definition(5, 5), Now.AddHours(-1));
            await db.SaveChangesAsync();
        }
        var result = await Schedule(plan.Id);
        Assert.Equal(2, result.Tasks.Count);
        Assert.All(result.Tasks, x => Assert.Equal(old, x.PlanVersionId));
        Assert.Equal(Now.AddHours(-1), Assert.Single(result.Tasks, x => x.BackupType == BackupType.Differential).ScheduledSlotAtUtc);
        await using var read = database.CreateContext();
        var ids = result.Tasks.Select(x => x.TaskId).ToArray();
        Assert.All(await read.BackupTaskSnapshots.Where(x => ids.Contains(x.TaskId)).ToArrayAsync(), x =>
            Assert.Equal(BackupStorageMode.LocalAndRemote, x.StorageMode));
    }

    [Fact]
    public async Task InvalidDiffConfigurationRollsBackFullCreatedEarlierInSameTransaction()
    {
        var plan = await PlanAsync(2, 3);
        var recorder = new SaveSignal();
        await using (var db = database.CreateContext())
        {
            var current = await db.BackupPlans.Include(x => x.Versions).SingleAsync(x => x.Id == plan.Id);
            var valid = await db.StorageTargets.SingleAsync(x => x.Id == current.CurrentVersion.StorageTargetId);
            var disabled = new StorageTarget(Guid.NewGuid(), $"Disabled-{Guid.NewGuid():N}",
                new(FileTransferProtocol.Smb, "synthetic-remote", null, "synthetic-folder", valid.CredentialReferenceId, null), isEnabled: false);
            db.Add(disabled);
            current.Revise(Guid.NewGuid(), Definition(5, 3, targetId: disabled.Id), Now.AddHours(-3));
            await db.SaveChangesAsync();
        }
        // FULL 来自旧版本前一天，DIFF 来自新版本今天；先保存 FULL 再遇到无效 DIFF 配置。
        var factory = new TestFactory(database, recorder);
        Assert.Equal(BackupPlanSchedulingCode.ConfigurationUnavailable,
            (await new BackupPlanSchedulingStore(factory).ScheduleAsync(plan.Id, Now)).Code);
        Assert.True(recorder.Entered.Task.IsCompleted);
        Assert.Equal(RecordCounts.Empty, await Counts(plan.Id));
        AssertDisposed(factory);
    }

    [Fact]
    public async Task InvalidTimeZoneIsAPlanResultAndCreatesNoRecords()
    {
        var plan = await PlanAsync(2, 3);
        await using (var db = database.CreateContext())
        {
            // 模拟持久化版本在另一台主机上遇到不可用时区；正常领域入口会拒绝未知标识。
            Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [BackupPlanVersions] SET [TimeZoneId] = N'synthetic/nonexistent-time-zone' WHERE [Id] = {plan.CurrentVersionId}"));
        }
        Assert.Equal(BackupPlanSchedulingCode.InvalidTimeZone, (await Schedule(plan.Id)).Code);
        Assert.Equal(RecordCounts.Empty, await Counts(plan.Id));
    }

    [Theory]
    [InlineData("second_save")]
    [InlineData("before_commit")]
    [InlineData("after_commit")]
    public async Task TransactionFailureAndLostCommitResponseAreAtomicAndRestartSafe(string failure)
    {
        var plan = await PlanAsync(2, 3);
        IInterceptor interceptor = failure == "second_save" ? new SecondSaveFailure() : new CommitFailure(failure == "after_commit");
        var factory = new TestFactory(database, interceptor);
        await Assert.ThrowsAsync<IOException>(() => new BackupPlanSchedulingStore(factory).ScheduleAsync(plan.Id, Now));
        var expected = new RecordCounts(2, 2, 2, 2, 2);
        Assert.Equal(failure == "after_commit" ? expected : RecordCounts.Empty, await Counts(plan.Id));
        Assert.Equal(failure == "after_commit" ? BackupPlanSchedulingCode.NoWork : BackupPlanSchedulingCode.Created, (await Schedule(plan.Id)).Code);
        Assert.Equal(expected, await Counts(plan.Id));
        AssertDisposed(factory);
    }

    [Fact]
    public async Task OneInvalidPlanDoesNotBlockOthersAndNoExternalPortsAreRequired()
    {
        var invalid = await PlanAsync(4, 3);
        var valid = await PlanAsync(4, 3);
        await using (var db = database.CreateContext())
        {
            var managed = await db.ManagedDatabases.SingleAsync(x => x.Id == invalid.DatabaseId);
            managed.SetManaged(false);
            await db.SaveChangesAsync();
        }
        using var provider = database.CreateServiceProvider();
        var scheduler = new BackupPlanScheduler(provider.GetRequiredService<IBackupPlanSchedulingStore>(), new FrozenTime());
        var results = await scheduler.RunOnceAsync();
        Assert.Equal(BackupPlanSchedulingCode.ConfigurationUnavailable, Assert.Single(results, x => x.PlanId == invalid.Id).Code);
        Assert.Equal(BackupPlanSchedulingCode.Created, Assert.Single(results, x => x.PlanId == valid.Id).Code);
        Assert.Equal(RecordCounts.Empty, await Counts(invalid.Id));
        Assert.Equal(new RecordCounts(1, 1, 1, 1, 1), await Counts(valid.Id));
    }

    [Fact]
    public async Task CancellationRollsBackAndDisposesContexts()
    {
        var plan = await PlanAsync(2, 3);
        using var cancellation = new CancellationTokenSource();
        var factory = new TestFactory(database, new CancelCommit(cancellation));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BackupPlanSchedulingStore(factory).ScheduleAsync(plan.Id, Now, cancellation.Token));
        Assert.Equal(RecordCounts.Empty, await Counts(plan.Id));
        AssertDisposed(factory);
    }

    [Fact]
    public async Task PlatformReadFailureIsNotMistakenForInvalidConfiguration()
    {
        var plan = await PlanAsync(2, 3);
        var factory = new TestFactory(database, new PlatformReadFailure());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BackupPlanSchedulingStore(factory).ScheduleAsync(plan.Id, Now));
        Assert.Equal(RecordCounts.Empty, await Counts(plan.Id));
        AssertDisposed(factory);
    }

    [Theory]
    [InlineData("comparison_missing")]
    [InlineData("header_missing")]
    [InlineData("history_missing")]
    [InlineData("history_duplicate")]
    [InlineData("source_conflict")]
    [InlineData("unknown")]
    [InlineData("active_only")]
    [InlineData("later_unknown")]
    public async Task IncompleteOrContradictoryMetadataIsNeverReportedAsPassed(string defect)
    {
        var plan = await PlanAsync(4, 3);
        var full = await SeedTask(plan, BackupRunPurpose.PlanFull, Now);
        await Finish(full.TaskId, "remote_failed");
        await using var db = database.CreateContext();
        var set = await db.BackupSets.SingleAsync(x => x.TaskId == full.TaskId);
        var rows = (await db.BackupSetEvidence.Where(x => x.TaskId == full.TaskId).ToArrayAsync()).ToList();
        Assert.True(BackupPlanSchedulingStore.MetadataPassed(set, rows));
        var source = defect switch
        {
            "comparison_missing" => BackupSetEvidenceSource.Comparison,
            "header_missing" => BackupSetEvidenceSource.BackupHeader,
            _ => BackupSetEvidenceSource.Msdb
        };
        if (defect.EndsWith("missing", StringComparison.Ordinal)) rows.RemoveAll(x => x.Source == source);
        else if (defect == "history_duplicate") rows.Add(rows.Single(x => x.Source == source));
        else if (defect == "source_conflict") set.MarkMetadataConflict();
        else if (defect is "unknown" or "later_unknown")
            rows.Add(Evidence(set, BackupSetEvidenceSource.Comparison, set.Metadata,
                new(BackupMetadataState.Known, DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields), 2));
        else rows.Clear();
        Assert.False(BackupPlanSchedulingStore.MetadataPassed(set, rows));
    }

    private async Task<BackupPlan> PlanAsync(int full, int diff, BackupWeekdays days = BackupWeekdays.None)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sql = new CredentialReference(Guid.NewGuid(), $"SQL-{suffix}", CredentialKind.SqlPassword, "synthetic", "protected:synthetic", "dp-v1");
        var source = new CredentialReference(Guid.NewGuid(), $"SRC-{suffix}", CredentialKind.SmbPassword, "synthetic", "protected:synthetic", "dp-v1");
        var server = new DatabaseServer(Guid.NewGuid(), $"SRC-{suffix}", @"D:\Synthetic", new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", source.Id, null));
        var instance = new DatabaseInstance(Guid.NewGuid(), server.Id, $"SQL-{suffix}", $"synthetic-sql-{suffix}", sql.Id, true, false, null, 30);
        var managed = new ManagedDatabase(Guid.NewGuid(), instance.Id, $"DB_{suffix}", false, true, Now, "SIMPLE", "ONLINE");
        managed.SetManaged(true);
        var target = new StorageTarget(Guid.NewGuid(), $"DST-{suffix}", new(FileTransferProtocol.Smb, "synthetic-remote", null, "synthetic-folder", source.Id, null));
        await using (var db = database.CreateContext()) { db.AddRange(sql, source, server, instance, managed, target); await db.SaveChangesAsync(); }
        var plan = BackupPlan.Create(Guid.NewGuid(), managed.Id, $"合成计划-{suffix}", Guid.NewGuid(), Definition(full, diff, days, target.Id), Now.AddMonths(-2));
        using var provider = database.CreateServiceProvider();
        await new BackupPlanStore(provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>()).AddAsync(plan);
        return plan;
    }
    private static BackupPlanDefinition Definition(int full, int diff, BackupWeekdays days = BackupWeekdays.None, Guid? targetId = null) =>
        new(BackupPlanMode.FullAndDifferential, new(BackupScheduleType.Daily, new(full, 0), BackupWeekdays.None),
            new(days == BackupWeekdays.None ? BackupScheduleType.Daily : BackupScheduleType.Weekly, new(diff, 0), days),
            null, "UTC", targetId is null ? BackupStorageMode.LocalOnly : BackupStorageMode.LocalAndRemote, targetId, 14, targetId is null ? null : 28, true, false, 120, 60, 90);
    private async Task<BackupPlanSchedulingResult> Schedule(Guid id)
    {
        using var provider = database.CreateServiceProvider();
        return await provider.GetRequiredService<IBackupPlanSchedulingStore>().ScheduleAsync(id, Now);
    }
    private async Task<CreatedBackupPlanTask> SeedTask(BackupPlan plan, BackupRunPurpose purpose, DateTimeOffset? slot)
    {
        using var provider = database.CreateServiceProvider();
        var request = new CreateBackupPlanTaskRequest(Guid.NewGuid(), Guid.NewGuid(), plan.Id, plan.CurrentVersionId, purpose, slot);
        var command = new CreateBackupPlanTaskCommand(request, BackupTaskTriggerType.Scheduled, Now);
        if (slot is null)
        {
            // 手动任务仅作为历史夹具；权限路径已由 8-2 单独验收。
            await using var db = database.CreateContext();
            var task = BackupTask.ForPlan(request.TaskId, plan.Id, plan.CurrentVersionId, purpose, BackupTaskTriggerType.Manual, null);
            var config = (await BackupTaskConfigurationReader.ReadAsync(db, plan.DatabaseId, plan.CurrentVersion.StorageTargetId, "v3", default))!;
            db.AddRange(task, BackupTaskSnapshot.ForPlan(task, plan.CurrentVersion, plan.Name, purpose, config.Identity, config.SqlTarget, config.Source, config.RemoteTarget));
            await db.SaveChangesAsync();
            return new(task.Id, plan.Id, plan.CurrentVersionId, plan.DatabaseId, purpose, task.BackupType, task.TriggerType, null, null);
        }
        var result = await provider.GetRequiredService<IBackupPlanTaskCreationStore>().CreateAsync(command);
        Assert.Equal(BackupPlanTaskCreationCode.Created, result.Code);
        return result.Task!;
    }
    private async Task Finish(Guid taskId, string outcome, PlatformDbContext? existing = null)
    {
        await using var owned = existing is null ? database.CreateContext() : null;
        var db = existing ?? owned!;
        var task = await db.BackupTasks.SingleAsync(x => x.Id == taskId);
        var snapshot = await db.BackupTaskSnapshots.SingleAsync(x => x.TaskId == taskId);
        if (outcome is "preparation_failed" or "preparation_no_receipt")
        {
            task.RejectPendingBackup(snapshot.StorageMode, Now, "backup_path_invalid", "合成认领前失败。");
            if (outcome == "preparation_failed")
                db.Add(new BackupTaskStateChange(Guid.NewGuid(), taskId, BackupTaskStatus.Pending, BackupTaskStage.Backup,
                    BackupTaskStatus.Failed, BackupTaskStage.Backup, null, "execution.preparation_failed", null, Now));
            await db.SaveChangesAsync();
            return;
        }
        var id = Guid.NewGuid();
        var at = task.ScheduledSlotAtUtc ?? Now.AddHours(-2);
        var paths = new BackupAttemptPaths($@"D:\Synthetic\{id:N}_FULL.bak", $@"\\synthetic\share\{id:N}_FULL.bak", null, null, null);
        var attempt = new BackupAttempt(id, taskId, 1, at, paths);
        var lease = Guid.NewGuid();
        task.ClaimExecution(snapshot.StorageMode, id, lease, "合成事实", at, at.AddMinutes(10));
        attempt.MarkBackupRunning(at);
        if (outcome is "failed" or "contradictory_sql" or "metadata_against_failure")
        {
            attempt.RecordBackupConfirmedFailed(at.AddMinutes(1), "synthetic.sql_failed");
            task.RecordConfirmedFailure(snapshot.StorageMode, lease, at.AddMinutes(1), "synthetic.sql_failed", "合成明确失败");
            if (outcome is "contradictory_sql" or "metadata_against_failure")
            {
                // 登记端口保存 SQL 成功声明，即使该声明与 Attempt 明确失败矛盾，也不得放行旧 DIFF。
                var metadata = outcome == "metadata_against_failure" ? BackupSetTestData.Full() : new BackupSetMetadata();
                var set = new BackupSet(Guid.NewGuid(), taskId, id, snapshot.DatabaseId, metadata,
                    new(null, BackupCompletionTimeSource.Unknown, BackupCompletionTimeReason.SqlFinishMissing),
                    new(BackupMetadataState.Known, DifferentialBaselineConclusion.Unknown, DifferentialBaselineReason.MissingFields), null, outcome == "contradictory_sql");
                set.Reconcile(set.Metadata, set.Completion, set.Assessment, null, outcome == "contradictory_sql");
                db.Add(set);
            }
        }
        else if (outcome == "uncertain")
        {
            attempt.RecordBackupIndeterminate(at.AddMinutes(1), "synthetic.unknown");
            task.RecordIndeterminateResult(snapshot.StorageMode, lease, at.AddMinutes(1), "synthetic.unknown", "合成未知");
        }
        else
        {
            attempt.RecordBackupSucceeded(at.AddMinutes(1));
            task.CompleteRunningStage(snapshot.StorageMode, lease, at.AddMinutes(1));
            attempt.RecordLocalVerification(1024, at.AddMinutes(2));
            task.CompleteRunningStage(snapshot.StorageMode, lease, at.AddMinutes(2));
            // SQL 成功、本地校验成功，但最终阶段失败；调度不能仅看最终 Failed。
            if (outcome == "remote_uncertain")
                task.RecordIndeterminateResult(snapshot.StorageMode, lease, at.AddMinutes(2), "synthetic.remote_unknown", "合成远端未知");
            else
                task.RecordConfirmedFailure(snapshot.StorageMode, lease, at.AddMinutes(2), "synthetic.remote_failed", "合成远端失败");
            if (outcome != "metadata_missing")
            {
                var metadata = BackupSetTestData.Full() with { ForkPointLsn = BackupMetadata.NotApplicable<BackupLsn>() };
                var set = new BackupSet(Guid.NewGuid(), taskId, id, snapshot.DatabaseId, metadata,
                    new(at.AddMinutes(1), BackupCompletionTimeSource.PlatformObserved, BackupCompletionTimeReason.PlatformObserved),
                    BackupSetAssessment.NotApplicable, null, true);
                set.Reconcile(metadata, set.Completion, set.Assessment, null, true);
                if (outcome == "source_conflict") set.MarkMetadataConflict();
                db.Add(set);
                foreach (var source in new[] { BackupSetEvidenceSource.Comparison, BackupSetEvidenceSource.BackupHeader, BackupSetEvidenceSource.Msdb })
                    db.Add(Evidence(set, source, metadata, BackupSetAssessment.NotApplicable));
            }
        }
        db.Add(attempt);
        await db.SaveChangesAsync();
    }
    private static BackupSetEvidence Evidence(BackupSet set, BackupSetEvidenceSource source, BackupSetMetadata metadata,
        BackupSetAssessment assessment, int round = 1) => new(Guid.NewGuid(), set.TaskId, set.AttemptId, set.Id, Guid.NewGuid(), round,
            (int)source, source, BackupSetEvidenceKind.Backup, metadata, set.Completion, assessment, Now, true);
    private async Task<RecordCounts> Counts(Guid planId)
    {
        await using var db = database.CreateContext();
        var ids = await db.BackupTasks.Where(x => x.PlanId == planId).Select(x => x.Id).ToArrayAsync();
        var names = ids.Select(x => x.ToString("N")).ToArray();
        return new(ids.Length, await db.BackupTaskSnapshots.CountAsync(x => ids.Contains(x.TaskId)),
            await db.BackupTaskStateChanges.CountAsync(x => ids.Contains(x.TaskId)), await db.AuditRecords.CountAsync(x => names.Contains(x.TargetId)),
            await db.TaskEvents.CountAsync(x => ids.Contains(x.TaskId)));
    }
    private sealed record RecordCounts(int Tasks, int Snapshots, int History, int Audit, int Events)
    {
        internal static RecordCounts Empty { get; } = new(0, 0, 0, 0, 0);
    }
    private sealed class FrozenTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private static void AssertDisposed(TestFactory factory) => Assert.All(factory.Contexts,
        context => Assert.Throws<ObjectDisposedException>(() => context.ChangeTracker.Entries().ToArray()));
    private sealed class TestFactory(PlatformDatabaseSqlServerFixture fixture, params IInterceptor[] interceptors) : IDbContextFactory<PlatformDbContext>
    {
        internal List<PlatformDbContext> Contexts { get; } = [];
        public PlatformDbContext CreateDbContext() { var db = fixture.CreateContext(interceptors); Contexts.Add(db); return db; }
        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class ReadSignal(string text) : DbCommandInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { if (command.CommandText.Contains(text, StringComparison.Ordinal)) Entered.TrySetResult(); return ValueTask.FromResult(result); }
    }
    private sealed class PlatformReadFailure : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            command.CommandText.Contains("FROM [ManagedDatabases]", StringComparison.Ordinal)
                ? throw new InvalidOperationException("合成平台查询失败。") : ValueTask.FromResult(result);
    }
    private sealed class SaveGate : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return result; }
    }
    private sealed class SaveSignal : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); return ValueTask.FromResult(result); }
    }
    private sealed class QueryGate(string text) : DbCommandInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(text, StringComparison.Ordinal)) { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }
    private sealed class SecondSaveFailure : SaveChangesInterceptor
    {
        private int calls;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ++calls == 2 ? throw new IOException("合成第二次保存失败。") : ValueTask.FromResult(result);
    }
    private sealed class CommitFailure(bool lost) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default) => lost ? ValueTask.FromResult(result) : throw new IOException("合成提交失败。");
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            lost ? throw new IOException("合成响应丢失。") : Task.CompletedTask;
    }
    private sealed class CancelCommit(CancellationTokenSource cancellation) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        { cancellation.Cancel(); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(result); }
    }
}
