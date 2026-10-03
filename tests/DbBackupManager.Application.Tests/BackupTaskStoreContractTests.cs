using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.Tests;

public sealed class BackupTaskStoreContractTests
{
    [Fact]
    public void LeaseHandleCopiesRowVersionInBothDirections()
    {
        byte[] source = [1, 2, 3];
        var handle = new LeaseHandle(
            Guid.NewGuid(),
            Guid.NewGuid(),
            BackupLeasePurpose.Execution,
            BackupTaskStage.Backup,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1),
            source);

        source[0] = 9;
        var firstRead = handle.RowVersion;
        firstRead[1] = 9;

        Assert.Equal([1, 2, 3], handle.RowVersion);
    }

    [Fact]
    public void AttemptModelCopiesRowVersionInBothDirections()
    {
        byte[] source = [4, 5, 6];
        var model = new BackupAttemptModel(
            Guid.NewGuid(),
            1,
            BackupInvocationStatus.Prepared,
            "synthetic-local-path",
            "synthetic-source-path",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            source);

        source[0] = 9;
        var firstRead = model.RowVersion;
        firstRead[1] = 9;

        Assert.Equal([4, 5, 6], model.RowVersion);
    }

    [Fact]
    public void RecoveryPortsDoNotExposeMutationRetryOrCancellationCapabilities()
    {
        var forbiddenNames = new[]
        {
            "Execute", "Backup", "Write", "Create", "Rename", "Delete", "Restore", "Retry", "Cancel",
        };
        Assert.DoesNotContain(
            typeof(IBackupReconciliationEvidenceProbe).GetMethods()
                .Concat(typeof(IBackupTaskRecovery).GetMethods()),
            method => forbiddenNames.Any(name => method.Name.Contains(name, StringComparison.Ordinal)));
    }

    [Fact]
    public void AutomaticReconciliationEvidenceCannotRequestRetryOrCancellation()
    {
        Assert.Equal(
            ["Succeeded", "ConfirmedFailed", "Inconclusive"],
            Enum.GetNames<BackupReconciliationConclusion>());

        var publicProperties = typeof(BackupReconciliationEvidence)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(publicProperties, name =>
            name.Contains("Path", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Host", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Connection", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Endpoint", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Write", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Execute", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Restore", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Retry", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Cancel", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(BackupReconciliationEvidence).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly),
            method => method.Name.Contains("Write", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Execute", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Restore", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Retry", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Cancel", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(P57ReconciliationEvidenceCases.InvalidConstructors), MemberType = typeof(P57ReconciliationEvidenceCases))]
    public void ReconciliationEvidenceRejectsInvalidConstructorInputs(P57InvalidEvidenceCase input)
    {
        Assert.False(string.IsNullOrWhiteSpace(input.Name));
        Assert.ThrowsAny<ArgumentException>(() => new BackupReconciliationEvidence(
            input.TaskId,
            input.BackupAttemptId,
            input.Stage,
            input.ObservedAtUtc,
            input.Conclusion,
            input.Artifact,
            input.TargetBackup,
            input.Verification,
            input.ReasonCode,
            input.SourceLengthBytes));
    }

    [Theory]
    [MemberData(nameof(P57ReconciliationEvidenceCases.ValidNonSuccess), MemberType = typeof(P57ReconciliationEvidenceCases))]
    public void NonSuccessEvidenceDoesNotRequireCompletedBackupProof(P57ValidNonSuccessEvidenceCase input)
    {
        var evidence = new BackupReconciliationEvidence(
            P57ReconciliationEvidenceCases.TaskId,
            P57ReconciliationEvidenceCases.AttemptId,
            input.Stage,
            P57ReconciliationEvidenceCases.Utc,
            input.Conclusion,
            input.Artifact,
            input.TargetBackup,
            input.Verification,
            input.ReasonCode,
            input.SourceLengthBytes);

        Assert.Equal(input.Conclusion, evidence.Conclusion);
        Assert.NotEqual(BackupReconciliationConclusion.Succeeded, evidence.Conclusion);
        Assert.Equal(input.SourceLengthBytes, evidence.SourceLengthBytes);
    }

    [Fact]
    public void SuccessfulEvidenceRequiresMatchingStableArtifactAndStageSpecificProof()
    {
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var backup = new BackupReconciliationEvidence(
            taskId,
            attemptId,
            BackupTaskStage.Backup,
            now,
            BackupReconciliationConclusion.Succeeded,
            BackupArtifactObservation.PresentStable,
            TargetBackupObservation.CompletedMatching,
            BackupVerificationObservation.NotAttempted,
            "backup.completed_matching");
        var verified = new BackupReconciliationEvidence(
            taskId,
            attemptId,
            BackupTaskStage.VerifyLocal,
            now,
            BackupReconciliationConclusion.Succeeded,
            BackupArtifactObservation.PresentStable,
            TargetBackupObservation.CompletedMatching,
            BackupVerificationObservation.Succeeded,
            "verify.completed_matching",
            1024);

        Assert.Null(backup.SourceLengthBytes);
        Assert.Equal(1024, verified.SourceLengthBytes);
        Assert.Throws<ArgumentException>(() => new BackupReconciliationEvidence(
            taskId,
            attemptId,
            BackupTaskStage.Backup,
            now,
            BackupReconciliationConclusion.Succeeded,
            BackupArtifactObservation.Missing,
            TargetBackupObservation.NotFound,
            BackupVerificationObservation.NotAttempted,
            "backup.missing"));
        Assert.Throws<ArgumentException>(() => new BackupReconciliationEvidence(
            taskId,
            attemptId,
            BackupTaskStage.VerifyLocal,
            now,
            BackupReconciliationConclusion.Succeeded,
            BackupArtifactObservation.PresentStable,
            TargetBackupObservation.CompletedMatching,
            BackupVerificationObservation.Succeeded,
            "verify.missing_length"));
        var transferred = new BackupReconciliationEvidence(
            taskId,
            attemptId,
            BackupTaskStage.Transfer,
            now,
            BackupReconciliationConclusion.Succeeded,
            BackupArtifactObservation.PresentStable,
            TargetBackupObservation.CompletedMatching,
            BackupVerificationObservation.NotAttempted,
            "reconciliation.transfer_partial_complete",
            4096);
        var cleaned = new BackupReconciliationEvidence(
            taskId,
            attemptId,
            BackupTaskStage.Cleanup,
            now,
            BackupReconciliationConclusion.Succeeded,
            BackupArtifactObservation.Missing,
            TargetBackupObservation.Unknown,
            BackupVerificationObservation.NotAttempted,
            "reconciliation.cleanup_source_absent");
        Assert.Equal(4096, transferred.SourceLengthBytes);
        Assert.Equal(BackupArtifactObservation.Missing, cleaned.Artifact);
        _ = new BackupReconciliationEvidence(
            taskId,
            attemptId,
            BackupTaskStage.Transfer,
            now,
            BackupReconciliationConclusion.Inconclusive,
            BackupArtifactObservation.Unknown,
            TargetBackupObservation.Unknown,
            BackupVerificationObservation.NotAttempted,
            "reconciliation.remote_copy_unconfirmed");
    }
}
