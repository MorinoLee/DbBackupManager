using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupTasks;

namespace DbBackupManager.Application.BackupTasks;

public sealed record BackupFileRetentionOptions(TimeSpan IdleInterval, TimeSpan LeaseDuration, int DeleteTimeoutSeconds)
{
    public static BackupFileRetentionOptions Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(60),
        60);

    public void Validate()
    {
        if (IdleInterval <= TimeSpan.Zero
            || LeaseDuration < IdleInterval * 3
            || DeleteTimeoutSeconds is < 1 or > 86_400)
        {
            throw new InvalidOperationException("Retention 空闲间隔、删除租约或超时配置无效。");
        }
    }
}

public sealed class BackupFileRetentionRunner(
    IBackupFileRetentionStore store,
    IBackupFileStorageProbe probe,
    IBackupFileDeletionExecutor deletion,
    TimeProvider clock,
    BackupFileRetentionOptions options)
{
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private Guid? _healthCursor;

    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        options.Validate();
        var now = clock.GetUtcNow();
        var claimed = await store.ClaimNextAsync(
            new ClaimBackupFileDeletionCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                _owner,
                now,
                now + options.LeaseDuration),
            cancellationToken);
        if (claimed.Code == BackupTaskStoreResultCode.Succeeded && claimed.Value is not null)
        {
            await ProcessDeletionAsync(claimed.Value, cancellationToken);
            return true;
        }

        await InspectHealthOnceAsync(now, cancellationToken);
        return false;
    }

    public Task ProcessClaimedAsync(
        BackupFileRetentionWorkItem claimed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claimed);
        options.Validate();
        return ProcessDeletionAsync(claimed, cancellationToken);
    }

    private async Task ProcessDeletionAsync(
        BackupFileRetentionWorkItem claimed,
        CancellationToken cancellationToken)
    {
        var refreshed = await store.RefreshWorkItemAsync(
            claimed.FileId,
            claimed.LeaseToken,
            clock.GetUtcNow(),
            cancellationToken);
        if (refreshed.Code is BackupTaskStoreResultCode.LeaseLost or BackupTaskStoreResultCode.NotFound)
        {
            return;
        }

        var work = refreshed.Value ?? claimed;
        if (refreshed.Code == BackupTaskStoreResultCode.ConfigurationUnavailable)
        {
            await CommitAsync(work, BackupFileRetentionOutcome.Failed, "retention_target_disabled", cancellationToken);
            return;
        }

        if (refreshed.Code == BackupTaskStoreResultCode.StateMismatch)
        {
            await CommitAsync(work, BackupFileRetentionOutcome.Failed, "retention_last_copy_protected", cancellationToken);
            return;
        }

        if (refreshed.Code != BackupTaskStoreResultCode.Succeeded)
        {
            return;
        }

        BackupFileStorageResult<BackupFileMetadata> inspect;
        try
        {
            inspect = await probe.InspectAsync(work.Endpoint, work.Path, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (IsMissing(inspect))
        {
            await CommitAsync(work, BackupFileRetentionOutcome.Missing, null, cancellationToken);
            return;
        }

        if (!inspect.IsSucceeded)
        {
            await CommitAsync(work, BackupFileRetentionOutcome.Failed, Map(inspect.Failure!), cancellationToken);
            return;
        }

        var metadata = inspect.Value!;
        if (!metadata.IsRegularFile || metadata.LengthBytes != work.LengthBytes)
        {
            await CommitAsync(
                work,
                BackupFileRetentionOutcome.Failed,
                metadata.IsRegularFile ? "file.length_mismatch" : "file.not_regular",
                cancellationToken);
            return;
        }

        BackupFileStorageResult<BackupFileMutationReceipt> deleted;
        try
        {
            deleted = await deletion.DeleteAsync(
                new BackupFileDeleteRequest(
                    work.Endpoint,
                    work.Path,
                    work.LengthBytes,
                    metadata.Identity,
                    options.DeleteTimeoutSeconds),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (deleted.IsSucceeded)
        {
            await CommitAsync(work, BackupFileRetentionOutcome.Deleted, null, cancellationToken);
            return;
        }

        if (deleted.Failure!.Code == BackupFileStorageFailureCode.FileNotFound)
        {
            await CommitAsync(work, BackupFileRetentionOutcome.Missing, null, cancellationToken);
            return;
        }

        if (deleted.Outcome != BackupFileStorageOutcome.Indeterminate)
        {
            await CommitAsync(work, BackupFileRetentionOutcome.Failed, Map(deleted.Failure), cancellationToken);
            return;
        }

        BackupFileStorageResult<BackupFileMetadata> recov;
        try
        {
            recov = await probe.InspectAsync(work.Endpoint, work.Path, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (IsMissing(recov))
        {
            await CommitAsync(work, BackupFileRetentionOutcome.Deleted, null, cancellationToken);
            return;
        }

        await CommitAsync(work, BackupFileRetentionOutcome.Failed, Map(deleted.Failure), cancellationToken);
    }

    private async Task InspectHealthOnceAsync(DateTimeOffset utcNow, CancellationToken cancellationToken)
    {
        var found = await store.FindNextHealthInspectionAsync(_healthCursor, utcNow, cancellationToken);
        if (found.Code != BackupTaskStoreResultCode.Succeeded || found.Value is null)
        {
            _healthCursor = null;
            return;
        }

        var work = found.Value;
        _healthCursor = work.FileId;
        if (work.Endpoint is null)
        {
            return;
        }

        BackupFileStorageResult<BackupFileMetadata> inspect;
        try
        {
            inspect = await probe.InspectAsync(work.Endpoint, work.Path, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!IsMissing(inspect))
        {
            return;
        }

        await store.CommitExternalMissingAsync(work, Guid.NewGuid(), clock.GetUtcNow(), cancellationToken);
    }

    private Task<BackupTaskStoreResult<BackupFileRetentionWorkItem>> CommitAsync(
        BackupFileRetentionWorkItem work,
        BackupFileRetentionOutcome outcome,
        string? errorCode,
        CancellationToken cancellationToken) =>
        store.CommitAsync(
            work,
            new BackupFileRetentionCommitCommand(
                Guid.NewGuid(),
                outcome,
                clock.GetUtcNow(),
                errorCode),
            cancellationToken);

    private static bool IsMissing(BackupFileStorageResult<BackupFileMetadata> result) =>
        result.IsSucceeded && result.Value is { Exists: false }
        || result.Failure?.Code == BackupFileStorageFailureCode.FileNotFound;

    private static string Map(BackupFileStorageFailure failure) => failure.Code switch
    {
        BackupFileStorageFailureCode.AuthorizationDenied => "file.delete_access_denied",
        BackupFileStorageFailureCode.AuthenticationFailed => "file.delete_authentication_failed",
        BackupFileStorageFailureCode.CredentialUnavailable
            or BackupFileStorageFailureCode.CredentialProtectionUnavailable
            or BackupFileStorageFailureCode.CredentialInvalid => "file.credential_unavailable",
        BackupFileStorageFailureCode.HostKeyMismatch => "file.host_key_mismatch",
        BackupFileStorageFailureCode.PathRejected => "file.path_rejected",
        BackupFileStorageFailureCode.LengthMismatch => "file.length_mismatch",
        BackupFileStorageFailureCode.NotRegularFile => "file.not_regular",
        BackupFileStorageFailureCode.SourceChanged => "file.source_changed",
        BackupFileStorageFailureCode.FileAlreadyExists => "file.conflict",
        BackupFileStorageFailureCode.PlatformUnsupported => "file.platform_unsupported",
        BackupFileStorageFailureCode.ConnectionFailed
            or BackupFileStorageFailureCode.ConnectionInterrupted
            or BackupFileStorageFailureCode.TimedOut
            or BackupFileStorageFailureCode.Cancelled
            or BackupFileStorageFailureCode.InvalidResponse
            or BackupFileStorageFailureCode.OperationRejected => "file.delete_indeterminate",
        _ => "file.delete_failed",
    };
}
