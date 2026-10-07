using System.Data;
using DbBackupManager.Application.BackupSets;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupSetRegistrationStore(IDbContextFactory<PlatformDbContext> factory)
    : IBackupSetRegistrationStore
{
    private readonly BackupTaskPersistence _persistence = new(factory);

    public Task<BackupTaskStoreResult<BackupSetRegistrationResult>> RegisterAsync(
        RegisterBackupSetCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.BackupSetId == Guid.Empty || command.DatabaseId == Guid.Empty || command.MutationId == Guid.Empty
            || command.ObservedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("登记标识或 UTC 观察时间无效。", nameof(command));
        command.Metadata.Validate();
        command.Assessment.Validate();
        BackupSetCodes.ValidateCompletion(command.Completion);
        // 入口时冻结调用方集合，执行策略重放时使用同一组证据。
        command = command with { Observations = command.Observations.ToArray() };
        foreach (var observation in command.Observations)
        {
            if (!Enum.IsDefined(observation.Source) || !Enum.IsDefined(observation.Kind)
                || observation.Source is BackupSetEvidenceSource.Comparison or BackupSetEvidenceSource.Registration)
                throw new ArgumentException("原始证据不能使用登记或汇总来源。", nameof(command));
            observation.Metadata.Validate();
            observation.Assessment.Validate();
            BackupSetCodes.ValidateCompletion(observation.Completion);
        }
        return _persistence.ExecuteWithStrategyAsync(db => RegisterCoreAsync(db, command, cancellationToken), cancellationToken);
    }

    private static async Task<BackupTaskStoreResult<BackupSetRegistrationResult>> RegisterCoreAsync(
        PlatformDbContext db, RegisterBackupSetCommand command, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        // 锁住任务后校验租约，核对记录与幂等标记在同一短事务提交。
        var task = await LockTaskAsync(db, command.Lease.TaskId, token);
        if (task is null) return new(BackupTaskStoreResultCode.NotFound);
        var snapshot = await db.BackupTaskSnapshots.SingleOrDefaultAsync(x => x.TaskId == task.Id, token);
        if (snapshot is null || snapshot.DatabaseId != command.DatabaseId)
            return new(BackupTaskStoreResultCode.StateMismatch);
        var replay = await db.BackupSetEvidence.AsNoTracking()
            .Where(x => x.TaskId == task.Id && x.AttemptId == command.Lease.BackupAttemptId
                && x.MutationId == command.MutationId).OrderBy(x => x.EntryNumber).ToArrayAsync(token);
        if (replay.Length != 0)
        {
            if (!Matches(replay, command)) return new(BackupTaskStoreResultCode.StateMismatch);
            var result = replay[1];
            return new(BackupTaskStoreResultCode.AlreadyApplied,
                new(result.BackupSetId, result.ReconciliationNumber,
                    result.Assessment.ReasonCode == DifferentialBaselineReason.SourceConflict,
                    result.Assessment));
        }
        var invalidLease = BackupTaskPersistence.ValidateLease(task, command.Lease, DateTimeOffset.UtcNow);
        if (invalidLease is not null) return new(invalidLease.Value);
        var attempt = await db.BackupAttempts.SingleOrDefaultAsync(
            x => x.Id == command.Lease.BackupAttemptId && x.TaskId == task.Id, token);
        if (attempt is null) return new(BackupTaskStoreResultCode.StateMismatch);
        var set = await db.BackupSets.AsTracking().SingleOrDefaultAsync(x => x.AttemptId == attempt.Id, token);
        var observed = (set?.Metadata ?? new()).Merge(command.Metadata, out var sourceConflict);
        foreach (var observation in command.Observations.Where(x => x.Kind == BackupSetEvidenceKind.Backup))
        {
            observed = observed.Merge(observation.Metadata, out var observationConflict);
            sourceConflict |= observationConflict;
        }
        var duplicateGuid = command.Metadata.BackupSetGuid.Value is { } guid
            && await db.BackupSets.AnyAsync(x => x.Metadata.BackupSetGuid.Value == guid && x.AttemptId != attempt.Id, token);
        var conflict = duplicateGuid;
        if (command.BaseBackupSetId is { } baseId)
        {
            var full = await db.BackupSets.SingleOrDefaultAsync(
                x => x.Id == baseId && x.DatabaseId == command.DatabaseId, token);
            if (full is null || !DependencyMatches(command.Metadata, full))
                return new(BackupTaskStoreResultCode.StateMismatch);
        }
        if (set is null && !duplicateGuid)
        {
            set = new(command.BackupSetId, task.Id, attempt.Id, command.DatabaseId,
                sourceConflict ? new() : command.Metadata, command.Completion, command.Assessment,
                sourceConflict ? null : command.BaseBackupSetId,
                command.SqlSuccessObserved);
            db.BackupSets.Add(set);
        }
        if (set is not null)
        {
            if (duplicateGuid)
            {
                // 不把其他 Attempt 的已确认逻辑身份写入当前备份集。
                set.Reconcile(set.Metadata, command.Completion,
                    new(BackupMetadataState.Known, DifferentialBaselineConclusion.Mismatch,
                        DifferentialBaselineReason.SourceConflict),
                    null, command.SqlSuccessObserved);
                set.MarkMetadataConflict();
            }
            else
                conflict = set.Reconcile(command.Metadata, command.Completion, command.Assessment,
                    command.BaseBackupSetId, command.SqlSuccessObserved, sourceConflict);
            if (conflict) set.MarkMetadataConflict();
            conflict |= set.HasMetadataConflict;
        }
        var check = (await db.BackupSetEvidence.Where(x => x.AttemptId == attempt.Id)
            .Select(x => (int?)x.ReconciliationNumber).MaxAsync(token) ?? 0) + 1;
        var assessment = conflict
            ? new BackupSetAssessment(BackupMetadataState.Known, DifferentialBaselineConclusion.Mismatch,
                DifferentialBaselineReason.SourceConflict)
            : set!.Assessment;
        var setId = set?.Id;
        AddEvidence(db, command, setId, check, 0, BackupSetEvidenceSource.Registration,
            BackupSetEvidenceKind.Backup, command.Metadata, command.Completion, command.Assessment);
        AddEvidence(db, command, setId, check, 1, BackupSetEvidenceSource.Comparison,
            BackupSetEvidenceKind.Backup, command.Metadata, command.Completion, assessment);
        for (var i = 0; i < command.Observations.Count; i++)
        {
            var observation = command.Observations[i];
            AddEvidence(db, command, setId, check, i + 2, observation.Source,
                observation.Kind, observation.Metadata, observation.Completion, observation.Assessment);
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(BackupTaskStoreResultCode.Succeeded,
            new(setId, check, assessment.ReasonCode == DifferentialBaselineReason.SourceConflict, assessment));
    }

    private static void AddEvidence(PlatformDbContext db, RegisterBackupSetCommand command, Guid? setId,
        int check, int entry, BackupSetEvidenceSource source, BackupSetEvidenceKind kind,
        BackupSetMetadata metadata, BackupCompletionTimeDecision completion, BackupSetAssessment assessment) =>
        db.BackupSetEvidence.Add(new(Guid.NewGuid(), command.Lease.TaskId, command.Lease.BackupAttemptId,
            setId, command.MutationId, check, entry, source, kind, metadata, completion, assessment,
            command.ObservedAtUtc, command.SqlSuccessObserved, command.BaseBackupSetId));

    private static bool Matches(BackupSetEvidence[] rows, RegisterBackupSetCommand command)
    {
        if (rows.Length != command.Observations.Count + 2) return false;
        var request = rows[0];
        if (request.Source != BackupSetEvidenceSource.Registration || request.EntryNumber != 0
            || request.Metadata != command.Metadata || request.Completion != command.Completion
            || request.Assessment != command.Assessment || request.SqlSuccessObserved != command.SqlSuccessObserved
            || request.RequestedBaseBackupSetId != command.BaseBackupSetId
            || request.ObservedAtUtc != command.ObservedAtUtc) return false;
        for (var i = 0; i < command.Observations.Count; i++)
        {
            var row = rows[i + 2];
            var input = command.Observations[i];
            if (row.Source != input.Source || row.Kind != input.Kind || row.Metadata != input.Metadata
                || row.Completion != input.Completion || row.Assessment != input.Assessment) return false;
        }
        return true;
    }

    private static bool DependencyMatches(BackupSetMetadata diff, BackupSet full)
    {
        var decision = DifferentialBaselineRules.EvaluateDependency(
            new(diff.DatabaseGuid.Value, diff.FamilyGuid.Value),
            new(new(new(diff.DatabaseGuid.Value, diff.FamilyGuid.Value),
                new(diff.FirstRecoveryForkId.Value, diff.RecoveryForkId.Value),
                diff.DifferentialBaseGuid.Value, diff.DifferentialBaseLsn.Value),
                diff.Type.Value, diff.IsCopyOnly.Value, diff.DatabaseBackupLsn.Value),
            [new(full.Id, new(full.Metadata.BackupSetGuid.Value,
                new(full.Metadata.DatabaseGuid.Value, full.Metadata.FamilyGuid.Value),
                new(full.Metadata.FirstRecoveryForkId.Value, full.Metadata.RecoveryForkId.Value),
                full.Metadata.Type.Value, full.Metadata.IsCopyOnly.Value, full.Metadata.CheckpointLsn.Value,
                full.HasMetadataConflict ? BaselineEvidenceStatus.SourceConflict : BaselineEvidenceStatus.Complete))]);
        return decision.Conclusion == DifferentialBaselineConclusion.Verified;
    }

    public Task<BackupTaskStoreResult<BackupFile>> RegisterCopyAsync(
        LeaseHandle lease, BackupFile file, CancellationToken cancellationToken = default)
    {
        if (file.BackupSetId is null || file.TaskId != lease.TaskId || file.AttemptId != lease.BackupAttemptId)
            throw new ArgumentException("副本必须关联同一任务、Attempt 的备份集。", nameof(file));
        return _persistence.ExecuteWithStrategyAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var task = await LockTaskAsync(db, lease.TaskId, cancellationToken);
            if (task is null) return new BackupTaskStoreResult<BackupFile>(BackupTaskStoreResultCode.NotFound);
            var set = await db.BackupSets.SingleOrDefaultAsync(x => x.Id == file.BackupSetId
                && x.TaskId == file.TaskId && x.AttemptId == file.AttemptId && x.DatabaseId == file.DatabaseId, cancellationToken);
            var attempt = await db.BackupAttempts.SingleOrDefaultAsync(x => x.Id == file.AttemptId, cancellationToken);
            var snapshot = await db.BackupTaskSnapshots.SingleOrDefaultAsync(x => x.TaskId == file.TaskId, cancellationToken);
            if (set is null || attempt is null || snapshot is null || !CopyMatchesAttempt(file, attempt, snapshot))
                return new(BackupTaskStoreResultCode.StateMismatch);
            var existing = await db.BackupFiles.SingleOrDefaultAsync(x => x.AttemptId == file.AttemptId
                && x.Location == file.Location, cancellationToken);
            if (existing is not null)
                return new(existing.BackupSetId == file.BackupSetId && existing.Path == file.Path
                    && existing.LengthBytes == file.LengthBytes && existing.ValidatedAtUtc == file.ValidatedAtUtc
                    && existing.RetentionDays == file.RetentionDays
                    ? BackupTaskStoreResultCode.AlreadyApplied : BackupTaskStoreResultCode.StateMismatch, existing);
            var invalidLease = BackupTaskPersistence.ValidateLease(task, lease, DateTimeOffset.UtcNow);
            if (invalidLease is not null) return new(invalidLease.Value);
            db.BackupFiles.Add(file);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(BackupTaskStoreResultCode.Succeeded, file);
        }, cancellationToken);
    }

    private static bool CopyMatchesAttempt(BackupFile file, BackupAttempt attempt, BackupTaskSnapshot snapshot)
    {
        var local = file.Location == BackupFileLocation.Local;
        var expectedPath = local ? attempt.WorkerSourceFilePath : attempt.RemoteFinalFilePath;
        var comparison = file.Protocol == FileTransferProtocol.Smb ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(file.Path, expectedPath, comparison)
            && file.Protocol == (local ? snapshot.SourceAccessProtocol : snapshot.RemoteProtocol)
            && file.DatabaseServerId == (local ? snapshot.ServerId : (Guid?)null)
            && file.StorageTargetId == (local ? (Guid?)null : snapshot.StorageTargetId)
            && file.RetentionDays == (local ? snapshot.LocalRetentionDays : snapshot.RemoteRetentionDays)
            && (attempt.SourceLengthBytes is null || file.LengthBytes == attempt.SourceLengthBytes);
    }

    private static Task<BackupTask?> LockTaskAsync(PlatformDbContext db, Guid taskId, CancellationToken token) =>
        db.BackupTasks.FromSqlInterpolated(
            $"SELECT * FROM [BackupTasks] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {taskId}")
            .AsTracking().SingleOrDefaultAsync(token);
}
