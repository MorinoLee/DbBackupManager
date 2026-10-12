using DbBackupManager.Application.BackupSets;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.FileStorage;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class PlanLocationRetentionSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM [BackupFileStateChanges];
            DELETE FROM [BackupFiles];
            DELETE FROM [BackupSetEvidence];
            UPDATE [BackupSets] SET [BaseBackupSetId] = NULL;
            DELETE FROM [BackupSets];
            """);
        await database.ClearBackupTaskDataAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task LocalAndRemoteWindowsStayIndependentAndLegacyRetentionSkipsPlanCopies()
    {
        var chain = await SeedChainAsync();
        var now = DateTimeOffset.UtcNow;
        var store = new PlanLocationRetentionStore(Factory());
        var assessments = await store.AssessAsync(chain.DatabaseId, now);
        var local = assessments.Single(item => item.Location == BackupFileLocation.Local);
        var remote = assessments.Single(item => item.Location == BackupFileLocation.Remote);
        Assert.Equal(LocationRecoveryConclusion.RestorableWindowSatisfied, local.Conclusion);
        Assert.Equal(LocationRecoveryConclusion.RestorableWindowSatisfied, remote.Conclusion);
        Assert.Equal(RecoveryProtectionReason.OutsideWindowUnneeded, Reason(local, chain.LocalFileId(40)));
        Assert.Equal(RecoveryProtectionReason.WindowStartAnchor, Reason(local, chain.LocalFileId(10)));
        Assert.Equal(RecoveryProtectionReason.LatestAvailableFull, Reason(local, chain.LocalFileId(1)));
        Assert.Equal(RecoveryProtectionReason.WindowStartAnchor, Reason(remote, chain.RemoteFileId(40)));
        Assert.Equal(BackupTaskStoreResultCode.NotFound, (await new BackupFileRetentionStore(Factory()).ClaimNextAsync(Claim(now))).Code);

        var claimed = await store.ClaimNextAsync(chain.DatabaseId, Claim(now));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, claimed.Code);
        Assert.Equal(chain.LocalFileId(40), claimed.Value!.FileId);
    }

    [Fact]
    public async Task StaleLeaseCannotCommitDelete()
    {
        var chain = await SeedChainAsync();
        var now = DateTimeOffset.UtcNow;
        var store = new PlanLocationRetentionStore(Factory());
        var claimed = await store.ClaimNextAsync(chain.DatabaseId, Claim(now) with { ExpiresAtUtc = now.AddMinutes(1) });
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, claimed.Code);
        var committed = await store.CommitAsync(
            claimed.Value!,
            new(Guid.NewGuid(), BackupFileRetentionOutcome.Deleted, now.AddMinutes(2)));
        Assert.Equal(BackupTaskStoreResultCode.LeaseLost, committed.Code);
        await using var db = database.CreateContext();
        var file = await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == chain.LocalFileId(40));
        Assert.Equal(BackupFileStatus.DeletePending, file.Status);
    }

    [Fact]
    public async Task ExplicitDeleteRemovesOnlyTheUnneededCopy()
    {
        var chain = await SeedChainAsync();
        var now = DateTimeOffset.UtcNow;
        var store = new PlanLocationRetentionStore(Factory());
        var claimed = await store.ClaimNextAsync(chain.DatabaseId, Claim(now));
        var protocol = new DeletingProtocol();
        var adapter = new BackupFileStorageAdapter(
            [protocol],
            new BackupArtifactDeletionGuard(Factory(), TimeProvider.System),
            new PlanCopyDeletionCoordinator(Factory(), TimeProvider.System));
        var deleted = await adapter.DeleteAsync(DeleteRequest(claimed.Value!));
        Assert.Equal(BackupFileStorageOutcome.Succeeded, deleted.Outcome);
        Assert.Equal(1, protocol.Deletes);
        await using var db = database.CreateContext();
        Assert.Equal(BackupFileStatus.Deleted, (await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == chain.LocalFileId(40))).Status);

        var kept = await db.BackupFiles.SingleAsync(item => item.Id == chain.LocalFileId(1));
        var token = Guid.NewGuid();
        kept.ClaimDeletion(token, "合成受保护领取", now, now.AddMinutes(5));
        await db.SaveChangesAsync();
        var refused = await adapter.DeleteAsync(new(
            claimed.Value!.Endpoint, kept.Path, kept.LengthBytes, null, 30,
            new(kept.TaskId, null, kept.Id, BackupArtifactPathRole.RegisteredFile, token, kept.RowVersion)));
        Assert.Equal(BackupFileStorageOutcome.ConfirmedFailed, refused.Outcome);
        Assert.Equal(1, protocol.Deletes);
        Assert.Equal(BackupFileStatus.Available, (await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == kept.Id)).Status);
    }

    [Fact]
    public async Task RegisteringADifferentialProtectsTheBaseline()
    {
        var chain = await SeedChainAsync();
        var store = new PlanLocationRetentionStore(Factory());
        var diff = await SeedRunningDifferentialAsync(chain, 40);
        await FinishAsync(diff.TaskId, diff.LeaseToken, BackupStorageMode.LocalAndRemote);
        var local = (await store.AssessAsync(chain.DatabaseId, DateTimeOffset.UtcNow)).Single(item => item.Location == BackupFileLocation.Local);
        Assert.Equal(RecoveryProtectionReason.DifferentialBase, Reason(local, chain.LocalFileId(40)));
        Assert.Equal(BackupTaskStoreResultCode.NotFound, (await store.ClaimNextAsync(chain.DatabaseId, Claim(DateTimeOffset.UtcNow))).Code);
    }

    [Fact]
    public async Task UnexpiredLeaseReturnsToAvailableWhenAnotherRunningBackupProtectsIt()
    {
        var chain = await SeedChainAsync();
        var now = DateTimeOffset.UtcNow;
        var store = new PlanLocationRetentionStore(Factory());
        var claimed = await store.ClaimNextAsync(chain.DatabaseId, Claim(now));
        Assert.Equal(chain.LocalFileId(40), claimed.Value!.FileId);
        await RegisterIndependentFullAsync(chain);
        await using (var db = database.CreateContext())
        {
            var file = await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == chain.LocalFileId(40));
            Assert.Equal(BackupFileStatus.Available, file.Status);
            Assert.Equal(0, file.DeletionAttemptCount);
            Assert.Null(file.DeletionLeaseToken);
        }

        Assert.Equal(BackupTaskStoreResultCode.StateMismatch, (await store.RefreshAsync(claimed.Value.FileId, claimed.Value.LeaseToken, now)).Code);
    }

    [Fact]
    public async Task UnknownCompletionAndRunningTaskAreNeverClaimed()
    {
        var unknown = await SeedChainAsync(completeNewest: true, completionKnown: false, ages: [40]);
        var store = new PlanLocationRetentionStore(Factory());
        var now = DateTimeOffset.UtcNow;
        var unknownLocal = (await store.AssessAsync(unknown.DatabaseId, now)).Single(item => item.Location == BackupFileLocation.Local);
        Assert.Equal(RecoveryProtectionReason.Uncertain, Reason(unknownLocal, unknown.LocalFileId(40)));
        Assert.Equal(BackupTaskStoreResultCode.NotFound, (await store.ClaimNextAsync(unknown.DatabaseId, Claim(now))).Code);

        var running = await SeedChainAsync(completeNewest: false);
        var runningLocal = (await store.AssessAsync(running.DatabaseId, now)).Single(item => item.Location == BackupFileLocation.Local);
        Assert.Equal(RecoveryProtectionReason.ActiveTaskDependency, Reason(runningLocal, running.LocalFileId(40)));
        Assert.Equal(BackupTaskStoreResultCode.NotFound, (await store.ClaimNextAsync(running.DatabaseId, Claim(now))).Code);
    }

    [Fact]
    public async Task RemoteOnlyLocalStagingIsNotDeleted()
    {
        var chain = await SeedChainAsync(storageMode: BackupStorageMode.RemoteOnly);
        var now = DateTimeOffset.UtcNow;
        var store = new PlanLocationRetentionStore(Factory());
        var assessments = await store.AssessAsync(chain.DatabaseId, now);
        var local = assessments.Single(item => item.Location == BackupFileLocation.Local);
        Assert.Equal(PlanLocationRecovery.RemoteStagingReleasable, Reason(local, chain.LocalFileId(40)));
        Assert.True(PlanLocationRecovery.IsStagingReleasable(local.Copies.Single(copy => copy.FileId == chain.LocalFileId(40))));
        Assert.False(PlanLocationRecovery.IsDeletable(local.Copies.Single(copy => copy.FileId == chain.LocalFileId(40))));
        var claimed = await store.ClaimNextAsync(chain.DatabaseId, Claim(now));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, claimed.Code);
        Assert.Equal(chain.RemoteFileId(40), claimed.Value!.FileId);
        Assert.Equal(BackupFileLocation.Remote, claimed.Value.Location);
        await using var db = database.CreateContext();
        Assert.Equal(BackupFileStatus.Available, (await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == chain.LocalFileId(40))).Status);
    }

    [Fact]
    public async Task RemoteDifferentialWithoutBaseStaysVisibleAndStagingIsNotClaimed()
    {
        var chain = await SeedChainAsync(
            storageMode: BackupStorageMode.RemoteOnly,
            ages: [2],
            mode: BackupPlanMode.FullAndDifferential,
            registerRemote: false);
        var diff = await SeedRunningDifferentialAsync(chain, 2);
        await AddLocalStagingAsync(diff, diff.ServerId);
        await RegisterCopyAsync(diff, local: false, 7);
        await FinishAsync(diff.TaskId, diff.LeaseToken, BackupStorageMode.RemoteOnly);
        var now = DateTimeOffset.UtcNow;
        var store = new PlanLocationRetentionStore(Factory());
        var assessments = await store.AssessAsync(chain.DatabaseId, now);
        var remote = assessments.Single(item => item.Location == BackupFileLocation.Remote);
        var local = assessments.Single(item => item.Location == BackupFileLocation.Local);
        Assert.Equal(LocationRecoveryConclusion.NotRestorable, remote.Conclusion);
        Assert.Equal(RecoveryProtectionReason.BaseUnavailableAtLocation, remote.Copies.Single().Reason);
        Assert.Equal(RecoveryProtectionReason.BaselineUnresolved, Reason(local, chain.LocalFileId(2)));
        Assert.Contains(local.Copies, copy => copy.Reason == PlanLocationRecovery.RemoteBaseUnconfirmed);
        Assert.All(local.Copies, copy => Assert.False(PlanLocationRecovery.IsStagingReleasable(copy)));
        Assert.Equal(BackupTaskStoreResultCode.NotFound, (await store.ClaimStagingAsync(chain.DatabaseId, Claim(now))).Code);
        await using var db = database.CreateContext();
        Assert.Equal(2, await db.BackupFiles.CountAsync(file => file.DatabaseId == chain.DatabaseId && file.Location == BackupFileLocation.Local && file.Status == BackupFileStatus.Available));
    }

    [Fact]
    public async Task ExplicitStagingCleanupDeletesOnlyTheConfirmedLocalCopy()
    {
        var chain = await SeedChainAsync(storageMode: BackupStorageMode.RemoteOnly, ages: [40]);
        var now = DateTimeOffset.UtcNow;
        var store = new PlanLocationRetentionStore(Factory());
        var claimed = await store.ClaimStagingAsync(chain.DatabaseId, Claim(now));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, claimed.Code);
        Assert.Equal(chain.LocalFileId(40), claimed.Value!.FileId);
        var later = now.AddMinutes(10);
        var stale = await store.CommitAsync(claimed.Value, new(Guid.NewGuid(), BackupFileRetentionOutcome.Deleted, later));
        Assert.Equal(BackupTaskStoreResultCode.LeaseLost, stale.Code);

        var fresh = await store.ClaimStagingAsync(chain.DatabaseId, Claim(later));
        var protocol = new DeletingProtocol();
        var adapter = new BackupFileStorageAdapter(
            [protocol],
            new BackupArtifactDeletionGuard(Factory(), TimeProvider.System),
            new PlanCopyDeletionCoordinator(Factory(), TimeProvider.System));
        var deleted = await adapter.DeleteAsync(DeleteRequest(fresh.Value!));
        Assert.Equal(BackupFileStorageOutcome.Succeeded, deleted.Outcome);
        Assert.Equal(1, protocol.Deletes);
        await using var db = database.CreateContext();
        Assert.Equal(BackupFileStatus.Deleted, (await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == chain.LocalFileId(40))).Status);
        Assert.Equal(BackupFileStatus.Available, (await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == chain.RemoteFileId(40))).Status);
        Assert.Equal(BackupTaskStoreResultCode.NotFound, (await store.ClaimNextAsync(chain.DatabaseId, Claim(now))).Code);
    }

    [Fact]
    public async Task AnotherOpenPlanTaskBlocksStagingRelease()
    {
        var chain = await SeedChainAsync(storageMode: BackupStorageMode.RemoteOnly, ages: [40]);
        await StartOpenPlanTaskAsync(chain);
        var now = DateTimeOffset.UtcNow;
        var store = new PlanLocationRetentionStore(Factory());
        var local = (await store.AssessAsync(chain.DatabaseId, now)).Single(item => item.Location == BackupFileLocation.Local);
        Assert.Equal(RecoveryProtectionReason.ActiveTaskDependency, Reason(local, chain.LocalFileId(40)));
        Assert.Equal(BackupTaskStoreResultCode.NotFound, (await store.ClaimStagingAsync(chain.DatabaseId, Claim(now))).Code);
    }

    [Fact]
    public async Task ExplicitStagingClaimSkipsARunningOwnersFileButTheOwnerCanClaimIt()
    {
        var chain = await SeedChainAsync(storageMode: BackupStorageMode.RemoteOnly, ages: [40], completeNewest: false);
        var now = DateTimeOffset.UtcNow;
        var store = new PlanLocationRetentionStore(Factory());
        await using var db = database.CreateContext();
        var file = await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == chain.LocalFileId(40));
        var task = await db.BackupTasks.AsNoTracking().SingleAsync(item => item.Id == file.TaskId);
        Assert.Equal(BackupTaskStatus.Running, task.Status);
        Assert.True(task.LeaseExpiresAtUtc > now);
        var decision = (await store.AssessAsync(chain.DatabaseId, now))
            .Single(item => item.Location == BackupFileLocation.Local)
            .Copies.Single(copy => copy.FileId == file.Id);
        Assert.True(PlanLocationRecovery.IsStagingReleasable(decision));
        Assert.Equal(BackupTaskStoreResultCode.NotFound, (await store.ClaimStagingAsync(chain.DatabaseId, Claim(now))).Code);
        var own = await store.ClaimStagingAsync(chain.DatabaseId, Claim(now), file.TaskId);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, own.Code);
        Assert.Equal(file.Id, own.Value!.FileId);
    }

    [Fact]
    public async Task MissingStagingDoesNotRecordCleanupEvidence()
    {
        var chain = await SeedChainAsync(storageMode: BackupStorageMode.RemoteOnly, ages: [40]);
        var now = DateTimeOffset.UtcNow;
        var store = new PlanLocationRetentionStore(Factory());
        var claimed = await store.ClaimStagingAsync(chain.DatabaseId, Claim(now));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, claimed.Code);
        var missing = await store.CommitAsync(
            claimed.Value!, new(Guid.NewGuid(), BackupFileRetentionOutcome.Missing, now.AddMinutes(1)));
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, missing.Code);
        await using var db = database.CreateContext();
        var file = await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == chain.LocalFileId(40));
        Assert.Equal(BackupFileStatus.Missing, file.Status);
        Assert.Null((await db.BackupAttempts.AsNoTracking().SingleAsync(item => item.Id == file.AttemptId)).LocalCleanupCompletedAtUtc);
    }

    [Fact]
    public async Task ConfirmedUnlinkIsRecordedAfterTheLeaseExpires()
    {
        var chain = await SeedChainAsync();
        var now = DateTimeOffset.UtcNow;
        var store = new PlanLocationRetentionStore(Factory());
        var claimed = await store.ClaimNextAsync(chain.DatabaseId, Claim(now) with { ExpiresAtUtc = now.AddMinutes(5) });
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, claimed.Code);
        var protocol = new DeletingProtocol();
        var adapter = new BackupFileStorageAdapter(
            [protocol],
            new BackupArtifactDeletionGuard(Factory(), TimeProvider.System),
            new PlanCopyDeletionCoordinator(Factory(), new SteppingClock(now)));
        var deleted = await adapter.DeleteAsync(DeleteRequest(claimed.Value!));

        Assert.Equal(BackupFileStorageOutcome.Succeeded, deleted.Outcome);
        Assert.Equal(1, protocol.Deletes);
        await using var db = database.CreateContext();
        var file = await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == chain.LocalFileId(40));
        Assert.Equal(BackupFileStatus.Deleted, file.Status);
        Assert.Equal(now.AddMinutes(10), file.DeletedAtUtc);
        Assert.Null(file.DeletionLeaseToken);
    }

    [Fact]
    public async Task DeletePendingBaseRejectsDifferentialAndMissingProbeDoesNotRestoreAvailable()
    {
        var chain = await SeedChainAsync();
        var now = DateTimeOffset.UtcNow;
        var store = new PlanLocationRetentionStore(Factory());
        var claimed = await store.ClaimNextAsync(chain.DatabaseId, Claim(now) with { ExpiresAtUtc = now.AddMinutes(1) });
        Assert.Equal(chain.LocalFileId(40), claimed.Value!.FileId);
        var diff = await SeedRunningDifferentialAsync(chain, 40, registerSet: false);
        Assert.Equal(BackupTaskStoreResultCode.StateMismatch, (await BackupSetTestData.RegisterAsync(database, diff.Command)).Code);
        await using (var db = database.CreateContext())
        {
            var pending = await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == chain.LocalFileId(40));
            Assert.Equal(BackupFileStatus.DeletePending, pending.Status);
            Assert.Equal(claimed.Value.LeaseToken, pending.DeletionLeaseToken);
        }

        await StartOpenPlanTaskAsync(chain);
        var later = now.AddMinutes(2);
        Assert.Equal(BackupTaskStoreResultCode.NotFound, (await store.ClaimNextAsync(chain.DatabaseId, Claim(later))).Code);
        await using (var db = database.CreateContext())
        {
            var pending = await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == chain.LocalFileId(40));
            Assert.Equal(BackupFileStatus.DeletePending, pending.Status);
            Assert.Equal(claimed.Value.LeaseToken, pending.DeletionLeaseToken);
        }

        var expired = await store.ReadExpiredProtectedAsync(chain.DatabaseId, later);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, expired.Code);
        Assert.Equal(chain.LocalFileId(40), expired.Value!.FileId);
        var resolved = await store.ResolveExpiredAsync(expired.Value.FileId, expired.Value.LeaseToken, stillPresent: false, later);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, resolved.Code);
        await using (var db = database.CreateContext())
        {
            var missing = await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == chain.LocalFileId(40));
            Assert.Equal(BackupFileStatus.Missing, missing.Status);
            Assert.Null(missing.DeletionLeaseToken);
            Assert.True(missing.DeletionAttemptCount >= 1);
        }
    }

    [Fact]
    public async Task ExpiredProtectedCopyReturnsToAvailableWhenTheFileRemains()
    {
        var chain = await SeedChainAsync();
        var now = DateTimeOffset.UtcNow;
        var store = new PlanLocationRetentionStore(Factory());
        var claimed = await store.ClaimNextAsync(chain.DatabaseId, Claim(now) with { ExpiresAtUtc = now.AddMinutes(1) });
        await StartOpenPlanTaskAsync(chain);
        var later = now.AddMinutes(2);
        var expired = await store.ReadExpiredProtectedAsync(chain.DatabaseId, later);
        Assert.Equal(chain.LocalFileId(40), expired.Value!.FileId);
        var resolved = await store.ResolveExpiredAsync(expired.Value.FileId, expired.Value.LeaseToken, stillPresent: true, later);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, resolved.Code);
        await using var db = database.CreateContext();
        var file = await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == chain.LocalFileId(40));
        Assert.Equal(BackupFileStatus.Available, file.Status);
        Assert.Equal(0, file.DeletionAttemptCount);
        Assert.Null(file.DeletionLeaseToken);
    }

    [Fact]
    public async Task LegacyBackupSetLeaseIsLeftUntouched()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var command = BackupSetTestData.Command(work);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, (await BackupSetTestData.RegisterAsync(database, command)).Code);
        var now = DateTimeOffset.UtcNow;
        var token = Guid.NewGuid();
        Guid fileId;
        Guid databaseId;
        await using (var db = database.CreateContext())
        {
            var snapshot = await db.BackupTaskSnapshots.SingleAsync(item => item.TaskId == work.Task.TaskId);
            Assert.NotEqual("v3", snapshot.FileNameRuleVersion);
            databaseId = snapshot.DatabaseId;
            var file = BackupFile.CreateLocal(
                Guid.NewGuid(), work.Task.TaskId, work.Lease.BackupAttemptId, snapshot.DatabaseId, snapshot.ServerId,
                FileTransferProtocol.Smb, work.Attempt.WorkerSourceFilePath, 4096, now, snapshot.LocalRetentionDays ?? 7);
            file.AssociateBackupSet(command.BackupSetId);
            file.ClaimDeletion(token, "旧策略删除", now, now.AddMinutes(10));
            db.Add(file);
            await db.SaveChangesAsync();
            fileId = file.Id;
            var stored = await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == fileId);
            var coordinator = new PlanCopyDeletionCoordinator(Factory(), TimeProvider.System);
            await using var permit = await coordinator.OpenAsync(new(
                new(stored.Protocol, "synthetic-host", null, "synthetic-share", snapshot.SourceCredentialReferenceId, null),
                stored.Path, stored.LengthBytes, null, 30,
                new(stored.TaskId, null, stored.Id, BackupArtifactPathRole.RegisteredFile, token, stored.RowVersion)));
            Assert.False(permit.Applies);
        }

        var store = new PlanLocationRetentionStore(Factory());
        Assert.Equal(BackupTaskStoreResultCode.NotFound, (await store.ClaimNextAsync(databaseId, Claim(now))).Code);
        Assert.Equal(BackupTaskStoreResultCode.NotFound, (await store.ReadExpiredProtectedAsync(databaseId, now.AddHours(1))).Code);
        Assert.Equal(
            BackupTaskStoreResultCode.StateMismatch,
            (await store.ResolveExpiredAsync(fileId, token, stillPresent: false, now.AddHours(1))).Code);
        await using (var db = database.CreateContext())
        {
            var file = await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == fileId);
            Assert.Equal(BackupFileStatus.DeletePending, file.Status);
            Assert.Equal(token, file.DeletionLeaseToken);
        }
    }

    [Fact]
    public async Task GuardStillRunsAfterAPlanPermitAllowsDeletion()
    {
        var chain = await SeedChainAsync();
        var now = DateTimeOffset.UtcNow;
        var store = new PlanLocationRetentionStore(Factory());
        var claimed = await store.ClaimNextAsync(chain.DatabaseId, Claim(now));
        var protocol = new DeletingProtocol();
        var blocked = new BackupFileStorageAdapter(
            [protocol],
            new FixedGuard(false, "artifact.owner_unknown"),
            new PlanCopyDeletionCoordinator(Factory(), TimeProvider.System));
        var refused = await blocked.DeleteAsync(DeleteRequest(claimed.Value!));
        Assert.Equal(BackupFileStorageOutcome.ConfirmedFailed, refused.Outcome);
        Assert.Equal(0, protocol.Deletes);
        await using (var db = database.CreateContext())
        {
            Assert.Equal(BackupFileStatus.DeletePending, (await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == chain.LocalFileId(40))).Status);
        }

        var allowed = new BackupFileStorageAdapter(
            [protocol],
            new FixedGuard(false, "plan.copy.protected"),
            new PlanCopyDeletionCoordinator(Factory(), TimeProvider.System));
        var deleted = await allowed.DeleteAsync(DeleteRequest(claimed.Value!));
        Assert.Equal(BackupFileStorageOutcome.Succeeded, deleted.Outcome);
        Assert.Equal(1, protocol.Deletes);
        await using (var db = database.CreateContext())
        {
            Assert.Equal(BackupFileStatus.Deleted, (await db.BackupFiles.AsNoTracking().SingleAsync(item => item.Id == chain.LocalFileId(40))).Status);
        }
    }

    private BackupExecutionContractSqlServerTests.Factory Factory() => new(database);

    private static ClaimBackupFileDeletionCommand Claim(DateTimeOffset now) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "plan-retention", now, now.AddMinutes(5));

    private static string Reason(PlanLocationAssessment assessment, Guid fileId) =>
        assessment.Copies.Single(copy => copy.FileId == fileId).Reason;

    private static BackupFileDeleteRequest DeleteRequest(BackupFileRetentionWorkItem work) => new(
        work.Endpoint, work.Path, work.LengthBytes, null, 30,
        new(work.TaskId, null, work.FileId, BackupArtifactPathRole.RegisteredFile, work.LeaseToken, work.RowVersion));

    private async Task<Chain> SeedChainAsync(
        bool completeNewest = true,
        bool completionKnown = true,
        BackupStorageMode storageMode = BackupStorageMode.LocalAndRemote,
        int[]? ages = null,
        BackupPlanMode? mode = null,
        bool registerRemote = true)
    {
        ages ??= [40, 10, 1];
        var legacy = await BackupSetTestData.WorkAsync(database);
        await using var db = database.CreateContext();
        var old = await db.BackupTaskSnapshots.SingleAsync(item => item.TaskId == legacy.Task.TaskId);
        var now = DateTimeOffset.UtcNow;
        var remoteOnly = storageMode == BackupStorageMode.RemoteOnly;
        var target = new StorageTarget(Guid.NewGuid(), $"合成目标-{Guid.NewGuid():N}",
            new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", old.SourceCredentialReferenceId, null));
        db.Add(target);
        await db.SaveChangesAsync();
        var localWindow = remoteOnly ? (int?)null : 7;
        var remoteWindow = remoteOnly ? 7 : 30;
        var planMode = mode ?? (remoteOnly ? BackupPlanMode.Full : BackupPlanMode.FullAndDifferential);
        var definition = new BackupPlanDefinition(planMode,
            new(BackupScheduleType.Daily, new(2, 0), BackupWeekdays.None),
            planMode == BackupPlanMode.Full
                ? null
                : new RecurringBackupSchedule(BackupScheduleType.Daily, new(3, 0), BackupWeekdays.None),
            null, "UTC", storageMode, target.Id, localWindow, remoteWindow, true, false, 120, 60, 60);
        var plan = BackupPlan.Create(Guid.NewGuid(), old.DatabaseId, $"合成保留计划-{Guid.NewGuid():N}", Guid.NewGuid(), definition, now);
        var version = plan.CurrentVersion;
        await new BackupPlanStore(Factory()).AddAsync(plan);
        var chain = new Chain(old.DatabaseId, target.Id);
        var newest = ages.Min();
        foreach (var age in ages)
        {
            var observed = now.AddMinutes(50 - age);
            var completion = completionKnown
                ? new BackupCompletionTimeDecision(now.AddDays(-age), BackupCompletionTimeSource.PlatformObserved, BackupCompletionTimeReason.PlatformObserved)
                : new BackupCompletionTimeDecision(null, BackupCompletionTimeSource.Unknown, BackupCompletionTimeReason.SqlFinishMissing);
            var includeBaseline = !remoteOnly && completionKnown && age == newest;
            var created = await CreateBackupAsync(old, version, target.Id, BackupRunPurpose.PlanFull, now, observed, completion, includeBaseline, BackupSetTestData.Full(), null, null);
            chain.Remember(age, created);
            if (remoteOnly)
            {
                await AddLocalStagingAsync(created, old.ServerId);
            }
            else
            {
                await RegisterCopyAsync(created, local: true, localWindow!.Value);
            }

            if (registerRemote)
            {
                await RegisterCopyAsync(created, local: false, remoteWindow);
            }
            if (completeNewest || age != newest)
            {
                await FinishAsync(created.TaskId, created.LeaseToken, storageMode);
            }
        }

        return chain;
    }

    private Task<CreatedBackup> SeedRunningDifferentialAsync(Chain chain, int age, bool registerSet = true) =>
        SeedRunningDifferentialAsyncCore(chain, age, registerSet);

    private async Task<CreatedBackup> SeedRunningDifferentialAsyncCore(Chain chain, int age, bool registerSet)
    {
        await using var db = database.CreateContext();
        var old = await db.BackupTaskSnapshots.AsNoTracking().FirstAsync(item => item.DatabaseId == chain.DatabaseId && item.Purpose != null);
        var versionId = await db.BackupPlans.AsNoTracking()
            .Where(plan => plan.DatabaseId == chain.DatabaseId)
            .Select(plan => EF.Property<Guid?>(plan, "_currentVersionId"))
            .SingleAsync() ?? throw new InvalidOperationException("计划没有当前版本。");
        var version = await db.BackupPlanVersions.SingleAsync(item => item.Id == versionId);
        var full = chain.Metadata(age);
        var facts = full with
        {
            BackupSetGuid = BackupMetadata.Known(Guid.NewGuid()),
            Type = BackupMetadata.Known(BackupType.Differential),
            DatabaseBackupLsn = full.CheckpointLsn,
            DifferentialBaseLsn = full.CheckpointLsn,
            DifferentialBaseGuid = full.BackupSetGuid
        };
        var identity = new BackupDatabaseIdentity(facts.DatabaseGuid.Value, facts.FamilyGuid.Value);
        var branch = new BackupRecoveryBranch(facts.FirstRecoveryForkId.Value, facts.RecoveryForkId.Value);
        var decision = DifferentialBaselineRules.EvaluateDependency(identity,
            new(new(identity, branch, facts.DifferentialBaseGuid.Value, facts.DifferentialBaseLsn.Value),
                BackupType.Differential, false, facts.DatabaseBackupLsn.Value),
            [new(chain.SetId(age), new(full.BackupSetGuid.Value, identity, branch, BackupType.Full, false, full.CheckpointLsn.Value))]);
        Assert.Equal(DifferentialBaselineConclusion.Verified, decision.Conclusion);
        var now = DateTimeOffset.UtcNow;
        var created = await CreateBackupAsync(old, version, chain.TargetId, BackupRunPurpose.PlanDifferential, now, now,
            new(now.AddDays(-1), BackupCompletionTimeSource.PlatformObserved, BackupCompletionTimeReason.PlatformObserved),
            false, facts, BackupSetAssessment.FromDecision(decision), decision.ManagedFullId, registerSet);
        return created;
    }

    private async Task<CreatedBackup> CreateBackupAsync(
        BackupTaskSnapshot old,
        BackupPlanVersion version,
        Guid targetId,
        BackupRunPurpose purpose,
        DateTimeOffset now,
        DateTimeOffset observedAt,
        BackupCompletionTimeDecision completion,
        bool includeBaseline,
        BackupSetMetadata metadata,
        BackupSetAssessment? assessment,
        Guid? baseBackupSetId,
        bool registerSet = true)
    {
        await using var db = database.CreateContext();
        var task = BackupTask.ForPlan(Guid.NewGuid(), version.PlanId, version.Id, purpose, BackupTaskTriggerType.Manual, null);
        var snapshot = BackupTaskSnapshot.ForPlan(task, version, "合成保留计划", purpose,
            new(old.ServerId, old.ServerName, old.InstanceId, old.InstanceName, old.DatabaseId, old.DatabaseName),
            new(old.ConnectionAddress, old.SqlCredentialReferenceId, old.EncryptConnection, old.TrustServerCertificate,
                old.CertificateTrustReason, old.ConnectionTimeoutSeconds, old.AllowLegacyTls, old.LegacyTlsReason),
            new(old.LocalSqlBackupRootPath, "v3", new(old.SourceAccessProtocol, old.SourceAccessHost, old.SourceAccessPort,
                old.SourceAccessBasePath, old.SourceCredentialReferenceId, old.SourceSftpHostKeyFingerprint)),
            new(targetId, new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", old.SourceCredentialReferenceId, null)));
        var attemptId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var attempt = new BackupAttempt(attemptId, task.Id, 1, now, new(
            $@"D:\Synthetic\Backup\{attemptId:N}.bak",
            $@"\\synthetic-host\synthetic-share\{attemptId:N}.bak",
            targetId,
            $@"\\synthetic-host\synthetic-share\{attemptId:N}.bak.part",
            $@"\\synthetic-host\synthetic-share\{attemptId:N}-remote.bak"));
        db.AddRange(task, snapshot);
        await db.SaveChangesAsync();
        attempt.BindSqlIdentity(Guid.NewGuid(), Guid.NewGuid());
        task.ClaimExecution(version.StorageMode, attemptId, leaseToken, "合成保留领取", now, now.AddMinutes(30));
        db.Add(attempt);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var stored = await db.BackupTasks.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        var observations = includeBaseline
            ? new BackupSetObservation[]
            {
                new(BackupSetEvidenceSource.Msdb, BackupSetEvidenceKind.ActiveBaseline, BackupSetTestData.Full(),
                    completion, new(BackupMetadataState.Known, DifferentialBaselineConclusion.Unmanaged, DifferentialBaselineReason.ExternalFullObserved))
            }
            : [];
        var command = new RegisterBackupSetCommand(stored.ToLeaseHandle(), Guid.NewGuid(), old.DatabaseId, Guid.NewGuid(), observedAt,
            metadata, completion, assessment ?? BackupSetAssessment.NotApplicable, baseBackupSetId, true, observations);
        if (registerSet)
        {
            var registered = await BackupSetTestData.RegisterAsync(database, command);
            Assert.Equal(BackupTaskStoreResultCode.Succeeded, registered.Code);
        }
        var attemptStored = await db.BackupAttempts.AsNoTracking().SingleAsync(item => item.Id == attemptId);
        return new(task.Id, attemptId, leaseToken, old.DatabaseId, old.ServerId, targetId, command, attemptStored.WorkerSourceFilePath, attemptStored.RemoteFinalFilePath!, metadata);
    }

    private async Task RegisterCopyAsync(CreatedBackup created, bool local, int retentionDays)
    {
        var file = local
            ? BackupFile.CreateLocal(Guid.NewGuid(), created.TaskId, created.AttemptId, created.DatabaseId, created.ServerId,
                FileTransferProtocol.Smb, created.LocalPath, 4096, DateTimeOffset.UtcNow, retentionDays)
            : BackupFile.CreateRemote(Guid.NewGuid(), created.TaskId, created.AttemptId, created.DatabaseId, created.TargetId,
                FileTransferProtocol.Smb, created.RemotePath, 4096, DateTimeOffset.UtcNow, retentionDays);
        file.AssociateBackupSet(created.Command.BackupSetId);
        await using var db = database.CreateContext();
        var task = await db.BackupTasks.AsNoTracking().SingleAsync(item => item.Id == created.TaskId);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IBackupSetRegistrationStore>().RegisterCopyAsync(task.ToLeaseHandle(), file);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, result.Code);
        if (local) created.RememberLocal(file.Id);
        else created.RememberRemote(file.Id);
    }

    private async Task AddLocalStagingAsync(CreatedBackup created, Guid serverId)
    {
        await using var db = database.CreateContext();
        var file = BackupFile.CreateLocal(Guid.NewGuid(), created.TaskId, created.AttemptId, created.DatabaseId, serverId,
            FileTransferProtocol.Smb, created.LocalPath, 4096, DateTimeOffset.UtcNow, 7);
        file.AssociateBackupSet(created.Command.BackupSetId);
        db.Add(file);
        await db.SaveChangesAsync();
        created.RememberLocal(file.Id);
    }

    private async Task FinishAsync(Guid taskId, Guid leaseToken, BackupStorageMode mode)
    {
        await using var db = database.CreateContext();
        var task = await db.BackupTasks.SingleAsync(item => item.Id == taskId);
        var guard = 0;
        var now = DateTimeOffset.UtcNow;
        while (task.Status != BackupTaskStatus.Succeeded)
        {
            task.CompleteRunningStage(mode, leaseToken, now);
            if (++guard > 6)
            {
                throw new InvalidOperationException("计划任务阶段没有结束。");
            }
        }

        await db.SaveChangesAsync();
    }

    private sealed class Chain(Guid databaseId, Guid targetId)
    {
        private readonly Dictionary<int, Guid> _sets = [];
        private readonly Dictionary<int, Guid> _local = [];
        private readonly Dictionary<int, Guid> _remote = [];
        private readonly Dictionary<int, BackupSetMetadata> _metadata = [];

        public Guid DatabaseId { get; } = databaseId;
        public Guid TargetId { get; } = targetId;
        public void Remember(int age, CreatedBackup created)
        {
            _sets[age] = created.Command.BackupSetId;
            _metadata[age] = created.Metadata;
            created.Bind(this, age);
        }

        public void RememberLocal(int age, Guid fileId) => _local[age] = fileId;
        public void RememberRemote(int age, Guid fileId) => _remote[age] = fileId;
        public Guid SetId(int age) => _sets[age];
        public Guid LocalFileId(int age) => _local[age];
        public Guid RemoteFileId(int age) => _remote[age];
        public BackupSetMetadata Metadata(int age) => _metadata[age];
    }

    private sealed class CreatedBackup(
        Guid taskId, Guid attemptId, Guid leaseToken, Guid databaseId, Guid serverId, Guid targetId, RegisterBackupSetCommand command,
        string localPath, string remotePath, BackupSetMetadata metadata)
    {
        private Chain? _chain;
        private int _age;
        public Guid TaskId { get; } = taskId;
        public Guid AttemptId { get; } = attemptId;
        public Guid LeaseToken { get; } = leaseToken;
        public Guid DatabaseId { get; } = databaseId;
        public Guid ServerId { get; } = serverId;
        public Guid TargetId { get; } = targetId;
        public RegisterBackupSetCommand Command { get; } = command;
        public string LocalPath { get; } = localPath;
        public string RemotePath { get; } = remotePath;
        public BackupSetMetadata Metadata { get; } = metadata;
        public void Bind(Chain chain, int age) { _chain = chain; _age = age; }
        public void RememberLocal(Guid fileId) => _chain?.RememberLocal(_age, fileId);
        public void RememberRemote(Guid fileId) => _chain?.RememberRemote(_age, fileId);
    }

    private async Task RegisterIndependentFullAsync(Chain chain)
    {
        await using var db = database.CreateContext();
        var old = await db.BackupTaskSnapshots.AsNoTracking()
            .FirstAsync(item => item.DatabaseId == chain.DatabaseId && item.Purpose != null);
        var versionId = await db.BackupPlans.AsNoTracking()
            .Where(plan => plan.DatabaseId == chain.DatabaseId)
            .Select(plan => EF.Property<Guid?>(plan, "_currentVersionId"))
            .SingleAsync() ?? throw new InvalidOperationException("计划没有当前版本。");
        var version = await db.BackupPlanVersions.SingleAsync(item => item.Id == versionId);
        var now = DateTimeOffset.UtcNow;
        await CreateBackupAsync(old, version, chain.TargetId, BackupRunPurpose.PlanFull, now, now,
            new(now.AddDays(-1), BackupCompletionTimeSource.PlatformObserved, BackupCompletionTimeReason.PlatformObserved),
            false, BackupSetTestData.Full(), null, null);
    }

    private async Task StartOpenPlanTaskAsync(Chain chain)
    {
        await using var db = database.CreateContext();
        var old = await db.BackupTaskSnapshots.AsNoTracking()
            .FirstAsync(item => item.DatabaseId == chain.DatabaseId && item.Purpose != null);
        var versionId = await db.BackupPlans.AsNoTracking()
            .Where(plan => plan.DatabaseId == chain.DatabaseId)
            .Select(plan => EF.Property<Guid?>(plan, "_currentVersionId"))
            .SingleAsync() ?? throw new InvalidOperationException("计划没有当前版本。");
        var version = await db.BackupPlanVersions.SingleAsync(item => item.Id == versionId);
        var now = DateTimeOffset.UtcNow;
        var task = BackupTask.ForPlan(Guid.NewGuid(), version.PlanId, version.Id, BackupRunPurpose.PlanFull, BackupTaskTriggerType.Manual, null);
        var snapshot = BackupTaskSnapshot.ForPlan(task, version, "合成保留计划", BackupRunPurpose.PlanFull,
            new(old.ServerId, old.ServerName, old.InstanceId, old.InstanceName, old.DatabaseId, old.DatabaseName),
            new(old.ConnectionAddress, old.SqlCredentialReferenceId, old.EncryptConnection, old.TrustServerCertificate,
                old.CertificateTrustReason, old.ConnectionTimeoutSeconds, old.AllowLegacyTls, old.LegacyTlsReason),
            new(old.LocalSqlBackupRootPath, "v3", new(old.SourceAccessProtocol, old.SourceAccessHost, old.SourceAccessPort,
                old.SourceAccessBasePath, old.SourceCredentialReferenceId, old.SourceSftpHostKeyFingerprint)),
            new(chain.TargetId, new(FileTransferProtocol.Smb, "synthetic-host", null, "synthetic-share", old.SourceCredentialReferenceId, null)));
        var attemptId = Guid.NewGuid();
        var attempt = new BackupAttempt(attemptId, task.Id, 1, now, new(
            $@"D:\Synthetic\Backup\{attemptId:N}.bak",
            $@"\\synthetic-host\synthetic-share\{attemptId:N}.bak",
            chain.TargetId,
            $@"\\synthetic-host\synthetic-share\{attemptId:N}.bak.part",
            $@"\\synthetic-host\synthetic-share\{attemptId:N}-remote.bak"));
        db.AddRange(task, snapshot);
        await db.SaveChangesAsync();
        attempt.BindSqlIdentity(Guid.NewGuid(), Guid.NewGuid());
        task.ClaimExecution(version.StorageMode, attemptId, Guid.NewGuid(), "合成活动任务", now, now.AddHours(1));
        db.Add(attempt);
        await db.SaveChangesAsync();
    }

    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        private int _calls;

        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref _calls) == 1 ? start : start.AddMinutes(10);
    }

    private sealed class FixedGuard(bool allowed, string? reason) : IBackupArtifactDeletionGuard
    {
        public Task<BackupArtifactDeletionDecision> EvaluateAsync(
            BackupFileDeleteRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupArtifactDeletionDecision(allowed, reason));
    }

    private sealed class DeletingProtocol : IFileStorageProtocolSessionFactory
    {
        private readonly HashSet<string> _deleted = new(StringComparer.OrdinalIgnoreCase);
        public int Deletes { get; private set; }
        public FileTransferProtocol Protocol => FileTransferProtocol.Smb;
        public ValueTask<IFileStorageProtocolSession> OpenAsync(BackupFileEndpointInput endpoint, CancellationToken cancellationToken) =>
            new(new Session(this));

        private sealed class Session(DeletingProtocol owner) : IFileStorageProtocolSession
        {
            public ValueTask PrepareParentDirectoryAsync(string filePath, CancellationToken cancellationToken) => ValueTask.CompletedTask;
            public ValueTask<BackupFileMetadata> InspectAsync(string path, CancellationToken cancellationToken) =>
                new(owner._deleted.Contains(path)
                    ? new BackupFileMetadata(false, false, null)
                    : new BackupFileMetadata(true, true, 4096, null));
            public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
            public ValueTask<Stream> CreateNewAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
            public ValueTask RenameNoReplaceAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken) => throw new NotSupportedException();
            public ValueTask DeleteAsync(string path, CancellationToken cancellationToken)
            {
                owner.Deletes++;
                owner._deleted.Add(path);
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
