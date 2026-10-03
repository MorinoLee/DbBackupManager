using System.Data;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using Microsoft.EntityFrameworkCore;

using static DbBackupManager.Infrastructure.Persistence.BackupTaskPersistence;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupTaskCommandStore(BackupTaskPersistence persistence)
{
    private const string CreatedReason = "task.created";

    private const string CancellationReason = "task.cancellation_requested";

    private const string RetryReason = "task.retry_requested";

    private const string AdminReconciliationReason = "admin_reconciliation_requested";

    public async Task<BackupTaskStoreResult<BackupTaskStateModel>> CreateTaskAsync(
        CreateBackupTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await persistence.ExecuteWithStrategyAsync(async context =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
                if (!await IsActorValidAsync(context, command.ActorAdminUserId, command.ActorSecurityStamp, cancellationToken))
                    return new BackupTaskStoreResult<BackupTaskStateModel>(BackupTaskStoreResultCode.AuthenticationRequired);
                var replay = await ReadMutationAsync(
                    context,
                    command.MutationId,
                    command.TaskId,
                    CreatedReason,
                    cancellationToken);
                if (replay.Found)
                {
                    return replay.Matches
                        && replay.Task!.PolicyId == command.PolicyId
                        && replay.Task.TriggerType == command.TriggerType
                        && replay.Task.ScheduledSlotAtUtc == command.ScheduledSlotAtUtc
                            ? MutationStateResult(replay)
                            : new BackupTaskStoreResult<BackupTaskStateModel>(
                                BackupTaskStoreResultCode.StateMismatch);
                }

                if (command.TriggerType == BackupTaskTriggerType.Scheduled)
                {
                    if (await context.BackupPolicies.AnyAsync(x => x.Id == command.PolicyId && x.IsManualOnly, cancellationToken))
                        return new BackupTaskStoreResult<BackupTaskStateModel>(BackupTaskStoreResultCode.ConfigurationUnavailable);

                    var existing = await context.BackupTasks.SingleOrDefaultAsync(
                        task => task.PolicyId == command.PolicyId
                            && task.ScheduledSlotAtUtc == command.ScheduledSlotAtUtc,
                        cancellationToken);
                    if (existing is not null)
                    {
                        return new BackupTaskStoreResult<BackupTaskStateModel>(
                            BackupTaskStoreResultCode.AlreadyExists,
                            existing.ToStateModel());
                    }
                }

                var snapshot = await TryBuildSnapshotAsync(
                    context,
                    command.TaskId,
                    command.PolicyId,
                    cancellationToken);
                if (snapshot is null || !BackupTaskPathFactory.TryCreate(
                        snapshot, command.TaskId, command.OccurredAtUtc, out _))
                {
                    return new BackupTaskStoreResult<BackupTaskStateModel>(
                        BackupTaskStoreResultCode.ConfigurationUnavailable);
                }

                var task = new BackupTask(
                    command.TaskId,
                    command.PolicyId,
                    command.TriggerType,
                    command.ScheduledSlotAtUtc);
                context.AddRange(
                    task,
                    snapshot,
                    CreateStateChange(
                        command.MutationId,
                        task,
                        fromStatus: null,
                        fromStage: null,
                        CreatedReason,
                        message: null,
                        command.OccurredAtUtc),
                    CreateAudit(
                        command.ActorAdminUserId,
                        task.Id,
                        "backup.task.create",
                        "succeeded",
                        null));
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new BackupTaskStoreResult<BackupTaskStateModel>(
                    BackupTaskStoreResultCode.Succeeded,
                    task.ToStateModel());
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new BackupTaskStoreResult<BackupTaskStateModel>(
                BackupTaskStoreResultCode.ConcurrencyConflict);
        }
        catch (DbUpdateException)
        {
            await using var context = await persistence.CreateDbContextAsync(cancellationToken);
            var replay = await ReadMutationAsync(
                context,
                command.MutationId,
                command.TaskId,
                CreatedReason,
                cancellationToken);
            if (replay.Found)
            {
                return MutationStateResult(replay);
            }

            if (command.TriggerType == BackupTaskTriggerType.Scheduled)
            {
                var existing = await context.BackupTasks.SingleOrDefaultAsync(
                    task => task.PolicyId == command.PolicyId
                        && task.ScheduledSlotAtUtc == command.ScheduledSlotAtUtc,
                    cancellationToken);
                if (existing is not null)
                {
                    return new BackupTaskStoreResult<BackupTaskStateModel>(
                        BackupTaskStoreResultCode.AlreadyExists,
                        existing.ToStateModel());
                }
            }

            return new BackupTaskStoreResult<BackupTaskStateModel>(
                BackupTaskStoreResultCode.ConcurrencyConflict);
        }
    }

    public Task<BackupTaskStoreResult<BackupTaskStateModel>> RequestCancellationAsync(
        BackupTaskMutationCommand command,
        CancellationToken cancellationToken = default)
    {
        return MutateTaskAsync(
            command,
            CancellationReason,
            "backup.task.cancel.request",
            (task, storageMode) =>
            {
                if (task.CancellationRequestedAtUtc is not null)
                {
                    return BackupTaskStoreResultCode.AlreadyExists;
                }

                task.RequestCancellation(command.OccurredAtUtc, storageMode);
                return BackupTaskStoreResultCode.Succeeded;
            },
            cancellationToken);
    }

    public Task<BackupTaskStoreResult<BackupTaskStateModel>> RetryFailedAsync(
        BackupTaskMutationCommand command,
        CancellationToken cancellationToken = default)
    {
        return MutateTaskAsync(
            command,
            RetryReason,
            "backup.task.retry",
            (task, storageMode) =>
            {
                task.RetryFailed(storageMode, command.OccurredAtUtc);
                return BackupTaskStoreResultCode.Succeeded;
            },
            cancellationToken);
    }

    public Task<BackupTaskStoreResult<BackupTaskStateModel>> RequestReconciliationAsync(
        BackupTaskMutationCommand command,
        CancellationToken cancellationToken = default)
    {
        return MutateTaskAsync(
            command,
            AdminReconciliationReason,
            "backup.task.reconciliation.request",
            (task, _) =>
            {
                task.RequestReconciliation(command.OccurredAtUtc);
                return BackupTaskStoreResultCode.Succeeded;
            },
            cancellationToken,
            requireActor: true);
    }

    public async Task<BackupTaskStateModel?> FindTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await persistence.CreateDbContextAsync(cancellationToken);
        var task = await context.BackupTasks.SingleOrDefaultAsync(
            item => item.Id == taskId,
            cancellationToken);
        return task?.ToStateModel();
    }

    private async Task<BackupTaskStoreResult<BackupTaskStateModel>> MutateTaskAsync(
        BackupTaskMutationCommand command,
        string reason,
        string auditAction,
        Func<BackupTask, BackupStorageMode, BackupTaskStoreResultCode> mutate,
        CancellationToken cancellationToken,
        bool requireActor = false)
    {
        try
        {
            return await persistence.ExecuteWithStrategyAsync(async context =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                if ((requireActor && command.ActorAdminUserId is null)
                    || !await IsActorValidAsync(context, command.ActorAdminUserId, command.ActorSecurityStamp, cancellationToken))
                    return new BackupTaskStoreResult<BackupTaskStateModel>(BackupTaskStoreResultCode.AuthenticationRequired);
                var replay = await ReadMutationAsync(
                    context,
                    command.MutationId,
                    command.TaskId,
                    reason,
                    cancellationToken);
                if (replay.Found)
                {
                    return MutationStateResult(replay);
                }

                var task = await context.BackupTasks.AsTracking().SingleOrDefaultAsync(
                    item => item.Id == command.TaskId,
                    cancellationToken);
                if (task is null)
                {
                    return new BackupTaskStoreResult<BackupTaskStateModel>(
                        BackupTaskStoreResultCode.NotFound);
                }

                var snapshot = await context.BackupTaskSnapshots.SingleAsync(
                    item => item.TaskId == task.Id,
                    cancellationToken);
                var fromStatus = task.Status;
                var fromStage = task.CurrentStage;
                BackupTaskStoreResultCode mutationResult;
                try
                {
                    mutationResult = mutate(task, snapshot.StorageMode);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    return new BackupTaskStoreResult<BackupTaskStateModel>(
                        BackupTaskStoreResultCode.StateMismatch);
                }

                if (mutationResult == BackupTaskStoreResultCode.AlreadyExists)
                {
                    return new BackupTaskStoreResult<BackupTaskStateModel>(
                        mutationResult,
                        task.ToStateModel());
                }

                context.AddRange(
                    CreateStateChange(
                        command.MutationId,
                        task,
                        fromStatus,
                        fromStage,
                        reason,
                        null,
                        command.OccurredAtUtc),
                    CreateAudit(command.ActorAdminUserId, task.Id, auditAction, "succeeded", reason));
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new BackupTaskStoreResult<BackupTaskStateModel>(
                    BackupTaskStoreResultCode.Succeeded,
                    task.ToStateModel());
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new BackupTaskStoreResult<BackupTaskStateModel>(
                BackupTaskStoreResultCode.ConcurrencyConflict);
        }
        catch (DbUpdateException)
        {
            return await persistence.ResolveStateReplayAsync(
                command.TaskId,
                command.MutationId,
                reason,
                cancellationToken);
        }
    }

    private static async Task<BackupTaskSnapshot?> TryBuildSnapshotAsync(
        PlatformDbContext context,
        Guid taskId,
        Guid policyId,
        CancellationToken cancellationToken)
    {
        var policy = await context.BackupPolicies.SingleOrDefaultAsync(
            item => item.Id == policyId,
            cancellationToken);
        if (policy is null || !policy.IsEnabled)
        {
            return null;
        }

        var database = await context.ManagedDatabases.SingleOrDefaultAsync(
            item => item.Id == policy.DatabaseId,
            cancellationToken);
        if (database is null
            || database.IsSystemDatabase
            || !database.IsManaged
            || !database.IsAvailable)
        {
            return null;
        }

        var instance = await context.DatabaseInstances.SingleOrDefaultAsync(
            item => item.Id == database.InstanceId,
            cancellationToken);
        var server = instance is null
            ? null
            : await context.DatabaseServers.SingleOrDefaultAsync(
                item => item.Id == instance.ServerId,
                cancellationToken);
        if (instance is null || !instance.IsEnabled || server is null || !server.IsEnabled)
        {
            return null;
        }

        var sqlCredential = await context.CredentialReferences.SingleOrDefaultAsync(
            item => item.Id == instance.SqlCredentialReferenceId,
            cancellationToken);
        var sourceCredential = await context.CredentialReferences.SingleOrDefaultAsync(
            item => item.Id == server.StagingCredentialReferenceId,
            cancellationToken);
        if (!IsCredentialUsable(sqlCredential, CredentialKind.SqlPassword)
            || !IsEndpointCredentialUsable(sourceCredential, server.StagingAccessProtocol))
        {
            return null;
        }

        BackupRemoteTargetSnapshot? remoteTarget = null;
        if (policy.StorageTargetId is not null)
        {
            var target = await context.StorageTargets.SingleOrDefaultAsync(
                item => item.Id == policy.StorageTargetId.Value,
                cancellationToken);
            if (target is null || !target.IsEnabled)
            {
                return null;
            }

            var targetCredential = await context.CredentialReferences.SingleOrDefaultAsync(
                item => item.Id == target.CredentialReferenceId,
                cancellationToken);
            if (!IsEndpointCredentialUsable(targetCredential, target.Protocol))
            {
                return null;
            }

            remoteTarget = new BackupRemoteTargetSnapshot(
                target.Id,
                new FileEndpointSettings(
                    target.Protocol,
                    target.Host,
                    target.Port,
                    target.BasePath,
                    target.CredentialReferenceId,
                    target.SftpHostKeyFingerprint));
        }

        return new BackupTaskSnapshot(
            taskId,
            policy.Name,
            new BackupTaskIdentitySnapshot(
                server.Id,
                server.Name,
                instance.Id,
                instance.Name,
                database.Id,
                database.DatabaseName),
            new BackupSqlTargetSnapshot(
                instance.ConnectionAddress,
                instance.SqlCredentialReferenceId,
                instance.EncryptConnection,
                instance.TrustServerCertificate,
                instance.CertificateTrustReason,
                instance.ConnectionTimeoutSeconds, instance.AllowLegacyTls, instance.LegacyTlsReason),
            new BackupSourceSnapshot(
                server.LocalBackupRootPath,
                BackupTaskPathFactory.CurrentVersion,
                new FileEndpointSettings(
                    server.StagingAccessProtocol,
                    server.StagingAccessHost,
                    server.StagingAccessPort,
                    server.StagingAccessBasePath,
                    server.StagingCredentialReferenceId,
                    server.StagingSftpHostKeyFingerprint)),
            new BackupTaskPolicySnapshot(
                policy.StorageMode,
                remoteTarget,
                policy.LocalRetentionDays,
                policy.RemoteRetentionDays,
                policy.UseChecksum,
                policy.UseCompression,
                policy.UseCopyOnly,
                policy.BackupTimeoutMinutes,
                policy.VerifyTimeoutMinutes,
                policy.TransferTimeoutMinutes,
                policy.TimeZoneId));
    }

    private static bool IsCredentialUsable(
        CredentialReference? credential,
        CredentialKind requiredKind)
    {
        return credential is not null && credential.IsEnabled && credential.Kind == requiredKind;
    }

    private static bool IsEndpointCredentialUsable(
        CredentialReference? credential,
        FileTransferProtocol protocol)
    {
        return credential is not null
            && credential.IsEnabled
            && protocol switch
            {
                FileTransferProtocol.Smb => credential.Kind == CredentialKind.SmbPassword,
                FileTransferProtocol.Sftp => credential.Kind is CredentialKind.SftpPassword
                    or CredentialKind.SftpPrivateKey,
                _ => false,
            };
    }
}
