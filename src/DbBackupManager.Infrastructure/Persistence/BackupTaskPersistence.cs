using System.Data;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupTaskPersistence(IDbContextFactory<PlatformDbContext> contextFactory)
{
    public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken) =>
        contextFactory.CreateDbContextAsync(cancellationToken);

    public async Task<T> ExecuteWithStrategyAsync<T>(
        Func<PlatformDbContext, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            return await operation(context);
        });
    }

    internal static async Task<bool> IsActorValidAsync(PlatformDbContext db, Guid? id, string? stamp, CancellationToken token)
    {
        if (id is null) return stamp is null;
        var admin = await db.AdminUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        return admin is { IsEnabled: true } && !admin.IsLockedOut(DateTimeOffset.UtcNow)
            && string.Equals(admin.SecurityStamp, stamp, StringComparison.Ordinal);
    }

    internal static BackupTaskStoreResultCode? ValidateLease(
        BackupTask task,
        LeaseHandle lease,
        DateTimeOffset utcNow)
    {
        if (!HasSameLeaseIdentity(task, lease)
            || task.LeaseExpiresAtUtc is null
            || task.LeaseExpiresAtUtc <= utcNow)
        {
            return BackupTaskStoreResultCode.LeaseLost;
        }

        if (!task.RowVersion.SequenceEqual(lease.RowVersion))
        {
            return BackupTaskStoreResultCode.ConcurrencyConflict;
        }

        return null;
    }

    internal static bool HasSameLeaseIdentity(BackupTask task, LeaseHandle lease)
    {
        var requiredStatus = lease.Purpose == BackupLeasePurpose.Execution
            ? BackupTaskStatus.Running
            : BackupTaskStatus.NeedsAttention;
        return task.Status == requiredStatus
            && task.CurrentStage == lease.Stage
            && task.CurrentBackupAttemptId == lease.BackupAttemptId
            && task.LeasePurpose == lease.Purpose
            && task.LeaseToken == lease.LeaseToken;
    }

    internal static void ApplySuccessfulEvidence(
        BackupTaskStage stage,
        BackupAttempt attempt,
        DateTimeOffset evidenceAtUtc,
        long? sourceLengthBytes)
    {
        switch (stage)
        {
            case BackupTaskStage.Backup:
                attempt.RecordBackupSucceeded(evidenceAtUtc);
                break;
            case BackupTaskStage.VerifyLocal:
                attempt.RecordLocalVerification(
                    sourceLengthBytes ?? throw new ArgumentException("本地校验成功必须提供源文件长度。"),
                    evidenceAtUtc);
                break;
            case BackupTaskStage.Transfer:
                break;
            case BackupTaskStage.ValidateCopy:
                attempt.RecordRemoteValidated(evidenceAtUtc);
                break;
            case BackupTaskStage.Cleanup:
                attempt.RecordLocalCleanupCompleted(evidenceAtUtc);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(stage));
        }
    }

    internal static void RegisterSuccessfulStageFile(
        PlatformDbContext context,
        BackupTask task,
        BackupTaskSnapshot snapshot,
        BackupAttempt attempt,
        BackupTaskStage stage,
        bool succeeded,
        Guid mutationId,
        DateTimeOffset occurredAtUtc)
    {
        if (!succeeded)
        {
            return;
        }

        BackupFile? file = null;
        if (stage == BackupTaskStage.VerifyLocal
            && snapshot.LocalRetentionDays is not null)
        {
            file = BackupFile.CreateLocal(
                Guid.NewGuid(),
                task.Id,
                attempt.Id,
                snapshot.DatabaseId,
                snapshot.ServerId,
                snapshot.SourceAccessProtocol,
                attempt.WorkerSourceFilePath,
                attempt.SourceLengthBytes!.Value,
                attempt.LocalVerifiedAtUtc!.Value,
                snapshot.LocalRetentionDays.Value);
        }
        else if (stage == BackupTaskStage.ValidateCopy
            && snapshot.StorageTargetId is not null)
        {
            file = BackupFile.CreateRemote(
                Guid.NewGuid(),
                task.Id,
                attempt.Id,
                snapshot.DatabaseId,
                snapshot.StorageTargetId.Value,
                snapshot.RemoteProtocol!.Value,
                attempt.RemoteFinalFilePath!,
                attempt.SourceLengthBytes!.Value,
                attempt.RemoteValidatedAtUtc!.Value,
                snapshot.RemoteRetentionDays!.Value);
        }

        if (file is null)
        {
            return;
        }

        context.BackupFiles.Add(file);
        context.BackupFileStateChanges.Add(new BackupFileStateChange(
            mutationId,
            file.Id,
            task.Id,
            null,
            BackupFileStatus.Available,
            "file.registered",
            occurredAtUtc));
    }

    internal static BackupTaskStateChange CreateStateChange(
        Guid mutationId,
        BackupTask task,
        BackupTaskStatus? fromStatus,
        BackupTaskStage? fromStage,
        string reason,
        string? message,
        DateTimeOffset occurredAtUtc)
    {
        return new BackupTaskStateChange(
            mutationId,
            task.Id,
            fromStatus,
            fromStage,
            task.Status,
            task.CurrentStage,
            task.CurrentBackupAttemptId,
            reason,
            message,
            occurredAtUtc);
    }

    internal static AuditRecord CreateAudit(
        Guid? actorAdminUserId,
        Guid taskId,
        string action,
        string result,
        string? reason)
    {
        return new AuditRecord(
            actorAdminUserId,
            action,
            "BackupTask",
            taskId.ToString("N"),
            result,
            reason);
    }

    internal static BackupExecutionWorkItem CreateWorkItem(
        BackupTask task,
        BackupTaskSnapshot snapshot,
        BackupAttempt attempt)
    {
        return new BackupExecutionWorkItem(
            task.ToStateModel(),
            snapshot.ToSnapshotModel(),
            attempt.ToAttemptModel(),
            task.ToLeaseHandle());
    }

    internal static async Task<BackupExecutionWorkItem?> LoadWorkItemAsync(
        PlatformDbContext context,
        BackupTask task,
        CancellationToken cancellationToken)
    {
        if (task.PlanId is not null || task.PolicyId is null || task.CurrentBackupAttemptId is null || task.LeaseToken is null)
        {
            return null;
        }

        var snapshot = await context.BackupTaskSnapshots.SingleAsync(
            item => item.TaskId == task.Id,
            cancellationToken);
        var attempt = await context.BackupAttempts.SingleOrDefaultAsync(
            item => item.Id == task.CurrentBackupAttemptId.Value && item.TaskId == task.Id,
            cancellationToken);
        return attempt is null ? null : CreateWorkItem(task, snapshot, attempt);
    }

    internal static async Task<MutationRead> ReadMutationAsync(
        PlatformDbContext context,
        Guid mutationId,
        Guid taskId,
        string reason,
        CancellationToken cancellationToken)
    {
        var change = await context.BackupTaskStateChanges.SingleOrDefaultAsync(
            item => item.MutationId == mutationId,
            cancellationToken);
        if (change is null)
        {
            return MutationRead.Missing;
        }

        var task = await context.BackupTasks.SingleAsync(
            item => item.Id == change.TaskId,
            cancellationToken);
        return new MutationRead(
            Found: true,
            Matches: change.TaskId == taskId
                && (reason == BackupTaskCommandStore.CancellationReason || task.PlanId is null && task.PolicyId is not null)
                && string.Equals(change.ReasonCode, reason, StringComparison.Ordinal),
            task);
    }

    internal static BackupTaskStoreResult<BackupTaskStateModel> MutationStateResult(
        MutationRead replay)
    {
        return new BackupTaskStoreResult<BackupTaskStateModel>(
            replay.Matches
                ? BackupTaskStoreResultCode.AlreadyApplied
                : BackupTaskStoreResultCode.StateMismatch,
            replay.Matches ? replay.Task!.ToStateModel() : null);
    }

    internal static BackupTaskStoreResult<BackupTaskTransitionModel> MutationTransitionResult(
        MutationRead replay,
        Guid expectedLeaseToken)
    {
        if (!replay.Matches)
        {
            return new BackupTaskStoreResult<BackupTaskTransitionModel>(
                BackupTaskStoreResultCode.StateMismatch);
        }

        var task = replay.Task!;
        var lease = task.LeaseToken == expectedLeaseToken ? task.ToLeaseHandle() : null;
        return new BackupTaskStoreResult<BackupTaskTransitionModel>(
            BackupTaskStoreResultCode.AlreadyApplied,
            new BackupTaskTransitionModel(task.ToStateModel(), lease));
    }

    public async Task<BackupTaskStoreResult<BackupTaskStateModel>> ResolveStateReplayAsync(
        Guid taskId,
        Guid mutationId,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var replay = await ReadMutationAsync(
            context,
            mutationId,
            taskId,
            reason,
            cancellationToken);
        return replay.Found
            ? MutationStateResult(replay)
            : new BackupTaskStoreResult<BackupTaskStateModel>(
                BackupTaskStoreResultCode.ConcurrencyConflict);
    }

    public async Task<BackupTaskStoreResult<BackupTaskTransitionModel>> ResolveTransitionReplayAsync(
        Guid taskId,
        Guid mutationId,
        string reason,
        Guid expectedLeaseToken,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var replay = await ReadMutationAsync(
            context,
            mutationId,
            taskId,
            reason,
            cancellationToken);
        return replay.Found
            ? MutationTransitionResult(replay, expectedLeaseToken)
            : new BackupTaskStoreResult<BackupTaskTransitionModel>(
                BackupTaskStoreResultCode.ConcurrencyConflict);
    }

    internal readonly record struct MutationRead(bool Found, bool Matches, BackupTask? Task)
    {
        public static MutationRead Missing => new(false, false, null);
    }
}
