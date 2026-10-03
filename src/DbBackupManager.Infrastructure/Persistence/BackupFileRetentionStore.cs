using System.Data;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupFileRetentionStore(
    IDbContextFactory<PlatformDbContext> contextFactory) : IBackupFileRetentionStore
{
    private const string ClaimedReason = "file.delete_claimed";
    private const string DeletedReason = "file.deleted";
    private const string MissingReason = "file.missing";
    private const string FailedReason = "file.delete_failed";
    private const string HealthMissingReason = "file.health_missing";

    public async Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ClaimNextAsync(
        ClaimBackupFileDeletionCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateClaim(command);
        return await ClaimAsync(null, command, cancellationToken);
    }

    public async Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ClaimFileAsync(
        Guid fileId,
        ClaimBackupFileDeletionCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateClaim(command);
        RequireId(fileId, nameof(fileId));
        return await ClaimAsync(fileId, command, cancellationToken);
    }

    private async Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ClaimAsync(
        Guid? requiredFileId,
        ClaimBackupFileDeletionCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteWithStrategyAsync(async context =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
                var replay = await context.BackupFileStateChanges.SingleOrDefaultAsync(
                    change => change.MutationId == command.MutationId,
                    cancellationToken);
                if (replay is not null)
                {
                    if (requiredFileId is { } fileId && replay.FileId != fileId)
                    {
                        await transaction.CommitAsync(cancellationToken);
                        return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                            BackupTaskStoreResultCode.StateMismatch);
                    }

                    var replayed = await ReplayClaimAsync(context, replay, command, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return replayed;
                }

                var selected = requiredFileId is { } exactFileId
                    ? await TryClaimFileAsync(context, exactFileId, command, cancellationToken)
                    : await TryClaimAsync(context, command, cancellationToken);
                if (selected.Value is { } claimed)
                {
                    await context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                        BackupTaskStoreResultCode.Succeeded,
                        CreateDetachedWorkItem(
                            claimed.File,
                            claimed.Endpoint,
                            command.LeaseToken,
                            claimed.File.DeletionLeaseExpiresAtUtc
                                ?? throw new InvalidOperationException("删除租约缺少到期时间。")));
                }

                await transaction.CommitAsync(cancellationToken);
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                    selected.Code);
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                BackupTaskStoreResultCode.ConcurrencyConflict);
        }
    }

    private static async Task<BackupTaskStoreResult<DeletionCandidate>> TryClaimFileAsync(
        PlatformDbContext context,
        Guid fileId,
        ClaimBackupFileDeletionCommand command,
        CancellationToken cancellationToken)
    {
        var file = await context.BackupFiles.AsTracking().SingleOrDefaultAsync(
            item => item.Id == fileId,
            cancellationToken);
        if (file is null)
        {
            return new(BackupTaskStoreResultCode.NotFound);
        }

        DeletionCandidate? claimed;
        if (file.Status == BackupFileStatus.DeletePending
            && file.DeletionLeaseExpiresAtUtc <= command.AcquiredAtUtc)
        {
            claimed = await TryAcceptAsync(context, file, command, takeover: true, cancellationToken);
        }
        else if (file.Status == BackupFileStatus.DeleteFailed
            && file.NextDeletionAttemptAtUtc <= command.AcquiredAtUtc)
        {
            claimed = await TryAcceptAsync(context, file, command, takeover: false, cancellationToken);
        }
        else if (file.Status == BackupFileStatus.Available)
        {
            var group = await LoadGroupAsync(context, file.RetentionGroup, cancellationToken);
            var candidate = BackupFileRetentionPolicy.SelectDeletionCandidate(group, command.AcquiredAtUtc);
            if (candidate is null) return new(BackupTaskStoreResultCode.NotFound);
            if (candidate.Id != file.Id) return new(BackupTaskStoreResultCode.StateMismatch);
            claimed = await TryAcceptAsync(context, candidate, command, takeover: false, cancellationToken);
        }
        else
        {
            return new(BackupTaskStoreResultCode.StateMismatch);
        }

        return claimed is null
            ? new(BackupTaskStoreResultCode.NotFound)
            : new(BackupTaskStoreResultCode.Succeeded, claimed);
    }

    public async Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> RefreshWorkItemAsync(
        Guid fileId,
        Guid leaseToken,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        RequireId(fileId, nameof(fileId));
        RequireId(leaseToken, nameof(leaseToken));
        var now = RequireUtc(utcNow, nameof(utcNow));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var file = await context.BackupFiles.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == fileId,
            cancellationToken);
        if (file is null)
        {
            return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.NotFound);
        }

        if (file.Status != BackupFileStatus.DeletePending
            || file.DeletionLeaseToken != leaseToken
            || file.DeletionLeaseExpiresAtUtc is null
            || file.DeletionLeaseExpiresAtUtc <= now)
        {
            return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(BackupTaskStoreResultCode.LeaseLost);
        }

        var group = await LoadGroupAsync(context, file.RetentionGroup, cancellationToken);
        if (!BackupFileRetentionPolicy.HasProtectedAvailableCopy(group, file.Id))
        {
            return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                BackupTaskStoreResultCode.StateMismatch);
        }

        return await LoadWorkItemAsync(context, file.Id, leaseToken, requireAccess: true, cancellationToken);
    }

    public async Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> CommitAsync(
        BackupFileRetentionWorkItem work,
        BackupFileRetentionCommitCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(command);
        RequireId(command.MutationId, nameof(command.MutationId));
        var occurredAt = RequireUtc(command.OccurredAtUtc, nameof(command.OccurredAtUtc));
        if (!Enum.IsDefined(command.Outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }
        if (command.Outcome == BackupFileRetentionOutcome.Failed
            && string.IsNullOrWhiteSpace(command.ErrorCode))
        {
            throw new ArgumentException("删除失败必须提供稳定错误码。", nameof(command));
        }

        try
        {
            return await ExecuteWithStrategyAsync(async context =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
                var replay = await context.BackupFileStateChanges.SingleOrDefaultAsync(
                    change => change.MutationId == command.MutationId,
                    cancellationToken);
                if (replay is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return replay.FileId == work.FileId && replay.ReasonCode == Reason(command.Outcome)
                        ? new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                            BackupTaskStoreResultCode.AlreadyApplied)
                        : new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                            BackupTaskStoreResultCode.StateMismatch);
                }

                var file = await context.BackupFiles.AsTracking().SingleOrDefaultAsync(
                    item => item.Id == work.FileId,
                    cancellationToken);
                if (file is null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                        BackupTaskStoreResultCode.NotFound);
                }

                if (file.Status != BackupFileStatus.DeletePending
                    || file.DeletionLeaseToken != work.LeaseToken
                    || file.DeletionLeaseExpiresAtUtc is null
                    || file.DeletionLeaseExpiresAtUtc <= occurredAt)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                        BackupTaskStoreResultCode.LeaseLost);
                }

                if (!file.RowVersion.SequenceEqual(work.RowVersion))
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                        BackupTaskStoreResultCode.ConcurrencyConflict);
                }

                var from = file.Status;
                switch (command.Outcome)
                {
                    case BackupFileRetentionOutcome.Deleted:
                        file.RecordDeleted(work.LeaseToken, occurredAt);
                        break;
                    case BackupFileRetentionOutcome.Missing:
                        file.RecordMissing(work.LeaseToken, occurredAt);
                        break;
                    case BackupFileRetentionOutcome.Failed:
                        file.RecordDeletionFailure(
                            work.LeaseToken,
                            occurredAt,
                            command.ErrorCode!,
                            occurredAt + BackupFileRetentionPolicy.DeletionBackoff(file.DeletionAttemptCount));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(command));
                }

                context.BackupFileStateChanges.Add(new BackupFileStateChange(
                    command.MutationId,
                    file.Id,
                    file.TaskId,
                    from,
                    file.Status,
                    Reason(command.Outcome),
                    occurredAt));
                context.AuditRecords.Add(new AuditRecord(
                    null,
                    "backup.file.retention.commit",
                    "BackupFile",
                    file.Id.ToString("N"),
                    "succeeded",
                    Reason(command.Outcome)));
                NotificationOutboxWriter.EnqueueRetention(
                    context,
                    command.MutationId,
                    file.Id,
                    file.TaskId,
                    command.Outcome,
                    occurredAt,
                    command.ErrorCode);
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                    BackupTaskStoreResultCode.Succeeded,
                    CreateDetachedWorkItem(file, work.Endpoint, work.LeaseToken, work.LeaseExpiresAtUtc));
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                BackupTaskStoreResultCode.ConcurrencyConflict);
        }
    }

    public async Task<BackupTaskStoreResult<BackupFileHealthWorkItem>> FindNextHealthInspectionAsync(
        Guid? afterFileId,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        _ = RequireUtc(utcNow, nameof(utcNow));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        // GUID 范围筛选与排序均交给 SQL Server，避免与 .NET Guid.CompareTo 的顺序混用。
        // 游标通过参数传入；越过末尾时再查询首条记录回绕。
        var next = afterFileId is { } cursor
            ? await context.BackupFiles.FromSqlInterpolated(
                    $"SELECT * FROM [BackupFiles] WHERE [Status] = {nameof(BackupFileStatus.Available)} AND [Id] > {cursor}")
                .AsNoTracking()
                .OrderBy(file => file.Id)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        next ??= await context.BackupFiles.AsNoTracking()
            .Where(file => file.Status == BackupFileStatus.Available)
            .OrderBy(file => file.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (next is null)
        {
            return new BackupTaskStoreResult<BackupFileHealthWorkItem>(BackupTaskStoreResultCode.NotFound);
        }

        var endpoint = await TryCreateEndpointAsync(context, next, requireAccess: true, cancellationToken);

        return new BackupTaskStoreResult<BackupFileHealthWorkItem>(
            BackupTaskStoreResultCode.Succeeded,
            new BackupFileHealthWorkItem(
                next.Id,
                next.TaskId,
                next.Path,
                next.LengthBytes,
                next.RowVersion,
                endpoint));
    }

    public async Task<BackupTaskStoreResult<BackupFileHealthWorkItem>> CommitExternalMissingAsync(
        BackupFileHealthWorkItem work,
        Guid mutationId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        RequireId(mutationId, nameof(mutationId));
        var occurredAt = RequireUtc(occurredAtUtc, nameof(occurredAtUtc));
        try
        {
            return await ExecuteWithStrategyAsync(async context =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
                var replay = await context.BackupFileStateChanges.SingleOrDefaultAsync(
                    change => change.MutationId == mutationId,
                    cancellationToken);
                if (replay is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return replay.FileId == work.FileId && replay.ReasonCode == HealthMissingReason
                        ? new BackupTaskStoreResult<BackupFileHealthWorkItem>(
                            BackupTaskStoreResultCode.AlreadyApplied,
                            work)
                        : new BackupTaskStoreResult<BackupFileHealthWorkItem>(
                            BackupTaskStoreResultCode.StateMismatch);
                }

                var file = await context.BackupFiles.AsTracking().SingleOrDefaultAsync(
                    item => item.Id == work.FileId,
                    cancellationToken);
                if (file is null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new BackupTaskStoreResult<BackupFileHealthWorkItem>(
                        BackupTaskStoreResultCode.NotFound);
                }

                if (file.Status != BackupFileStatus.Available
                    || !file.RowVersion.SequenceEqual(work.RowVersion))
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new BackupTaskStoreResult<BackupFileHealthWorkItem>(
                        BackupTaskStoreResultCode.ConcurrencyConflict);
                }

                file.RecordExternalMissing(occurredAt);
                context.BackupFileStateChanges.Add(new BackupFileStateChange(
                    mutationId,
                    file.Id,
                    file.TaskId,
                    BackupFileStatus.Available,
                    BackupFileStatus.Missing,
                    HealthMissingReason,
                    occurredAt));
                context.AuditRecords.Add(new AuditRecord(
                    null,
                    "backup.file.retention.health",
                    "BackupFile",
                    file.Id.ToString("N"),
                    "succeeded",
                    HealthMissingReason));
                NotificationOutboxWriter.EnqueueExternalMissing(
                    context,
                    mutationId,
                    file.Id,
                    file.TaskId,
                    occurredAt);
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new BackupTaskStoreResult<BackupFileHealthWorkItem>(
                    BackupTaskStoreResultCode.Succeeded,
                    work);
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            return new BackupTaskStoreResult<BackupFileHealthWorkItem>(
                BackupTaskStoreResultCode.ConcurrencyConflict);
        }
    }

    private static async Task<BackupTaskStoreResult<DeletionCandidate>> TryClaimAsync(
        PlatformDbContext context,
        ClaimBackupFileDeletionCommand command,
        CancellationToken cancellationToken)
    {
        var expiredLeases = await context.BackupFiles
            .AsTracking()
            .Where(file => file.Status == BackupFileStatus.DeletePending
                && file.DeletionLeaseExpiresAtUtc <= command.AcquiredAtUtc)
            .OrderBy(file => file.DeletionLeaseExpiresAtUtc)
            .ThenBy(file => file.Id)
            .Take(50)
            .ToListAsync(cancellationToken);
        foreach (var file in expiredLeases)
        {
            var accepted = await TryAcceptAsync(context, file, command, takeover: true, cancellationToken);
            if (accepted is not null) return new(BackupTaskStoreResultCode.Succeeded, accepted);
        }

        var failed = await context.BackupFiles
            .AsTracking()
            .Where(file => file.Status == BackupFileStatus.DeleteFailed
                && file.NextDeletionAttemptAtUtc <= command.AcquiredAtUtc)
            .OrderBy(file => file.NextDeletionAttemptAtUtc)
            .ThenBy(file => file.Id)
            .Take(50)
            .ToListAsync(cancellationToken);
        foreach (var file in failed)
        {
            var accepted = await TryAcceptAsync(context, file, command, takeover: false, cancellationToken);
            if (accepted is not null) return new(BackupTaskStoreResultCode.Succeeded, accepted);
        }

        var available = await context.BackupFiles
            .AsTracking()
            .Where(file => file.Status == BackupFileStatus.Available)
            .OrderBy(file => file.ValidatedAtUtc)
            .ThenBy(file => file.Id)
            .Take(200)
            .ToListAsync(cancellationToken);
        foreach (var file in available.Where(item => item.IsRetentionDue(command.AcquiredAtUtc)))
        {
            var group = await LoadGroupAsync(context, file.RetentionGroup, cancellationToken);
            var candidate = BackupFileRetentionPolicy.SelectDeletionCandidate(group, command.AcquiredAtUtc);
            if (candidate?.Id != file.Id)
            {
                continue;
            }

            var accepted = await TryAcceptAsync(context, candidate, command, takeover: false, cancellationToken);
            if (accepted is not null) return new(BackupTaskStoreResultCode.Succeeded, accepted);
        }

        return new(BackupTaskStoreResultCode.NotFound);
    }

    private static async Task<DeletionCandidate?> TryAcceptAsync(
        PlatformDbContext context,
        BackupFile file,
        ClaimBackupFileDeletionCommand command,
        bool takeover,
        CancellationToken cancellationToken)
    {
        var group = await LoadGroupAsync(context, file.RetentionGroup, cancellationToken);
        if (!BackupFileRetentionPolicy.HasProtectedAvailableCopy(group, file.Id))
        {
            return null;
        }

        var endpoint = await TryCreateEndpointAsync(context, file, requireAccess: true, cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        var fromStatus = file.Status;
        if (takeover)
        {
            file.TakeOverExpiredDeletionLease(
                command.LeaseToken,
                command.LeaseOwner,
                command.AcquiredAtUtc,
                command.ExpiresAtUtc);
        }
        else
        {
            file.ClaimDeletion(
                command.LeaseToken,
                command.LeaseOwner,
                command.AcquiredAtUtc,
                command.ExpiresAtUtc);
        }

        context.BackupFileStateChanges.Add(new BackupFileStateChange(
            command.MutationId,
            file.Id,
            file.TaskId,
            fromStatus,
            BackupFileStatus.DeletePending,
            ClaimedReason,
            command.AcquiredAtUtc));
        context.AuditRecords.Add(new AuditRecord(
            null,
            "backup.file.retention.claim",
            "BackupFile",
            file.Id.ToString("N"),
            "succeeded",
            ClaimedReason));
        return new DeletionCandidate(file, endpoint);
    }

    private static async Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> ReplayClaimAsync(
        PlatformDbContext context,
        BackupFileStateChange replay,
        ClaimBackupFileDeletionCommand command,
        CancellationToken cancellationToken)
    {
        if (replay.ReasonCode != ClaimedReason || replay.ToStatus != BackupFileStatus.DeletePending)
        {
            return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                BackupTaskStoreResultCode.StateMismatch);
        }

        // 重放返回已提交的租约事实；实际删除前仍需刷新租约并重新校验访问配置。
        var loaded = await LoadWorkItemAsync(context, replay.FileId, command.LeaseToken, requireAccess: false, cancellationToken);
        return loaded.Code == BackupTaskStoreResultCode.Succeeded
            ? new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                BackupTaskStoreResultCode.AlreadyApplied,
                loaded.Value)
            : loaded;
    }

    private static async Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> LoadWorkItemAsync(
        PlatformDbContext context,
        Guid fileId,
        Guid leaseToken,
        bool requireAccess,
        CancellationToken cancellationToken)
    {
        var file = await context.BackupFiles.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == fileId,
            cancellationToken);
        if (file is null || file.DeletionLeaseToken != leaseToken)
        {
            return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                BackupTaskStoreResultCode.StateMismatch);
        }

        var endpoint = await TryCreateEndpointAsync(context, file, requireAccess, cancellationToken);
        if (endpoint is null)
        {
            return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
                BackupTaskStoreResultCode.ConfigurationUnavailable);
        }

        return new BackupTaskStoreResult<BackupFileRetentionWorkItem>(
            BackupTaskStoreResultCode.Succeeded,
            CreateDetachedWorkItem(
                file,
                endpoint,
                leaseToken,
                file.DeletionLeaseExpiresAtUtc ?? throw new InvalidOperationException("删除租约缺少到期时间。")));
    }

    private static async Task<List<BackupFile>> LoadGroupAsync(
        PlatformDbContext context,
        BackupFileRetentionGroup group,
        CancellationToken cancellationToken) =>
        await context.BackupFiles
            .AsTracking()
            .Where(file => file.DatabaseId == group.DatabaseId
                && file.Location == group.Location
                && file.StorageTargetId == group.StorageTargetId)
            .ToListAsync(cancellationToken);

    private static async Task<BackupFileEndpointInput?> TryCreateEndpointAsync(
        PlatformDbContext context,
        BackupFile file,
        bool requireAccess,
        CancellationToken cancellationToken)
    {
        BackupFileEndpointInput endpoint;
        bool enabled;
        try
        {
            if (file.Location == BackupFileLocation.Local)
            {
                var server = await context.DatabaseServers.AsNoTracking().SingleOrDefaultAsync(
                    item => item.Id == file.DatabaseServerId,
                    cancellationToken);
                if (server is null || server.StagingAccessProtocol != file.Protocol) return null;
                enabled = server.IsEnabled;
                endpoint = new BackupFileEndpointInput(
                    server.StagingAccessProtocol,
                    server.StagingAccessHost,
                    server.StagingAccessProtocol == FileTransferProtocol.Smb ? null : server.StagingAccessPort,
                    server.StagingAccessBasePath,
                    server.StagingCredentialReferenceId,
                    string.IsNullOrWhiteSpace(server.StagingSftpHostKeyFingerprint) ? null : server.StagingSftpHostKeyFingerprint);
            }
            else
            {
                var target = await context.StorageTargets.AsNoTracking().SingleOrDefaultAsync(
                    item => item.Id == file.StorageTargetId,
                    cancellationToken);
                if (target is null || target.Protocol != file.Protocol) return null;
                enabled = target.IsEnabled;
                endpoint = new BackupFileEndpointInput(
                    target.Protocol,
                    target.Host,
                    target.Protocol == FileTransferProtocol.Smb ? null : target.Port,
                    target.BasePath,
                    target.CredentialReferenceId,
                    string.IsNullOrWhiteSpace(target.SftpHostKeyFingerprint) ? null : target.SftpHostKeyFingerprint);
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (requireAccess && (!enabled || !await context.CredentialReferences.AnyAsync(
            credential => credential.Id == endpoint.CredentialReferenceId
                && credential.IsEnabled
                && (endpoint.Protocol == FileTransferProtocol.Smb
                    ? credential.Kind == CredentialKind.SmbPassword
                    : credential.Kind == CredentialKind.SftpPassword
                        || credential.Kind == CredentialKind.SftpPrivateKey),
            cancellationToken)))
        {
            return null;
        }

        return endpoint;
    }

    private static BackupFileRetentionWorkItem CreateDetachedWorkItem(
        BackupFile file,
        BackupFileEndpointInput endpoint,
        Guid leaseToken,
        DateTimeOffset leaseExpiresAtUtc) =>
        new(
            file.Id,
            file.TaskId,
            file.DatabaseId,
            file.Location,
            file.StorageTargetId,
            file.DatabaseServerId,
            file.Protocol,
            file.Path,
            file.LengthBytes,
            file.Status,
            leaseToken,
            leaseExpiresAtUtc,
            file.RowVersion,
            endpoint);

    private async Task<T> ExecuteWithStrategyAsync<T>(
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

    private static void ValidateClaim(ClaimBackupFileDeletionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireId(command.LeaseToken, nameof(command.LeaseToken));
        RequireId(command.MutationId, nameof(command.MutationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.LeaseOwner);
        if (command.LeaseOwner.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        var acquired = RequireUtc(command.AcquiredAtUtc, nameof(command.AcquiredAtUtc));
        var expires = RequireUtc(command.ExpiresAtUtc, nameof(command.ExpiresAtUtc));
        if (expires <= acquired)
        {
            throw new ArgumentException("删除租约到期时间必须晚于取得时间。", nameof(command));
        }
    }

    private static Guid RequireId(Guid value, string parameterName) => value == Guid.Empty
        ? throw new ArgumentException("标识不能为空。", parameterName)
        : value;

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName) =>
        value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentException("时间必须使用 UTC。", parameterName);

    private static string Reason(BackupFileRetentionOutcome outcome) => outcome switch
    {
        BackupFileRetentionOutcome.Deleted => DeletedReason,
        BackupFileRetentionOutcome.Missing => MissingReason,
        BackupFileRetentionOutcome.Failed => FailedReason,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private sealed record DeletionCandidate(BackupFile File, BackupFileEndpointInput Endpoint);
}
