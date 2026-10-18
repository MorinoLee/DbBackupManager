using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.Tests;

public sealed class PlanRemoteStagingCleanerTests
{
    [Fact]
    public async Task RetainedCleanupRecordsTheRemoteUnrecoverableReason()
    {
        var session = Session();
        var execution = new Execution();
        var retention = new Retention { Release = new(false, Guid.NewGuid(), PlanLocationRecovery.RemoteBaseUnconfirmed) };
        var cleaner = Cleaner(retention, execution);

        Assert.Equal(BackupExecutionContractCode.Succeeded, await cleaner.CleanOwnAsync(session));
        Assert.Equal((true, PlanLocationRecovery.RemoteBaseUnconfirmed), execution.Completed);
        Assert.Null(execution.SuspendReason);
        Assert.Equal(0, retention.Claims);
    }

    [Fact]
    public async Task AlreadyCleanedStagingFinishesInsteadOfSuspending()
    {
        var execution = new Execution { CompleteCode = BackupExecutionContractCode.Succeeded };
        var retention = new Retention { Release = new(false, null, PlanLocationRecovery.DependencyUnproven) };
        var cleaner = Cleaner(retention, execution);

        Assert.Equal(BackupExecutionContractCode.Succeeded, await cleaner.CleanOwnAsync(Session()));
        Assert.Equal((false, null), execution.Completed);
        Assert.Null(execution.SuspendReason);
    }

    [Fact]
    public async Task ClaimBlockedWhileTheFileIsAlreadyCleanedDoesNotStayNeedsAttention()
    {
        var execution = new Execution { CompleteCode = BackupExecutionContractCode.Succeeded };
        var retention = new Retention
        {
            Release = new(true, Guid.NewGuid(), PlanLocationRecovery.RemoteStagingReleasable),
            ClaimCode = BackupTaskStoreResultCode.DatabaseBlocked
        };
        var cleaner = Cleaner(retention, execution);

        Assert.Equal(BackupExecutionContractCode.Succeeded, await cleaner.CleanOwnAsync(Session()));
        Assert.Equal((false, null), execution.Completed);
        Assert.Null(execution.SuspendReason);
    }

    [Fact]
    public async Task DatabaseBlockedClaimSuspendsSoALaterEntryCanClose()
    {
        var execution = new Execution { CompleteCode = BackupExecutionContractCode.Conflict };
        var retention = new Retention
        {
            Release = new(true, Guid.NewGuid(), PlanLocationRecovery.RemoteStagingReleasable),
            ClaimCode = BackupTaskStoreResultCode.DatabaseBlocked
        };
        var cleaner = Cleaner(retention, execution);

        Assert.Equal(BackupExecutionContractCode.DatabaseBlocked, await cleaner.CleanOwnAsync(Session()));
        Assert.Equal(PlanCopyDeletionReasons.Busy, execution.SuspendReason);
    }

    [Fact]
    public async Task MissingStagingSuspendsForManualHandlingWithoutCleanupEvidence()
    {
        var execution = new Execution();
        var retention = new Retention { Release = new(true, Guid.NewGuid(), PlanLocationRecovery.RemoteStagingReleasable) };
        var cleaner = Cleaner(retention, execution, new Probe(false), new Deletion(BackupFileStorageOutcome.Succeeded));

        Assert.Equal(BackupExecutionContractCode.Succeeded, await cleaner.CleanOwnAsync(Session()));
        Assert.Equal(PlanCopyDeletionReasons.StagingMissing, execution.SuspendReason);
        Assert.Equal(BackupFileRetentionOutcome.Missing, retention.LastOutcome);
        Assert.Null(execution.Completed);
    }

    [Fact]
    public async Task CancellationDuringDeleteDoesNotSuspendAsAnUnexpectedError()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var execution = new Execution();
        var retention = new Retention { Release = new(true, Guid.NewGuid(), PlanLocationRecovery.RemoteStagingReleasable) };
        var deletion = new Deletion(BackupFileStorageOutcome.Succeeded) { ThrowCancelled = true };
        var cleaner = Cleaner(retention, execution, new Probe(true), deletion);

        Assert.Equal(BackupExecutionContractCode.CancellationRequested, await cleaner.CleanOwnAsync(Session(), cancelled.Token));
        Assert.Null(execution.SuspendReason);
        Assert.Null(execution.Completed);
        Assert.Equal(0, retention.Commits);
    }

    [Fact]
    public async Task IndeterminateDeleteCancellationIsNotRecordedAsFailure()
    {
        var execution = new Execution();
        var retention = new Retention { Release = new(true, Guid.NewGuid(), PlanLocationRecovery.RemoteStagingReleasable) };
        var deletion = new Deletion(BackupFileStorageOutcome.Indeterminate, BackupFileStorageFailureCode.Cancelled);
        var cleaner = Cleaner(retention, execution, new Probe(true), deletion);

        Assert.Equal(BackupExecutionContractCode.CancellationRequested, await cleaner.CleanOwnAsync(Session()));
        Assert.Null(execution.SuspendReason);
        Assert.Equal(0, retention.Commits);
    }

    [Fact]
    public async Task ExplicitCleanupSurfacesACloseFailure()
    {
        var execution = new Execution { CloseCode = BackupExecutionContractCode.Conflict };
        var retention = new Retention { ClaimCode = BackupTaskStoreResultCode.Succeeded };
        var cleaner = Cleaner(retention, execution, new Probe(true), new Deletion(BackupFileStorageOutcome.Succeeded));

        Assert.False(await cleaner.RunOnceAsync(Guid.NewGuid()));
        Assert.Equal(1, execution.ReadyCloses);
        Assert.Equal(1, execution.DeferredCloses);
    }

    private static PlanRemoteStagingCleaner Cleaner(
        Retention retention,
        Execution execution,
        Probe? probe = null,
        Deletion? deletion = null) =>
        new(retention, execution, probe ?? new Probe(true), deletion ?? new Deletion(BackupFileStorageOutcome.Succeeded), TimeProvider.System);

    private static BackupPlanExecutionSession Session()
    {
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var endpoint = new BackupFileEndpointModel(FileTransferProtocol.Smb, "localhost", null, @"C:\synthetic", Guid.NewGuid(), null);
        var policy = new BackupTaskPolicyModel(BackupStorageMode.RemoteOnly, null, null, null, 7, true, false, false, 30, 30, 30, "UTC");
        var snapshot = new BackupTaskSnapshotModel(taskId, "合成计划", new(Guid.NewGuid(), "server", Guid.NewGuid(), "instance", Guid.NewGuid(), "synthetic"),
            new("synthetic", Guid.NewGuid(), true, true, "合成", 30), @"C:\synthetic", "v3", endpoint, policy, BackupType.Full, BackupRunPurpose.PlanFull);
        var attempt = new BackupAttemptModel(attemptId, 1, BackupInvocationStatus.Succeeded, @"C:\synthetic\a.bak", @"C:\synthetic\a.bak",
            null, null, null, null, null, null, null, [1]);
        var lease = new LeaseHandle(taskId, Guid.NewGuid(), BackupLeasePurpose.Execution, BackupTaskStage.Cleanup, attemptId, now.AddMinutes(2), [1]);
        var task = new BackupTaskStateModel(taskId, BackupTaskStatus.Running, BackupTaskStage.Cleanup, attemptId, null, 0, null, null, now, null);
        return new(new(task, snapshot, attempt, lease), [], null);
    }

    private sealed class Retention : IPlanLocationRetentionStore
    {
        public PlanStagingRelease Release { get; init; } = new(true, Guid.NewGuid(), PlanLocationRecovery.RemoteStagingReleasable);
        public BackupTaskStoreResultCode ClaimCode { get; init; } = BackupTaskStoreResultCode.Succeeded;
        public int Claims { get; private set; }
        public int Commits { get; private set; }
        public BackupFileRetentionOutcome? LastOutcome { get; private set; }

        public Task<PlanStagingRelease> ReadOwnStagingAsync(Guid taskId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(Release);

        public Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ClaimStagingAsync(
            Guid databaseId, ClaimBackupFileDeletionCommand command, Guid? onlyTaskId = null, CancellationToken cancellationToken = default)
        {
            Claims++;
            return Task.FromResult(new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                ClaimCode, ClaimCode == BackupTaskStoreResultCode.Succeeded ? Work() : null));
        }

        public Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> RefreshAsync(
            Guid fileId, Guid leaseToken, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.Succeeded, Work()));

        public Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> CommitAsync(
            BackupFileRetentionWorkItem work, BackupFileRetentionCommitCommand command, CancellationToken cancellationToken = default)
        {
            Commits++;
            LastOutcome = command.Outcome;
            return Task.FromResult(new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.Succeeded, work));
        }

        public Task<IReadOnlyList<PlanLocationAssessment>> AssessAsync(Guid databaseId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlanLocationAssessment>>([]);
        public Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ClaimNextAsync(Guid databaseId, ClaimBackupFileDeletionCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.NotFound));
        public Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ReadExpiredProtectedAsync(Guid databaseId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.NotFound));
        public Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ResolveExpiredAsync(Guid fileId, Guid leaseToken, bool stillPresent, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.Succeeded));

        private static BackupFileRetentionWorkItem Work() => new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), BackupFileLocation.Local, null, Guid.NewGuid(),
            FileTransferProtocol.Smb, @"\\SYNTHETIC-HOST\SHARE\BACKUP.BAK", 4096, BackupFileStatus.DeletePending,
            Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2), [1, 2, 3],
            new(FileTransferProtocol.Smb, "synthetic-host", null, "share", Guid.NewGuid(), null));
    }

    private sealed class Execution : IBackupPlanExecutionStore
    {
        public BackupExecutionContractCode CompleteCode { get; init; } = BackupExecutionContractCode.Succeeded;
        public BackupExecutionContractCode CloseCode { get; init; } = BackupExecutionContractCode.Succeeded;
        public (bool Retained, string? Reason)? Completed { get; private set; }
        public string? SuspendReason { get; private set; }
        public int ReadyCloses { get; private set; }
        public int DeferredCloses { get; private set; }

        public Task<BackupExecutionContractCode> CompleteRemoteCleanupAsync(
            LeaseHandle lease, bool retained, string? outcomeReason = null, CancellationToken cancellationToken = default)
        {
            Completed = (retained, outcomeReason);
            return Task.FromResult(CompleteCode);
        }

        public Task<BackupExecutionContractCode> SuspendAsync(LeaseHandle lease, Guid mutationId, string reason, CancellationToken cancellationToken = default)
        {
            SuspendReason = reason;
            return Task.FromResult(BackupExecutionContractCode.Succeeded);
        }

        public Task<BackupExecutionContractCode> CloseDeferredRemoteCleanupAsync(Guid taskId, CancellationToken cancellationToken = default)
        {
            DeferredCloses++;
            return Task.FromResult(CloseCode);
        }

        public Task<BackupExecutionContractCode> CloseReadyRemoteCleanupsAsync(Guid databaseId, CancellationToken cancellationToken = default)
        {
            ReadyCloses++;
            return Task.FromResult(BackupExecutionContractCode.Succeeded);
        }

        public Task<BackupExecutionContractCode> ReleaseDeferredCleanupForExecutionAsync(LeaseHandle lease, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BackupExecutionContractCode> CloseUnknownAsync(LeaseHandle lease, Guid terminationMutationId, string reasonCode,
            DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BackupExecutionContractResult<BackupPlanExecutionSession>> OpenAsync(Guid taskId, Guid requestMutationId, bool reconciliationOnly, Guid leaseToken, string owner, DateTimeOffset now, DateTimeOffset expires, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupPlanExecutionSession?> RefreshAsync(Guid taskId, Guid leaseToken, CancellationToken cancellationToken = default) =>
            Task.FromResult<BackupPlanExecutionSession?>(null);
        public Task<BackupExecutionContractCode> RenewAsync(Guid taskId, Guid leaseToken, DateTimeOffset expires, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QualifiedManagedFull?> FindQualifiedFullAsync(Guid databaseId, Guid backupSetGuid, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BackupExecutionContractCode> ApplyAsync(LeaseHandle lease, ReserveBackupExecutionOperation identity, IVerifiedBackupFileHandle? protectedFile = null, IVerifiedBackupFileHandle? protectedDependency = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Probe(bool exists) : IBackupFileStorageProbe
    {
        public Task<BackupFileStorageResult<BackupFileMetadata>> InspectAsync(
            BackupFileEndpointInput endpoint, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(BackupFileStorageResult.Succeeded(exists
                ? new BackupFileMetadata(true, true, 4096, "synthetic-id")
                : new BackupFileMetadata(false, false, null)));
    }

    private sealed class Deletion(BackupFileStorageOutcome outcome, BackupFileStorageFailureCode code = BackupFileStorageFailureCode.OperationRejected) : IBackupFileDeletionExecutor
    {
        public bool ThrowCancelled { get; init; }

        public Task<BackupFileStorageResult<BackupFileMutationReceipt>> DeleteAsync(
            BackupFileDeleteRequest request, CancellationToken cancellationToken = default)
        {
            if (ThrowCancelled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return Task.FromResult(outcome switch
            {
                BackupFileStorageOutcome.Succeeded => BackupFileStorageResult.Succeeded(BackupFileMutationReceipt.Instance),
                BackupFileStorageOutcome.Indeterminate => BackupFileStorageResult.Indeterminate<BackupFileMutationReceipt>(code, BackupFileStorageFailurePhase.Delete),
                _ => BackupFileStorageResult.ConfirmedFailure<BackupFileMutationReceipt>(code, BackupFileStorageFailurePhase.Delete)
            });
        }
    }
}
