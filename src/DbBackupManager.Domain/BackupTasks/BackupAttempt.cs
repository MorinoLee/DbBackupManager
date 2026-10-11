using DbBackupManager.Domain.Entities;

namespace DbBackupManager.Domain.BackupTasks;

public sealed record BackupAttemptPaths(
    string LocalSqlFilePath,
    string WorkerSourceFilePath,
    Guid? RemoteStorageTargetId,
    string? RemotePartialFilePath,
    string? RemoteFinalFilePath);

public sealed class BackupAttempt : ConcurrentEntity
{
    private BackupAttempt()
    {
    }

    public BackupAttempt(
        Guid id,
        Guid taskId,
        int attemptNumber,
        DateTimeOffset preparedAtUtc,
        BackupAttemptPaths paths)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(paths);
        TaskId = BackupTaskValues.RequireId(taskId, nameof(taskId));
        AttemptNumber = BackupTaskValues.RequirePositive(attemptNumber, int.MaxValue, nameof(attemptNumber));
        PreparedAtUtc = BackupTaskValues.RequireUtc(preparedAtUtc, nameof(preparedAtUtc));
        var validatedPaths = ValidatePaths(id, paths);

        LocalSqlFilePath = validatedPaths.LocalSql;
        WorkerSourceFilePath = validatedPaths.WorkerSource;
        RemoteStorageTargetId = validatedPaths.RemoteTargetId;
        RemotePartialFilePath = validatedPaths.RemotePartial;
        RemoteFinalFilePath = validatedPaths.RemoteFinal;
        BackupInvocationStatus = BackupInvocationStatus.Prepared;
    }

    public Guid TaskId { get; private set; }

    public int AttemptNumber { get; private set; }

    public BackupInvocationStatus BackupInvocationStatus { get; private set; }

    public DateTimeOffset PreparedAtUtc { get; private set; }

    public DateTimeOffset? BackupStartedAtUtc { get; private set; }

    public DateTimeOffset? BackupFinishedAtUtc { get; private set; }

    public string LocalSqlFilePath { get; private set; } = string.Empty;

    public string WorkerSourceFilePath { get; private set; } = string.Empty;

    public Guid? RemoteStorageTargetId { get; private set; }

    public string? RemotePartialFilePath { get; private set; }

    public string? RemoteFinalFilePath { get; private set; }

    public long? SourceLengthBytes { get; private set; }

    public DateTimeOffset? LocalVerifiedAtUtc { get; private set; }

    public DateTimeOffset? RemoteValidatedAtUtc { get; private set; }

    public DateTimeOffset? LocalCleanupCompletedAtUtc { get; private set; }

    public string? OutcomeCode { get; private set; }

    public Guid? ExpectedDatabaseGuid { get; private set; }
    public Guid? ExpectedFamilyGuid { get; private set; }
    public Guid? AdmittedFullBackupSetId { get; private set; }
    public Guid? AdmissionRecoveryForkId { get; private set; }
    public DateTimeOffset? AdmissionObservedAtUtc { get; private set; }

    public void BindSqlIdentity(Guid databaseGuid, Guid familyGuid)
    {
        BackupTaskValues.RequireId(databaseGuid, nameof(databaseGuid));
        BackupTaskValues.RequireId(familyGuid, nameof(familyGuid));
        if (ExpectedDatabaseGuid == databaseGuid && ExpectedFamilyGuid == familyGuid) return;
        if (ExpectedDatabaseGuid is not null || ExpectedFamilyGuid is not null
            || BackupInvocationStatus != BackupInvocationStatus.Prepared)
            throw new InvalidOperationException("SQL 身份只能在调用前固定，不能反填或覆盖。");
        ExpectedDatabaseGuid = databaseGuid;
        ExpectedFamilyGuid = familyGuid;
    }

    public void AdmitDifferential(Guid fullBackupSetId, Guid recoveryForkId, DateTimeOffset observedAtUtc)
    {
        BackupTaskValues.RequireId(fullBackupSetId, nameof(fullBackupSetId));
        BackupTaskValues.RequireId(recoveryForkId, nameof(recoveryForkId));
        BackupTaskValues.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (AdmittedFullBackupSetId == fullBackupSetId && AdmissionRecoveryForkId == recoveryForkId
            && AdmissionObservedAtUtc == observedAtUtc) return;
        if (BackupInvocationStatus != BackupInvocationStatus.Prepared || ExpectedDatabaseGuid is null
            || AdmittedFullBackupSetId is not null)
            throw new InvalidOperationException("差异准入只能在身份固定且尚未调用时写入一次。");
        AdmittedFullBackupSetId = fullBackupSetId;
        AdmissionRecoveryForkId = recoveryForkId;
        AdmissionObservedAtUtc = observedAtUtc;
    }

    public void MarkBackupRunning(DateTimeOffset startedAtUtc)
    {
        if (BackupInvocationStatus != BackupInvocationStatus.Prepared)
        {
            throw new InvalidOperationException("只有 Prepared Attempt 可以标记 Backup 已开始。");
        }

        var startedAt = BackupTaskValues.RequireUtc(startedAtUtc, nameof(startedAtUtc));
        if (startedAt < PreparedAtUtc)
        {
            throw new ArgumentException("Backup 开始时间不能早于 Attempt 准备时间。", nameof(startedAtUtc));
        }

        BackupStartedAtUtc = startedAt;
        BackupInvocationStatus = BackupInvocationStatus.Running;
    }

    public void RecordBackupSucceeded(DateTimeOffset finishedAtUtc)
    {
        var finishedAt = ValidateFinishedAt(finishedAtUtc);
        BackupInvocationStatus = BackupInvocationStatus.Succeeded;
        BackupFinishedAtUtc = finishedAt;
        OutcomeCode = null;
    }

    public void RecordBackupConfirmedFailed(DateTimeOffset finishedAtUtc, string outcomeCode)
    {
        var finishedAt = ValidateFinishedAt(finishedAtUtc);
        var code = BackupTaskValues.RequireText(outcomeCode, 100, nameof(outcomeCode));

        BackupInvocationStatus = BackupInvocationStatus.ConfirmedFailed;
        BackupFinishedAtUtc = finishedAt;
        OutcomeCode = code;
    }

    public void RecordBackupIndeterminate(DateTimeOffset? observedAtUtc, string outcomeCode)
    {
        if (BackupInvocationStatus != BackupInvocationStatus.Running)
        {
            throw new InvalidOperationException("只有 Running Attempt 可以记录结果不确定。");
        }

        var observedAt = BackupTaskValues.OptionalUtc(observedAtUtc, nameof(observedAtUtc));
        if (observedAt is not null && observedAt < BackupStartedAtUtc)
        {
            throw new ArgumentException("结果观察时间不能早于 Backup 开始时间。", nameof(observedAtUtc));
        }

        var code = BackupTaskValues.RequireText(outcomeCode, 100, nameof(outcomeCode));
        BackupInvocationStatus = BackupInvocationStatus.Indeterminate;
        BackupFinishedAtUtc = observedAt;
        OutcomeCode = code;
    }

    public void ReconcileBackupSucceeded(DateTimeOffset finishedAtUtc)
    {
        ReconcileBackupResult(finishedAtUtc, BackupInvocationStatus.Succeeded, null);
    }

    public void ReconcileBackupConfirmedFailed(DateTimeOffset finishedAtUtc, string outcomeCode)
    {
        var code = BackupTaskValues.RequireText(outcomeCode, 100, nameof(outcomeCode));
        ReconcileBackupResult(finishedAtUtc, BackupInvocationStatus.ConfirmedFailed, code);
    }

    public void RecordLocalVerification(long sourceLengthBytes, DateTimeOffset verifiedAtUtc)
    {
        if (BackupInvocationStatus != BackupInvocationStatus.Succeeded)
        {
            throw new InvalidOperationException("只有明确成功的 Backup 才能记录本地校验证据。");
        }

        var length = BackupTaskValues.RequirePositive(sourceLengthBytes, nameof(sourceLengthBytes));
        var verifiedAt = BackupTaskValues.RequireUtc(verifiedAtUtc, nameof(verifiedAtUtc));
        if (verifiedAt < BackupFinishedAtUtc)
        {
            throw new ArgumentException("本地校验时间不能早于 Backup 完成时间。", nameof(verifiedAtUtc));
        }

        if (SourceLengthBytes is not null || LocalVerifiedAtUtc is not null)
        {
            if (SourceLengthBytes == length && LocalVerifiedAtUtc == verifiedAt)
            {
                return;
            }

            throw new InvalidOperationException("本地校验证据已经存在且与新值冲突。");
        }

        SourceLengthBytes = length;
        LocalVerifiedAtUtc = verifiedAt;
    }

    public void RecordRemoteValidated(DateTimeOffset validatedAtUtc)
    {
        EnsureRemotePaths();
        if (SourceLengthBytes is null || LocalVerifiedAtUtc is null)
        {
            throw new InvalidOperationException("远程验证前必须先保存本地校验和源文件长度。");
        }

        var validatedAt = BackupTaskValues.RequireUtc(validatedAtUtc, nameof(validatedAtUtc));
        if (validatedAt < LocalVerifiedAtUtc)
        {
            throw new ArgumentException("远程验证时间不能早于本地校验时间。", nameof(validatedAtUtc));
        }

        if (RemoteValidatedAtUtc is not null)
        {
            if (RemoteValidatedAtUtc == validatedAt)
            {
                return;
            }

            throw new InvalidOperationException("远程验证证据已经存在且与新值冲突。");
        }

        RemoteValidatedAtUtc = validatedAt;
    }

    public void RecordLocalCleanupCompleted(DateTimeOffset completedAtUtc)
    {
        EnsureRemotePaths();
        if (RemoteValidatedAtUtc is null)
        {
            throw new InvalidOperationException("本地清理前必须确认远程副本已经验证。");
        }

        var completedAt = BackupTaskValues.RequireUtc(completedAtUtc, nameof(completedAtUtc));
        if (completedAt < RemoteValidatedAtUtc)
        {
            throw new ArgumentException("本地清理时间不能早于远程验证时间。", nameof(completedAtUtc));
        }

        if (LocalCleanupCompletedAtUtc is not null)
        {
            if (LocalCleanupCompletedAtUtc == completedAt)
            {
                return;
            }

            throw new InvalidOperationException("本地清理证据已经存在且与新值冲突。");
        }

        LocalCleanupCompletedAtUtc = completedAt;
    }

    private DateTimeOffset ValidateFinishedAt(DateTimeOffset finishedAtUtc)
    {
        if (BackupInvocationStatus != BackupInvocationStatus.Running
            || BackupStartedAtUtc is null)
        {
            throw new InvalidOperationException("只有 Running Attempt 可以记录 Backup 结果。");
        }

        var finishedAt = BackupTaskValues.RequireUtc(finishedAtUtc, nameof(finishedAtUtc));
        if (finishedAt < BackupStartedAtUtc)
        {
            throw new ArgumentException("Backup 完成时间不能早于开始时间。", nameof(finishedAtUtc));
        }

        return finishedAt;
    }

    private void ReconcileBackupResult(
        DateTimeOffset finishedAtUtc,
        BackupInvocationStatus targetStatus,
        string? outcomeCode)
    {
        if (BackupInvocationStatus is not (BackupInvocationStatus.Running
            or BackupInvocationStatus.Indeterminate)
            || BackupStartedAtUtc is null)
        {
            throw new InvalidOperationException("只有已开始且尚未明确结束的 Backup Attempt 可以核对结果。");
        }

        var finishedAt = BackupTaskValues.RequireUtc(finishedAtUtc, nameof(finishedAtUtc));
        if (finishedAt < BackupStartedAtUtc)
        {
            throw new ArgumentException("核对完成时间不能早于 Backup 开始时间。", nameof(finishedAtUtc));
        }

        BackupInvocationStatus = targetStatus;
        BackupFinishedAtUtc ??= finishedAt;
        OutcomeCode = outcomeCode;
    }

    private void EnsureRemotePaths()
    {
        if (RemoteStorageTargetId is null
            || RemotePartialFilePath is null
            || RemoteFinalFilePath is null)
        {
            throw new InvalidOperationException("当前 Attempt 没有远程目标和固定路径。");
        }
    }

    private static (
        string LocalSql,
        string WorkerSource,
        Guid? RemoteTargetId,
        string? RemotePartial,
        string? RemoteFinal) ValidatePaths(
        Guid attemptId,
        BackupAttemptPaths paths)
    {
        var localSql = RequireAttemptPath(paths.LocalSqlFilePath, attemptId, nameof(paths));
        var workerSource = RequireAttemptPath(paths.WorkerSourceFilePath, attemptId, nameof(paths));
        var hasAnyRemote = paths.RemoteStorageTargetId is not null
            || paths.RemotePartialFilePath is not null
            || paths.RemoteFinalFilePath is not null;
        var hasAllRemote = paths.RemoteStorageTargetId is not null
            && paths.RemotePartialFilePath is not null
            && paths.RemoteFinalFilePath is not null;

        if (hasAnyRemote && !hasAllRemote)
        {
            throw new ArgumentException("远程目标、临时路径和最终路径必须同时提供。", nameof(paths));
        }

        if (!hasAllRemote)
        {
            return (localSql, workerSource, null, null, null);
        }

        var targetId = BackupTaskValues.RequireId(
            paths.RemoteStorageTargetId!.Value,
            nameof(paths.RemoteStorageTargetId));
        var partial = RequireAttemptPath(paths.RemotePartialFilePath!, attemptId, nameof(paths));
        var final = RequireAttemptPath(paths.RemoteFinalFilePath!, attemptId, nameof(paths));
        if (!partial.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("远程临时路径必须使用 .part 后缀。", nameof(paths));
        }

        if (final.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            || string.Equals(partial, final, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("远程最终路径不能是 .part 或与临时路径相同。", nameof(paths));
        }

        return (localSql, workerSource, targetId, partial, final);
    }

    private static string RequireAttemptPath(string value, Guid attemptId, string parameterName)
    {
        var path = BackupTaskValues.RequireText(value, 2048, parameterName);
        var containsAttemptId = path.Contains(
            attemptId.ToString("N"),
            StringComparison.OrdinalIgnoreCase)
            || path.Contains(attemptId.ToString("D"), StringComparison.OrdinalIgnoreCase);

        if (!containsAttemptId)
        {
            throw new ArgumentException("Attempt 路径必须包含当前 Attempt 标识。", parameterName);
        }

        return path;
    }
}
