using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupArtifactDeletionGuard(IDbContextFactory<PlatformDbContext> factory, TimeProvider clock)
    : IBackupArtifactDeletionGuard
{
    public async Task<BackupArtifactDeletionDecision> EvaluateAsync(BackupFileDeleteRequest request, CancellationToken cancellationToken = default)
    {
        var owner = request.Owner;
        if (owner is null) return new(false, "artifact.owner_unknown");
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var task = await context.BackupTasks.SingleOrDefaultAsync(x => x.Id == owner.TaskId, cancellationToken);
        var snapshot = await context.BackupTaskSnapshots.SingleOrDefaultAsync(x => x.TaskId == owner.TaskId, cancellationToken);
        if (task is null || snapshot is null) return new(false, "artifact.owner_unknown");
        if (task.PlanId is not null || snapshot.Purpose is not null || snapshot.FileNameRuleVersion == "v3")
            return new(false, "plan.copy.protected");
        if (task.PolicyId is null) return new(false, "artifact.owner_unknown");
        if (owner.Role == BackupArtifactPathRole.RegisteredFile)
        {
            var file = await context.BackupFiles.SingleOrDefaultAsync(x => x.Id == owner.FileId && x.TaskId == task.Id, cancellationToken);
            if (file is null) return new(false, "artifact.owner_unknown");
            if (file.BackupSetId is not null) return new(false, "plan.copy.protected");
            if (await BackupArtifactProtection.IsProtectedAsync(context, file.Id, cancellationToken)) return new(false, "artifact.owner_unknown");
            if (file.Status != BackupFileStatus.DeletePending || file.DeletionLeaseToken != owner.LeaseToken
                || file.DeletionLeaseExpiresAtUtc <= clock.GetUtcNow() || !file.RowVersion.SequenceEqual(owner.RowVersion)
                || file.Path != request.Path || file.LengthBytes != request.ExpectedLengthBytes) return new(false, "artifact.owner_unknown");
            var endpoint = await BackupFileRetentionStore.TryCreateEndpointAsync(context, file, true, cancellationToken);
            return endpoint is not null && SameEndpoint(endpoint, request.Endpoint)
                ? new(true) : new(false, "artifact.owner_unknown");
        }
        if (owner.AttemptId is null || owner.FileId is not null || task.CurrentBackupAttemptId != owner.AttemptId
            || task.Status != BackupTaskStatus.Running || task.LeasePurpose != BackupLeasePurpose.Execution
            || task.LeaseToken != owner.LeaseToken || task.LeaseExpiresAtUtc <= clock.GetUtcNow()
            || !task.RowVersion.SequenceEqual(owner.RowVersion)) return new(false, "artifact.owner_unknown");
        var attempt = await context.BackupAttempts.SingleOrDefaultAsync(x => x.Id == owner.AttemptId && x.TaskId == task.Id, cancellationToken);
        if (attempt is null || attempt.BackupInvocationStatus != BackupInvocationStatus.Succeeded || attempt.LocalVerifiedAtUtc is null)
            return new(false, "artifact.owner_unknown");
        var model = snapshot.ToSnapshotModel();
        var endpointModel = owner.Role == BackupArtifactPathRole.RemotePartial ? model.Policy.RemoteEndpoint : model.WorkerSourceEndpoint;
        if (endpointModel is null) return new(false, "artifact.owner_unknown");
        var matches = owner.Role switch
        {
            BackupArtifactPathRole.RemotePartial => task.CurrentStage == BackupTaskStage.Transfer && request.Path == attempt.RemotePartialFilePath,
            BackupArtifactPathRole.RemoteOnlySource => task.CurrentStage == BackupTaskStage.Cleanup
                && snapshot.StorageMode == BackupStorageMode.RemoteOnly && attempt.RemoteValidatedAtUtc is not null
                && request.Path == attempt.WorkerSourceFilePath && request.ExpectedLengthBytes == attempt.SourceLengthBytes,
            _ => false
        };
        var expectedEndpoint = new BackupFileEndpointInput(endpointModel.Protocol, endpointModel.Host, endpointModel.Port,
            endpointModel.BasePath, endpointModel.CredentialReferenceId, endpointModel.SftpHostKeyFingerprint);
        return matches && SameEndpoint(expectedEndpoint, request.Endpoint) ? new(true) : new(false, "artifact.owner_unknown");
    }

    private static bool SameEndpoint(BackupFileEndpointInput a, BackupFileEndpointInput b) =>
        a.Protocol == b.Protocol && a.Host == b.Host && a.Port == b.Port && a.RootPath == b.RootPath
        && a.CredentialReferenceId == b.CredentialReferenceId && a.SftpHostKeyFingerprint == b.SftpHostKeyFingerprint;
}
