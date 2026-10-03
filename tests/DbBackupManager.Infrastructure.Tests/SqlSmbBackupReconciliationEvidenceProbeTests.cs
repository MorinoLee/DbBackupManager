using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.BackupExecution;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class SqlSmbBackupReconciliationEvidenceProbeTests
{
    [Fact]
    public async Task StableMatchingBackupOnlyAdvancesBackupStage()
    {
        var adapters = new FakeReadOnlyAdapters(
            TargetSqlBackupIdentityStatus.CompletedMatching,
            new(true, true, 4096),
            new(true, true, 4096));
        var evidence = await CreateProbe(adapters).InspectAsync(CreateWorkItem(BackupTaskStage.Backup));

        Assert.Equal(BackupReconciliationConclusion.Succeeded, evidence.Conclusion);
        Assert.Equal(BackupArtifactObservation.PresentStable, evidence.Artifact);
        Assert.Equal(TargetBackupObservation.CompletedMatching, evidence.TargetBackup);
        Assert.Equal(BackupVerificationObservation.NotAttempted, evidence.Verification);
        Assert.Null(evidence.SourceLengthBytes);
        Assert.Equal(0, adapters.VerificationCount);
    }

    [Fact]
    public async Task ChangingFileNeverAllowsSuccess()
    {
        var adapters = new FakeReadOnlyAdapters(
            TargetSqlBackupIdentityStatus.CompletedMatching,
            new(true, true, 2048),
            new(true, true, 4096));
        var evidence = await CreateProbe(adapters).InspectAsync(CreateWorkItem(BackupTaskStage.Backup));

        Assert.Equal(BackupReconciliationConclusion.Inconclusive, evidence.Conclusion);
        Assert.Equal(BackupArtifactObservation.PresentChanging, evidence.Artifact);
        Assert.Equal("reconciliation.file_changing", evidence.ReasonCode);
    }

    [Fact]
    public async Task VerifyStageRequiresIdentityIntegrityAndPositiveStableLength()
    {
        var adapters = new FakeReadOnlyAdapters(
            TargetSqlBackupIdentityStatus.CompletedMatching,
            new(true, true, 4096),
            new(true, true, 4096));
        var evidence = await CreateProbe(adapters).InspectAsync(CreateWorkItem(BackupTaskStage.VerifyLocal));

        Assert.Equal(BackupReconciliationConclusion.Succeeded, evidence.Conclusion);
        Assert.Equal(BackupVerificationObservation.Succeeded, evidence.Verification);
        Assert.Equal(4096, evidence.SourceLengthBytes);
        Assert.Equal(1, adapters.VerificationCount);
    }

    [Fact]
    public async Task StableIdentityMismatchIsConfirmedFailureWithoutVerify()
    {
        var adapters = new FakeReadOnlyAdapters(
            TargetSqlBackupIdentityStatus.IdentityMismatch,
            new(true, true, 4096),
            new(true, true, 4096));
        var evidence = await CreateProbe(adapters).InspectAsync(CreateWorkItem(BackupTaskStage.VerifyLocal));

        Assert.Equal(BackupReconciliationConclusion.ConfirmedFailed, evidence.Conclusion);
        Assert.Equal(TargetBackupObservation.IdentityMismatch, evidence.TargetBackup);
        Assert.Equal("reconciliation.backup_identity_mismatch", evidence.ReasonCode);
        Assert.Equal(0, adapters.VerificationCount);
    }

    [Fact]
    public async Task VerifyPermissionFailureDoesNotBecomeOriginalBackupFailure()
    {
        var adapters = new FakeReadOnlyAdapters(
            TargetSqlBackupIdentityStatus.CompletedMatching,
            new(true, true, 4096),
            new(true, true, 4096))
        {
            VerificationResult = TargetSqlResult.ConfirmedFailure<TargetSqlBackupVerification>(
                TargetSqlFailureCode.AuthorizationDenied,
                TargetSqlFailurePhase.BackupVerification),
        };
        var evidence = await CreateProbe(adapters).InspectAsync(CreateWorkItem(BackupTaskStage.VerifyLocal));

        Assert.Equal(BackupReconciliationConclusion.Inconclusive, evidence.Conclusion);
        Assert.Equal(BackupVerificationObservation.Indeterminate, evidence.Verification);
        Assert.Equal("reconciliation.verify_indeterminate", evidence.ReasonCode);
    }

    [Fact]
    public async Task MissingFileStillChecksSqlButCannotAuthorizeRetry()
    {
        var adapters = new FakeReadOnlyAdapters(
            TargetSqlBackupIdentityStatus.NotFound,
            new BackupFileProbeResult(true, false, null));
        var evidence = await CreateProbe(adapters).InspectAsync(CreateWorkItem(BackupTaskStage.Backup));

        Assert.Equal(BackupReconciliationConclusion.Inconclusive, evidence.Conclusion);
        Assert.Equal(BackupArtifactObservation.Missing, evidence.Artifact);
        Assert.Equal(TargetBackupObservation.NotFound, evidence.TargetBackup);
        Assert.Equal("reconciliation.file_missing", evidence.ReasonCode);
        Assert.Equal(1, adapters.IdentityInspectionCount);
    }

    [Fact]
    public async Task CompletePartialWithoutFinalAdvancesTransfer()
    {
        var adapters = new FakeReadOnlyAdapters(TargetSqlBackupIdentityStatus.CompletedMatching);
        var remote = new FakeRemoteFiles(4096, missingFinal: true);
        var evidence = await CreateRemoteProbe(adapters, remote)
            .InspectAsync(CreateRemoteWorkItem(BackupTaskStage.Transfer, 4096));

        Assert.Equal(BackupReconciliationConclusion.Succeeded, evidence.Conclusion);
        Assert.Equal("reconciliation.transfer_partial_complete", evidence.ReasonCode);
        Assert.Equal(4096, evidence.SourceLengthBytes);
        Assert.Equal(0, adapters.IdentityInspectionCount);
    }

    [Fact]
    public async Task PartialWithoutRenameStaysInconclusiveOnValidateCopy()
    {
        var adapters = new FakeReadOnlyAdapters(TargetSqlBackupIdentityStatus.CompletedMatching);
        var remote = new FakeRemoteFiles(4096, missingFinal: true);
        var evidence = await CreateRemoteProbe(adapters, remote)
            .InspectAsync(CreateRemoteWorkItem(BackupTaskStage.ValidateCopy, 4096));

        Assert.Equal(BackupReconciliationConclusion.Inconclusive, evidence.Conclusion);
        Assert.Equal("reconciliation.remote_copy_unconfirmed", evidence.ReasonCode);
    }

    [Fact]
    public async Task MissingSourceAllowsCleanupSuccess()
    {
        var adapters = new FakeReadOnlyAdapters(
            TargetSqlBackupIdentityStatus.CompletedMatching,
            new BackupFileProbeResult(true, false, null),
            new BackupFileProbeResult(true, false, null));
        var remote = new FakeRemoteFiles(4096, missingFinal: false);
        var evidence = await CreateRemoteProbe(adapters, remote)
            .InspectAsync(CreateRemoteWorkItem(BackupTaskStage.Cleanup, 4096));

        Assert.Equal(BackupReconciliationConclusion.Succeeded, evidence.Conclusion);
        Assert.Equal(BackupArtifactObservation.Missing, evidence.Artifact);
        Assert.Equal("reconciliation.cleanup_source_absent", evidence.ReasonCode);
    }

    private static SqlSmbBackupReconciliationEvidenceProbe CreateRemoteProbe(
        FakeReadOnlyAdapters adapters,
        FakeRemoteFiles remote)
    {
        return new SqlSmbBackupReconciliationEvidenceProbe(
            adapters,
            adapters,
            adapters,
            remote,
            TimeProvider.System,
            new BackupReconciliationOptions(
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                10));
    }

    private static BackupExecutionWorkItem CreateRemoteWorkItem(BackupTaskStage stage, long sourceLength)
    {
        var work = CreateWorkItem(stage);
        var targetId = Guid.NewGuid();
        const string fingerprint = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var remote = new BackupFileEndpointModel(
            FileTransferProtocol.Sftp,
            "synthetic-remote-host",
            22,
            "/synthetic/remote",
            Guid.NewGuid(),
            fingerprint);
        var snapshot = work.Snapshot with
        {
            Policy = work.Snapshot.Policy with
            {
                StorageMode = BackupStorageMode.LocalAndRemote,
                StorageTargetId = targetId,
                RemoteEndpoint = remote,
                RemoteRetentionDays = 30,
            },
        };
        var attempt = new BackupAttemptModel(
            work.Attempt.Id,
            1,
            BackupInvocationStatus.Succeeded,
            work.Attempt.LocalSqlFilePath,
            work.Attempt.WorkerSourceFilePath,
            targetId,
            "/synthetic/remote/synthetic.part",
            "/synthetic/remote/synthetic.bak",
            sourceLength,
            DateTimeOffset.UtcNow,
            null,
            null,
            [1]);
        return work with { Snapshot = snapshot, Attempt = attempt };
    }

    private sealed class FakeRemoteFiles(long length, bool missingFinal) : IBackupFileStorageProbe
    {
        public Task<BackupFileStorageResult<BackupFileMetadata>> InspectAsync(
            BackupFileEndpointInput endpoint,
            string path,
            CancellationToken cancellationToken = default)
        {
            var exists = !path.EndsWith(".bak", StringComparison.Ordinal) || !missingFinal;
            return Task.FromResult(exists
                ? BackupFileStorageResult.Succeeded(new BackupFileMetadata(true, true, length))
                : BackupFileStorageResult.Succeeded(new BackupFileMetadata(false, false, null)));
        }
    }

    private static SqlSmbBackupReconciliationEvidenceProbe CreateProbe(
        FakeReadOnlyAdapters adapters)
    {
        return new SqlSmbBackupReconciliationEvidenceProbe(
            adapters,
            adapters,
            adapters,
            adapters,
            TimeProvider.System,
            new BackupReconciliationOptions(
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                10));
    }

    private static BackupExecutionWorkItem CreateWorkItem(BackupTaskStage stage)
    {
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var sqlCredentialId = Guid.NewGuid();
        var smbCredentialId = Guid.NewGuid();
        var endpoint = new BackupFileEndpointModel(
            FileTransferProtocol.Smb,
            "synthetic-host",
            null,
            "synthetic-share",
            smbCredentialId,
            null);
        var snapshot = new BackupTaskSnapshotModel(
            taskId,
            "synthetic-policy",
            new BackupTaskIdentityModel(
                Guid.NewGuid(),
                "synthetic-server",
                Guid.NewGuid(),
                "synthetic-instance",
                Guid.NewGuid(),
                "synthetic-database"),
            new BackupSqlTargetModel(
                "synthetic-target",
                sqlCredentialId,
                true,
                false,
                null,
                15),
            @"D:\SyntheticRoot",
            "v1",
            endpoint,
            new BackupTaskPolicyModel(
                BackupStorageMode.LocalOnly,
                null,
                null,
                7,
                null,
                true,
                false,
                true,
                120,
                60,
                60,
                "UTC"));
        var attempt = new BackupAttemptModel(
            attemptId,
            1,
            BackupInvocationStatus.Indeterminate,
            $@"D:\SyntheticRoot\synthetic_{attemptId:N}.bak",
            $@"\\synthetic-host\synthetic-share\synthetic_{attemptId:N}.bak",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [1]);
        var lease = new LeaseHandle(
            taskId,
            Guid.NewGuid(),
            BackupLeasePurpose.Reconciliation,
            stage,
            attemptId,
            DateTimeOffset.UtcNow.AddMinutes(1),
            [1]);
        return new BackupExecutionWorkItem(
            new BackupTaskStateModel(
                taskId,
                BackupTaskStatus.NeedsAttention,
                stage,
                attemptId,
                null,
                0,
                "synthetic",
                "合成待核对状态。",
                DateTimeOffset.UtcNow.AddMinutes(-1),
                null),
            snapshot,
            attempt,
            lease);
    }

    private sealed class FakeReadOnlyAdapters :
        ITargetSqlBackupEvidenceProbe,
        ITargetSqlReadOnlyProbe,
        IBackupSourceProbe,
        IBackupFileStorageProbe
    {
        private readonly Queue<BackupFileProbeResult> _fileResults;

        public FakeReadOnlyAdapters(
            TargetSqlBackupIdentityStatus identityStatus,
            params BackupFileProbeResult[] fileResults)
        {
            IdentityStatus = identityStatus;
            _fileResults = new Queue<BackupFileProbeResult>(fileResults);
        }

        public TargetSqlBackupIdentityStatus IdentityStatus { get; }

        public int IdentityInspectionCount { get; private set; }

        public int VerificationCount { get; private set; }

        public TargetSqlResult<TargetSqlBackupVerification> VerificationResult { get; init; } =
            TargetSqlResult.Succeeded(new TargetSqlBackupVerification(true));

        public Task<BackupFileProbeResult> InspectAsync(
            BackupTaskSnapshotModel snapshot,
            BackupAttemptModel attempt,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_fileResults.Dequeue());
        }

        public Task<TargetSqlResult<TargetSqlBackupIdentity>> InspectBackupIdentityAsync(
            TargetSqlConnectionInput connection,
            TargetSqlBackupIdentityRequest request,
            CancellationToken cancellationToken = default)
        {
            IdentityInspectionCount++;
            return Task.FromResult(TargetSqlResult.Succeeded(
                new TargetSqlBackupIdentity(IdentityStatus)));
        }

        public Task<TargetSqlResult<TargetSqlBackupVerification>> VerifyBackupAsync(
            TargetSqlConnectionInput connection,
            TargetSqlBackupVerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            VerificationCount++;
            return Task.FromResult(VerificationResult);
        }

        public Task<TargetSqlResult<TargetSqlServerInfo>> ProbeServerAsync(
            TargetSqlConnectionInput connection,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TargetSqlResult<TargetSqlDatabaseCatalog>> DiscoverDatabasesAsync(
            TargetSqlConnectionInput connection,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BackupFileStorageResult<BackupFileMetadata>> InspectAsync(
            BackupFileEndpointInput endpoint,
            string path,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Backup/VerifyLocal 核对不应读取远程文件端口。");
    }
}
