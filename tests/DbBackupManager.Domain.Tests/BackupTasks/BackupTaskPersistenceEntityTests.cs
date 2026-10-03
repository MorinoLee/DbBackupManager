using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Domain.Tests.BackupTasks;

public sealed class BackupTaskPersistenceEntityTests
{
    [Theory]
    [InlineData(BackupStorageMode.LocalOnly)]
    [InlineData(BackupStorageMode.LocalAndRemote)]
    [InlineData(BackupStorageMode.RemoteOnly)]
    public void PreparationFailureDoesNotInventAttemptOrExecutionEvidence(BackupStorageMode mode)
    {
        var now = DateTimeOffset.UtcNow;
        var task = CreateManualTask();
        task.RejectPendingBackup(mode, now, "backup_path_invalid", "备份路径无效");

        Assert.Equal(BackupTaskStatus.Failed, task.Status);
        Assert.Equal(BackupTaskStage.Backup, task.CurrentStage);
        Assert.Null(task.CurrentBackupAttemptId);
        Assert.Null(task.StartedAtUtc);
        Assert.Null(task.LeaseToken);
        Assert.Equal(now, task.CompletedAtUtc);
        Assert.Equal("backup_path_invalid", task.ErrorCode);
        Assert.Throws<InvalidOperationException>(() => task.RejectPendingBackup(mode, now, "invalid", "无效"));
    }

    [Fact]
    public void PreparationFailureCannotReplaceRunningOrUncertainResult()
    {
        var now = DateTimeOffset.UtcNow;
        var task = CreateManualTask();
        var lease = Guid.NewGuid();
        task.ClaimExecution(BackupStorageMode.LocalOnly, Guid.NewGuid(), lease, "worker", now, now.AddMinutes(1));
        Assert.Throws<InvalidOperationException>(() => task.RejectPendingBackup(
            BackupStorageMode.LocalOnly, now, "invalid", "无效"));
        task.RecordIndeterminateResult(BackupStorageMode.LocalOnly, lease, now, "unknown", "结果待核对");
        Assert.Throws<InvalidOperationException>(() => task.RejectPendingBackup(
            BackupStorageMode.LocalOnly, now, "invalid", "无效"));
        Assert.Equal(BackupTaskStatus.NeedsAttention, task.Status);
    }

    [Fact]
    public void RunningTaskCanCancelAtSafeBoundaryOnlyAfterRequest()
    {
        var now = DateTimeOffset.UtcNow;
        var task = CreateManualTask();
        var attemptId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        task.ClaimExecution(
            BackupStorageMode.LocalOnly,
            attemptId,
            leaseToken,
            "worker-a",
            now,
            now.AddMinutes(5));

        Assert.Throws<InvalidOperationException>(() => task.CancelRunningAtSafeBoundary(
            BackupStorageMode.LocalOnly,
            leaseToken,
            now.AddSeconds(1)));

        task.RequestCancellation(now.AddSeconds(1), BackupStorageMode.LocalOnly);
        task.CancelRunningAtSafeBoundary(
            BackupStorageMode.LocalOnly,
            leaseToken,
            now.AddSeconds(2));

        Assert.Equal(BackupTaskStatus.Cancelled, task.Status);
        Assert.Equal(now.AddSeconds(2), task.CompletedAtUtc);
        Assert.Null(task.LeaseToken);
    }

    [Theory]
    [InlineData(BackupReconciliationOutcome.Succeeded, BackupTaskStatus.Pending)]
    [InlineData(BackupReconciliationOutcome.SafeToRetry, BackupTaskStatus.Pending)]
    [InlineData(BackupReconciliationOutcome.Failed, BackupTaskStatus.Failed)]
    [InlineData(BackupReconciliationOutcome.Cancelled, BackupTaskStatus.Cancelled)]
    [InlineData(BackupReconciliationOutcome.Inconclusive, BackupTaskStatus.NeedsAttention)]
    public void ReconciliationLeaseCommitsStructuredOutcome(
        BackupReconciliationOutcome outcome,
        BackupTaskStatus expectedStatus)
    {
        var now = DateTimeOffset.UtcNow;
        var task = CreateManualTask();
        var executionToken = Guid.NewGuid();
        task.ClaimExecution(
            BackupStorageMode.LocalAndRemote,
            Guid.NewGuid(),
            executionToken,
            "worker-a",
            now,
            now.AddMinutes(1));
        task.RecordIndeterminateResult(
            BackupStorageMode.LocalAndRemote,
            executionToken,
            now.AddSeconds(1),
            "result_unknown",
            "合成的不确定结果");
        var reconciliationToken = Guid.NewGuid();
        task.AcquireReconciliationLease(
            reconciliationToken,
            "worker-r",
            now.AddSeconds(2),
            now.AddMinutes(2));

        task.CompleteReconciliation(
            BackupStorageMode.LocalAndRemote,
            reconciliationToken,
            now.AddSeconds(3),
            outcome,
            outcome is BackupReconciliationOutcome.Failed or BackupReconciliationOutcome.Inconclusive
                ? "reconciliation_evidence_insufficient"
                : null,
            outcome is BackupReconciliationOutcome.Failed or BackupReconciliationOutcome.Inconclusive
                ? "合成的核对结果"
                : null);

        Assert.Equal(expectedStatus, task.Status);
        Assert.Null(task.LeaseToken);
        if (outcome == BackupReconciliationOutcome.Inconclusive)
        {
            Assert.Equal("reconciliation_evidence_insufficient", task.ErrorCode);
            Assert.Null(task.CompletedAtUtc);
        }
    }

    [Theory]
    [InlineData(null, "只读证据不足，任务继续等待核对")]
    [InlineData("reconciliation_evidence_insufficient", null)]
    [InlineData("", "只读证据不足，任务继续等待核对")]
    [InlineData("reconciliation_evidence_insufficient", "")]
    public void InconclusiveReconciliationRejectsMissingErrorAndKeepsLease(
        string? errorCode,
        string? errorMessage)
    {
        var now = DateTimeOffset.UtcNow;
        var task = P57ReconciliationTaskFactory.CreateNeedsAttention(
            BackupStorageMode.LocalOnly,
            now,
            out _);
        var reconciliationToken = Guid.NewGuid();
        task.AcquireReconciliationLease(
            reconciliationToken,
            "worker-r",
            now.AddSeconds(2),
            now.AddMinutes(2));

        Assert.ThrowsAny<ArgumentException>(() => task.CompleteReconciliation(
            BackupStorageMode.LocalOnly,
            reconciliationToken,
            now.AddSeconds(3),
            BackupReconciliationOutcome.Inconclusive,
            errorCode,
            errorMessage));

        Assert.Equal(BackupTaskStatus.NeedsAttention, task.Status);
        Assert.Equal(reconciliationToken, task.LeaseToken);
        Assert.Equal(BackupLeasePurpose.Reconciliation, task.LeasePurpose);
    }

    [Fact]
    public void ReplacedAndForeignReconciliationTokensCannotCommit()
    {
        var now = DateTimeOffset.UtcNow;
        var task = P57ReconciliationTaskFactory.CreateNeedsAttention(
            BackupStorageMode.LocalOnly,
            now,
            out _);
        var firstToken = Guid.NewGuid();
        task.AcquireReconciliationLease(
            firstToken,
            "reconciler-a",
            now.AddSeconds(2),
            now.AddMinutes(2));
        var replacementToken = Guid.NewGuid();
        task.AcquireReconciliationLease(
            replacementToken,
            "reconciler-b",
            now.AddMinutes(2),
            now.AddMinutes(4));

        Assert.Throws<InvalidOperationException>(() => task.CompleteReconciliation(
            BackupStorageMode.LocalOnly,
            firstToken,
            now.AddMinutes(2).AddSeconds(1),
            BackupReconciliationOutcome.Inconclusive,
            "reconciliation_evidence_insufficient",
            "旧 Token 不得提交"));
        Assert.Throws<InvalidOperationException>(() => task.CompleteReconciliation(
            BackupStorageMode.LocalOnly,
            Guid.NewGuid(),
            now.AddMinutes(2).AddSeconds(1),
            BackupReconciliationOutcome.Inconclusive,
            "reconciliation_evidence_insufficient",
            "无关 Token 不得提交"));

        task.CompleteReconciliation(
            BackupStorageMode.LocalOnly,
            replacementToken,
            now.AddMinutes(2).AddSeconds(2),
            BackupReconciliationOutcome.Inconclusive,
            "reconciliation_evidence_insufficient",
            "当前核对租约可以提交证据不足");

        Assert.Equal(BackupTaskStatus.NeedsAttention, task.Status);
        Assert.Null(task.LeaseToken);
    }

    [Fact]
    public void ExpiredReconciliationLeaseCanBeReplacedButActiveLeaseCannot()
    {
        var now = DateTimeOffset.UtcNow;
        var task = CreateManualTask();
        var executionToken = Guid.NewGuid();
        task.ClaimExecution(
            BackupStorageMode.LocalOnly,
            Guid.NewGuid(),
            executionToken,
            "worker-a",
            now,
            now.AddMinutes(1));
        task.RecordIndeterminateResult(
            BackupStorageMode.LocalOnly,
            executionToken,
            now.AddSeconds(1),
            "result_unknown",
            "合成的不确定结果");
        task.AcquireReconciliationLease(
            Guid.NewGuid(),
            "reconciler-a",
            now.AddSeconds(2),
            now.AddMinutes(2));

        Assert.Throws<InvalidOperationException>(() => task.AcquireReconciliationLease(
            Guid.NewGuid(),
            "reconciler-b",
            now.AddMinutes(1),
            now.AddMinutes(3)));

        var replacementToken = Guid.NewGuid();
        task.AcquireReconciliationLease(
            replacementToken,
            "reconciler-b",
            now.AddMinutes(2),
            now.AddMinutes(4));

        Assert.Equal(replacementToken, task.LeaseToken);
        Assert.Equal("reconciler-b", task.LeaseOwner);
    }

    [Fact]
    public void IndeterminateAttemptCanBeReconciledToSucceeded()
    {
        var now = DateTimeOffset.UtcNow;
        var attemptId = Guid.NewGuid();
        var attempt = new BackupAttempt(
            attemptId,
            Guid.NewGuid(),
            1,
            now,
            new BackupAttemptPaths(
                $"synthetic-sql-root/{attemptId:N}.bak",
                $"synthetic-worker-root/{attemptId:N}.bak",
                null,
                null,
                null));
        attempt.MarkBackupRunning(now.AddSeconds(1));
        attempt.RecordBackupIndeterminate(null, "connection_lost");

        attempt.ReconcileBackupSucceeded(now.AddSeconds(2));

        Assert.Equal(BackupInvocationStatus.Succeeded, attempt.BackupInvocationStatus);
        Assert.Equal(now.AddSeconds(2), attempt.BackupFinishedAtUtc);
        Assert.Null(attempt.OutcomeCode);
    }

    [Fact]
    public void RunningAttemptCanBeReconciledAfterExecutionLeaseExpires()
    {
        var now = DateTimeOffset.UtcNow;
        var attemptId = Guid.NewGuid();
        var attempt = new BackupAttempt(
            attemptId,
            Guid.NewGuid(),
            1,
            now,
            new BackupAttemptPaths(
                $"synthetic-sql-root/{attemptId:N}.bak",
                $"synthetic-worker-root/{attemptId:N}.bak",
                null,
                null,
                null));
        attempt.MarkBackupRunning(now.AddSeconds(1));

        attempt.ReconcileBackupSucceeded(now.AddSeconds(2));

        Assert.Equal(BackupInvocationStatus.Succeeded, attempt.BackupInvocationStatus);
        Assert.Equal(now.AddSeconds(2), attempt.BackupFinishedAtUtc);
    }

    private static readonly DateTimeOffset BaselineUtc =
        new(2026, 9, 4, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TriggerTypeAndScheduledSlotMustMatch()
    {
        Assert.Throws<ArgumentException>(() =>
            new BackupTask(
                Guid.NewGuid(),
                Guid.NewGuid(),
                BackupTaskTriggerType.Scheduled,
                null));
        Assert.Throws<ArgumentException>(() =>
            new BackupTask(
                Guid.NewGuid(),
                Guid.NewGuid(),
                BackupTaskTriggerType.Manual,
                BaselineUtc));
        Assert.Throws<ArgumentException>(() =>
            new BackupTask(
                Guid.NewGuid(),
                Guid.NewGuid(),
                BackupTaskTriggerType.Scheduled,
                BaselineUtc.ToOffset(TimeSpan.FromHours(8))));
    }

    [Fact]
    public void ClaimCreatesExecutionLeaseAndRequiresNewBackupAttempt()
    {
        var task = CreateManualTask();
        var attemptId = Guid.NewGuid();
        var token = Guid.NewGuid();

        task.ClaimExecution(
            BackupStorageMode.RemoteOnly,
            attemptId,
            token,
            "synthetic-worker",
            BaselineUtc,
            BaselineUtc.AddMinutes(5));

        Assert.Equal(BackupTaskStatus.Running, task.Status);
        Assert.Equal(BackupTaskStage.Backup, task.CurrentStage);
        Assert.Equal(attemptId, task.CurrentBackupAttemptId);
        Assert.Equal(BackupLeasePurpose.Execution, task.LeasePurpose);
        Assert.Equal(token, task.LeaseToken);
        Assert.Equal(BaselineUtc, task.StartedAtUtc);
        Assert.Throws<InvalidOperationException>(() =>
            task.CompleteRunningStage(
                BackupStorageMode.RemoteOnly,
                Guid.NewGuid(),
                BaselineUtc.AddMinutes(1)));
    }

    [Fact]
    public void LeaseRenewalRejectsExpiredOrNonIncreasingExpiry()
    {
        var task = CreateManualTask();
        var token = Guid.NewGuid();
        task.ClaimExecution(
            BackupStorageMode.LocalOnly,
            Guid.NewGuid(),
            token,
            "synthetic-worker",
            BaselineUtc,
            BaselineUtc.AddMinutes(5));

        Assert.Throws<ArgumentException>(() =>
            task.RenewLease(
                token,
                BackupLeasePurpose.Execution,
                BaselineUtc.AddMinutes(1),
                BaselineUtc.AddMinutes(4)));
        Assert.Throws<InvalidOperationException>(() =>
            task.RenewLease(
                token,
                BackupLeasePurpose.Execution,
                BaselineUtc.AddMinutes(5),
                BaselineUtc.AddMinutes(10)));

        task.RenewLease(
            token,
            BackupLeasePurpose.Execution,
            BaselineUtc.AddMinutes(1),
            BaselineUtc.AddMinutes(10));

        Assert.Equal(BaselineUtc.AddMinutes(10), task.LeaseExpiresAtUtc);
    }

    [Fact]
    public void IndeterminateResultClearsExecutionLeaseAndAllowsReconciliationLease()
    {
        var task = CreateManualTask();
        var executionToken = Guid.NewGuid();
        task.ClaimExecution(
            BackupStorageMode.LocalOnly,
            Guid.NewGuid(),
            executionToken,
            "synthetic-worker",
            BaselineUtc,
            BaselineUtc.AddMinutes(5));

        task.RecordIndeterminateResult(
            BackupStorageMode.LocalOnly,
            executionToken,
            BaselineUtc.AddMinutes(1),
            "backup_result_unknown",
            "备份结果需要核对");

        Assert.Equal(BackupTaskStatus.NeedsAttention, task.Status);
        Assert.Null(task.LeaseToken);
        Assert.Null(task.CompletedAtUtc);

        var reconciliationToken = Guid.NewGuid();
        task.AcquireReconciliationLease(
            reconciliationToken,
            "synthetic-reconciler",
            BaselineUtc.AddMinutes(2),
            BaselineUtc.AddMinutes(7));

        Assert.Equal(BackupLeasePurpose.Reconciliation, task.LeasePurpose);
        Assert.Equal(reconciliationToken, task.LeaseToken);
    }

    [Fact]
    public void InconclusiveReconciliationUsesPersistentBackoffAndSuccessClearsSchedule()
    {
        var task = CreateManualTask();
        var executionToken = Guid.NewGuid();
        task.ClaimExecution(
            BackupStorageMode.LocalOnly,
            Guid.NewGuid(),
            executionToken,
            "synthetic-worker",
            BaselineUtc,
            BaselineUtc.AddMinutes(5));
        task.RecordIndeterminateResult(
            BackupStorageMode.LocalOnly,
            executionToken,
            BaselineUtc.AddMinutes(1),
            "backup_result_unknown",
            "备份结果需要核对");

        Assert.Equal(0, task.ReconciliationAttemptCount);
        Assert.Equal(BaselineUtc.AddMinutes(1), task.NextReconciliationAtUtc);
        Assert.Throws<InvalidOperationException>(() => task.AcquireReconciliationLease(
            Guid.NewGuid(),
            "too-early",
            BaselineUtc.AddMinutes(1).AddTicks(-1),
            BaselineUtc.AddMinutes(2)));

        var expectedDelays = new[]
        {
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1),
        };
        var eligibleAt = task.NextReconciliationAtUtc!.Value;
        for (var index = 0; index < expectedDelays.Length; index++)
        {
            var token = Guid.NewGuid();
            task.AcquireReconciliationLease(
                token,
                $"reconciler-{index}",
                eligibleAt,
                eligibleAt.AddMinutes(2));
            var committedAt = eligibleAt.AddSeconds(1);
            task.CompleteReconciliation(
                BackupStorageMode.LocalOnly,
                token,
                committedAt,
                BackupReconciliationOutcome.Inconclusive,
                "evidence_missing",
                "只读证据不足");

            Assert.Equal(index + 1, task.ReconciliationAttemptCount);
            Assert.Equal(committedAt + expectedDelays[index], task.NextReconciliationAtUtc);
            eligibleAt = task.NextReconciliationAtUtc!.Value;
        }

        var successToken = Guid.NewGuid();
        task.AcquireReconciliationLease(
            successToken,
            "reconciler-success",
            eligibleAt,
            eligibleAt.AddMinutes(2));
        task.CompleteReconciliation(
            BackupStorageMode.LocalOnly,
            successToken,
            eligibleAt.AddSeconds(1),
            BackupReconciliationOutcome.Succeeded);

        Assert.Equal(BackupTaskStatus.Pending, task.Status);
        Assert.Equal(BackupTaskStage.VerifyLocal, task.CurrentStage);
        Assert.Equal(0, task.ReconciliationAttemptCount);
        Assert.Null(task.NextReconciliationAtUtc);
    }

    [Fact]
    public void AdminReconciliationRequestOnlyAdvancesScheduleAndPreservesEvidence()
    {
        var now = BaselineUtc;
        var task = P57ReconciliationTaskFactory.CreateNeedsAttention(
            BackupStorageMode.LocalOnly,
            now,
            out _);
        var firstLease = Guid.NewGuid();
        task.AcquireReconciliationLease(
            firstLease,
            "worker-first",
            now.AddSeconds(2),
            now.AddMinutes(1));
        task.CompleteReconciliation(
            BackupStorageMode.LocalOnly,
            firstLease,
            now.AddSeconds(3),
            BackupReconciliationOutcome.Inconclusive,
            "reconciliation.file_missing",
            "未发现稳定文件证据");
        var requestedAt = now.AddSeconds(10);

        task.RequestReconciliation(requestedAt);

        Assert.Equal(BackupTaskStatus.NeedsAttention, task.Status);
        Assert.Equal(BackupTaskStage.Backup, task.CurrentStage);
        Assert.Equal(1, task.ReconciliationAttemptCount);
        Assert.Equal(requestedAt, task.NextReconciliationAtUtc);
        Assert.Equal("reconciliation.file_missing", task.ErrorCode);
        Assert.Equal("未发现稳定文件证据", task.ErrorMessage);
        Assert.NotNull(task.CurrentBackupAttemptId);
        Assert.Null(task.LeaseToken);
    }

    [Fact]
    public void AdminReconciliationRequestRejectsOtherStatesAndActiveLease()
    {
        var now = BaselineUtc;
        var pending = new BackupTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            BackupTaskTriggerType.Manual,
            null);
        var attention = P57ReconciliationTaskFactory.CreateNeedsAttention(
            BackupStorageMode.LocalOnly,
            now,
            out _);
        attention.AcquireReconciliationLease(
            Guid.NewGuid(),
            "worker-active",
            now.AddSeconds(2),
            now.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() => pending.RequestReconciliation(now));
        Assert.Throws<InvalidOperationException>(() => attention.RequestReconciliation(now.AddSeconds(3)));
        Assert.Throws<ArgumentException>(() => attention.RequestReconciliation(now.ToOffset(TimeSpan.FromHours(8))));
    }

    [Fact]
    public void AdminReconciliationRequestAllowsExpiredLeaseWithoutReplacingCapability()
    {
        var now = BaselineUtc;
        var task = P57ReconciliationTaskFactory.CreateNeedsAttention(
            BackupStorageMode.LocalOnly,
            now,
            out _);
        var leaseToken = Guid.NewGuid();
        task.AcquireReconciliationLease(
            leaseToken,
            "worker-expired",
            now.AddSeconds(2),
            now.AddSeconds(3));

        task.RequestReconciliation(now.AddSeconds(4));

        Assert.Equal(now.AddSeconds(4), task.NextReconciliationAtUtc);
        Assert.Equal(leaseToken, task.LeaseToken);
        Assert.Equal(BackupLeasePurpose.Reconciliation, task.LeasePurpose);
        Assert.Equal(now.AddSeconds(3), task.LeaseExpiresAtUtc);
    }

    [Fact]
    public void AdminCanConfirmFailedWithoutReconciliationLease()
    {
        var now = BaselineUtc;
        var task = P57ReconciliationTaskFactory.CreateNeedsAttention(
            BackupStorageMode.LocalOnly,
            now,
            out _);

        task.ConfirmFailed(
            BackupStorageMode.LocalOnly,
            now.AddSeconds(4),
            "admin.confirmed_failed",
            "维护者确认中断备份未产生有效副本");

        Assert.Equal(BackupTaskStatus.Failed, task.Status);
        Assert.Equal(BackupTaskStage.Backup, task.CurrentStage);
        Assert.Equal("admin.confirmed_failed", task.ErrorCode);
        Assert.Equal(now.AddSeconds(4), task.CompletedAtUtc);
        Assert.Null(task.LeaseToken);
        Assert.Null(task.NextReconciliationAtUtc);
    }

    [Fact]
    public void AdminCanConfirmCancelledAndClearsExpiredLease()
    {
        var now = BaselineUtc;
        var task = P57ReconciliationTaskFactory.CreateNeedsAttention(
            BackupStorageMode.LocalOnly,
            now,
            out _);
        task.AcquireReconciliationLease(
            Guid.NewGuid(),
            "worker-expired",
            now.AddSeconds(2),
            now.AddSeconds(3));

        task.ConfirmCancelled(BackupStorageMode.LocalOnly, now.AddSeconds(4));

        Assert.Equal(BackupTaskStatus.Cancelled, task.Status);
        Assert.Equal(now.AddSeconds(4), task.CompletedAtUtc);
        Assert.Equal(now.AddSeconds(4), task.CancellationRequestedAtUtc);
        Assert.Null(task.ErrorCode);
        Assert.Null(task.LeaseToken);
    }

    [Fact]
    public void AdminConfirmRejectsActiveLeaseAndNonAttention()
    {
        var now = BaselineUtc;
        var pending = new BackupTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            BackupTaskTriggerType.Manual,
            null);
        var attention = P57ReconciliationTaskFactory.CreateNeedsAttention(
            BackupStorageMode.LocalOnly,
            now,
            out _);
        attention.AcquireReconciliationLease(
            Guid.NewGuid(),
            "worker-active",
            now.AddSeconds(2),
            now.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() =>
            pending.ConfirmFailed(
                BackupStorageMode.LocalOnly,
                now,
                "admin.confirmed_failed",
                "维护者确认失败"));
        Assert.Throws<InvalidOperationException>(() =>
            attention.ConfirmFailed(
                BackupStorageMode.LocalOnly,
                now.AddSeconds(3),
                "admin.confirmed_failed",
                "维护者确认失败"));
        Assert.Throws<ArgumentException>(() =>
            attention.ConfirmCancelled(
                BackupStorageMode.LocalOnly,
                now.ToOffset(TimeSpan.FromHours(8))));
    }

    [Fact]
    public void ConfirmedFailureCanRetryButUsesNewBackupAttempt()
    {
        var task = CreateManualTask();
        var firstAttemptId = Guid.NewGuid();
        var firstToken = Guid.NewGuid();
        task.ClaimExecution(
            BackupStorageMode.LocalOnly,
            firstAttemptId,
            firstToken,
            "synthetic-worker",
            BaselineUtc,
            BaselineUtc.AddMinutes(5));
        task.RecordConfirmedFailure(
            BackupStorageMode.LocalOnly,
            firstToken,
            BaselineUtc.AddMinutes(1),
            "backup_rejected",
            "目标明确拒绝备份");

        var handling = task.RetryFailed(
            BackupStorageMode.LocalOnly,
            BaselineUtc.AddMinutes(2));

        Assert.Equal(BackupAttemptHandling.CreateNew, handling);
        Assert.Equal(1, task.RetryCount);
        Assert.Equal(BackupTaskStatus.Pending, task.Status);
        Assert.Throws<InvalidOperationException>(() =>
            task.ClaimExecution(
                BackupStorageMode.LocalOnly,
                firstAttemptId,
                Guid.NewGuid(),
                "synthetic-worker",
                BaselineUtc.AddMinutes(3),
                BaselineUtc.AddMinutes(8)));
    }

    [Fact]
    public void SnapshotFreezesLocalOnlyConfigurationWithoutSecretColumns()
    {
        var snapshot = CreateSnapshot(BackupStorageMode.LocalOnly);

        Assert.Equal(BackupStorageMode.LocalOnly, snapshot.StorageMode);
        Assert.Null(snapshot.StorageTargetId);
        Assert.Null(snapshot.RemoteCredentialReferenceId);
        Assert.Equal(30, snapshot.LocalRetentionDays);
        Assert.Null(snapshot.RemoteRetentionDays);
        Assert.True(snapshot.EncryptConnection);

        var publicPropertyNames = typeof(BackupTaskSnapshot)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(publicPropertyNames, name =>
            name.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ProtectedSecret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SnapshotRequiresRemoteTargetForRemoteModes()
    {
        Assert.Throws<ArgumentException>(() =>
            CreateSnapshot(BackupStorageMode.RemoteOnly, includeRemoteTarget: false));
    }

    [Fact]
    public void AttemptPathsMustBeCompleteUniqueToAttemptAndUsePartSuffix()
    {
        var attemptId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() =>
            new BackupAttempt(
                attemptId,
                Guid.NewGuid(),
                1,
                BaselineUtc,
                new BackupAttemptPaths(
                    "synthetic-without-id.bak",
                    "synthetic-without-id.bak",
                    null,
                    null,
                    null)));
        Assert.Throws<ArgumentException>(() =>
            new BackupAttempt(
                attemptId,
                Guid.NewGuid(),
                1,
                BaselineUtc,
                CreatePaths(attemptId) with
                {
                    RemotePartialFilePath = $"remote/{attemptId:N}.bak"
                }));
    }

    [Fact]
    public void AttemptEvidenceIsMonotonicAndIdempotentOnlyForSameValue()
    {
        var attemptId = Guid.NewGuid();
        var attempt = new BackupAttempt(
            attemptId,
            Guid.NewGuid(),
            1,
            BaselineUtc,
            CreatePaths(attemptId));
        attempt.MarkBackupRunning(BaselineUtc.AddMinutes(1));
        attempt.RecordBackupSucceeded(BaselineUtc.AddMinutes(2));
        attempt.RecordLocalVerification(1024, BaselineUtc.AddMinutes(3));
        attempt.RecordLocalVerification(1024, BaselineUtc.AddMinutes(3));

        Assert.Throws<InvalidOperationException>(() =>
            attempt.RecordLocalVerification(2048, BaselineUtc.AddMinutes(3)));

        attempt.RecordRemoteValidated(BaselineUtc.AddMinutes(4));
        attempt.RecordRemoteValidated(BaselineUtc.AddMinutes(4));
        attempt.RecordLocalCleanupCompleted(BaselineUtc.AddMinutes(5));

        Assert.Equal(1024, attempt.SourceLengthBytes);
        Assert.Equal(BaselineUtc.AddMinutes(4), attempt.RemoteValidatedAtUtc);
        Assert.Equal(BaselineUtc.AddMinutes(5), attempt.LocalCleanupCompletedAtUtc);
    }

    [Fact]
    public void StateChangeSupportsInitialEntryAndRejectsPartialPreviousState()
    {
        var taskId = Guid.NewGuid();
        var change = new BackupTaskStateChange(
            Guid.NewGuid(),
            taskId,
            null,
            null,
            BackupTaskStatus.Pending,
            BackupTaskStage.Backup,
            null,
            "task_created",
            null,
            BaselineUtc);

        Assert.Equal(taskId, change.TaskId);
        Assert.Equal(BackupTaskStatus.Pending, change.ToStatus);
        Assert.Throws<ArgumentException>(() =>
            new BackupTaskStateChange(
                Guid.NewGuid(),
                taskId,
                null,
                BackupTaskStage.Backup,
                BackupTaskStatus.Pending,
                BackupTaskStage.Backup,
                null,
                "invalid_previous_state",
                null,
                BaselineUtc));
    }

    private static BackupTask CreateManualTask()
    {
        return new BackupTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            BackupTaskTriggerType.Manual,
            null);
    }

    private static BackupAttemptPaths CreatePaths(Guid attemptId)
    {
        return new BackupAttemptPaths(
            $"synthetic-sql-root/{attemptId:N}.bak",
            $"synthetic-worker-root/{attemptId:N}.bak",
            Guid.NewGuid(),
            $"synthetic-remote-root/{attemptId:N}.bak.part",
            $"synthetic-remote-root/{attemptId:N}.bak");
    }

    private static BackupTaskSnapshot CreateSnapshot(
        BackupStorageMode storageMode,
        bool includeRemoteTarget = true)
    {
        var sourceCredentialId = Guid.NewGuid();
        var remote = includeRemoteTarget && storageMode != BackupStorageMode.LocalOnly
            ? new BackupRemoteTargetSnapshot(
                Guid.NewGuid(),
                new FileEndpointSettings(
                    FileTransferProtocol.Sftp,
                    "synthetic-remote-host",
                    22,
                    "/synthetic/remote-root",
                    Guid.NewGuid(),
                    "SHA256:synthetic-host-key"))
            : null;
        int? localRetention = storageMode == BackupStorageMode.RemoteOnly ? null : 30;
        int? remoteRetention = storageMode == BackupStorageMode.LocalOnly ? null : 90;

        return new BackupTaskSnapshot(
            Guid.NewGuid(),
            "合成策略",
            new BackupTaskIdentitySnapshot(
                Guid.NewGuid(),
                "合成服务器",
                Guid.NewGuid(),
                "合成实例",
                Guid.NewGuid(),
                "SyntheticDatabase"),
            new BackupSqlTargetSnapshot(
                "synthetic-sql-host",
                Guid.NewGuid(),
                EncryptConnection: true,
                TrustServerCertificate: false,
                CertificateTrustReason: null,
                ConnectionTimeoutSeconds: 30),
            new BackupSourceSnapshot(
                "synthetic-sql-root",
                "v1",
                new FileEndpointSettings(
                    FileTransferProtocol.Smb,
                    "synthetic-source-host",
                    null,
                    "synthetic-source-share",
                    sourceCredentialId,
                    null)),
            new BackupTaskPolicySnapshot(
                storageMode,
                remote,
                localRetention,
                remoteRetention,
                UseChecksum: true,
                UseCompression: true,
                UseCopyOnly: true,
                BackupTimeoutMinutes: 120,
                VerifyTimeoutMinutes: 60,
                TransferTimeoutMinutes: 180,
                TimeZoneId: "Taipei Standard Time"));
    }
}
