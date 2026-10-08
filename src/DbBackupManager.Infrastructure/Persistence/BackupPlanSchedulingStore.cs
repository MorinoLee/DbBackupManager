using System.Data;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupPlanSchedulingStore(IDbContextFactory<PlatformDbContext> factory) : IBackupPlanSchedulingStore
{
    private readonly BackupTaskPersistence _persistence = new(factory);

    public async Task<IReadOnlyList<Guid>> ReadPlanIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.BackupPlans.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(cancellationToken);
    }

    public Task<BackupPlanSchedulingResult> ScheduleAsync(Guid planId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (planId == Guid.Empty || nowUtc.Offset != TimeSpan.Zero) throw new ArgumentException("计划标识或 UTC 时刻无效。");
        return _persistence.ExecuteWithStrategyAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            // 与手动入队及计划修改共用父行锁。事实的串行化读锁保持到提交，重试必须重新选择。
            var plan = await db.BackupPlans.FromSqlInterpolated(
                    $"SELECT * FROM [BackupPlans] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {planId}")
                .Include(x => x.Versions).SingleOrDefaultAsync(cancellationToken);
            if (plan is null) return Result(BackupPlanSchedulingCode.NotFound);
            if (plan.IsPaused) return Result(BackupPlanSchedulingCode.Paused);
            var existing = await ReadDispositionsAsync(db, plan, nowUtc, cancellationToken);
            var selection = BackupPlanScheduleSelectionRules.Select(plan, nowUtc, existing);
            if (selection.Status == BackupScheduleCalculationStatus.InvalidTimeZone) return Result(BackupPlanSchedulingCode.InvalidTimeZone);
            if (selection.Work.Count == 0) return Result(BackupPlanSchedulingCode.NoWork);
            var created = new List<CreatedBackupPlanTask>();
            foreach (var work in selection.Work)
            {
                var request = new CreateBackupPlanTaskRequest(Guid.NewGuid(), Guid.NewGuid(), planId, work.Key.PlanVersionId,
                    work.Key.BackupType == BackupType.Full ? BackupRunPurpose.PlanFull : BackupRunPurpose.PlanDifferential,
                    work.Key.SlotUtc, work.DifferentialCoveredWhenFullSucceeds?.SlotUtc);
                var result = await BackupPlanTaskCreationCore.CreateAsync(db, plan,
                    new(request, BackupTaskTriggerType.Scheduled, nowUtc), resolveOnly: false, cancellationToken);
                // 中途配置无效时回滚整个计划；不会留下只有 FULL 的半份组合决定。
                if (result.Code == BackupPlanTaskCreationCode.ConfigurationUnavailable)
                    return Result(BackupPlanSchedulingCode.ConfigurationUnavailable);
                if (result.Code != BackupPlanTaskCreationCode.Created || result.Task is null)
                    throw new InvalidOperationException("锁定计划后的调度选择与创建校验不一致。");
                created.Add(result.Task);
            }
            await transaction.CommitAsync(cancellationToken);
            return new BackupPlanSchedulingResult(planId, BackupPlanSchedulingCode.Created, created);

            BackupPlanSchedulingResult Result(BackupPlanSchedulingCode code) => new(planId, code, []);
        }, cancellationToken);
    }

    private static async Task<IReadOnlyDictionary<BackupScheduleSlotKey, BackupSlotDisposition>> ReadDispositionsAsync(
        PlatformDbContext db, BackupPlan plan, DateTimeOffset nowUtc, CancellationToken token)
    {
        // 保留所有历史 FULL，而非只读最近一次；手动任务不构造定时时隙。
        var tasks = await db.BackupTasks.Where(x => x.PlanId == plan.Id && x.TriggerType == BackupTaskTriggerType.Scheduled
            && x.ScheduledSlotAtUtc != null && x.ScheduledSlotAtUtc <= nowUtc).ToArrayAsync(token);
        var ids = tasks.Select(x => x.Id).ToArray();
        var attempts = await db.BackupAttempts.Where(x => ids.Contains(x.TaskId)).ToArrayAsync(token);
        var sets = await db.BackupSets.Where(x => ids.Contains(x.TaskId)).ToArrayAsync(token);
        var snapshots = await db.BackupTaskSnapshots.Where(x => ids.Contains(x.TaskId)).ToArrayAsync(token);
        var evidence = await db.BackupSetEvidence.Where(x => ids.Contains(x.TaskId) && x.Kind == BackupSetEvidenceKind.Backup).ToArrayAsync(token);
        // 无 Attempt 只有明确的认领前准备失败历史才能证明未执行；最终 Failed 本身不能证明。
        var rejectedBeforeBackup = (await db.BackupTaskStateChanges.Where(x => ids.Contains(x.TaskId)
                && x.ReasonCode == "execution.preparation_failed" && x.FromStatus == BackupTaskStatus.Pending
                && x.FromStage == BackupTaskStage.Backup && x.ToStatus == BackupTaskStatus.Failed
                && x.ToStage == BackupTaskStage.Backup && x.BackupAttemptId == null)
            .Select(x => x.TaskId).ToArrayAsync(token)).ToHashSet();
        var attemptsByTask = attempts.ToLookup(x => x.TaskId);
        var setsByAttempt = sets.ToDictionary(x => x.AttemptId);
        var snapshotsByTask = snapshots.ToDictionary(x => x.TaskId);
        var evidenceByAttempt = evidence.ToLookup(x => x.AttemptId);
        var dispositions = new Dictionary<BackupScheduleSlotKey, BackupSlotDisposition>();
        foreach (var task in tasks)
        {
            snapshotsByTask.TryGetValue(task.Id, out var snapshot);
            var facts = new List<BackupPlanAttemptFacts>();
            foreach (var attempt in attemptsByTask[task.Id])
            {
                setsByAttempt.TryGetValue(attempt.Id, out var set);
                var rows = evidenceByAttempt[attempt.Id].ToArray();
                var metadataPassed = snapshot is not null && !snapshot.UseCopyOnly
                    && snapshot.Purpose == (task.BackupType == BackupType.Full ? BackupRunPurpose.PlanFull : BackupRunPurpose.PlanDifferential)
                    && set is not null && set.DatabaseId == snapshot.DatabaseId && MetadataPassed(set, rows, task.BackupType);
                var contradictory = attempt.BackupInvocationStatus == BackupInvocationStatus.ConfirmedFailed
                    && (set?.SqlSuccessObserved == true || set?.HasMetadataConflict == true
                        || set?.Metadata.BackupSetGuid.Value is not null
                        || rows.Any(x => x.SqlSuccessObserved || x.Metadata.BackupSetGuid.Value is not null
                            || x.Assessment.Conclusion == DifferentialBaselineConclusion.Mismatch));
                facts.Add(new(attempt.Id, attempt.BackupInvocationStatus, metadataPassed,
                    attempt.LocalVerifiedAtUtc is not null && attempt.SourceLengthBytes > 0, contradictory));
            }
            dispositions.Add(new(plan.Id, task.PlanVersionId!.Value, task.BackupType, task.ScheduledSlotAtUtc!.Value),
                BackupPlanSlotDispositionRules.Evaluate(task.Status, task.CurrentBackupAttemptId, facts,
                    task.CurrentStage == BackupTaskStage.Backup && task.ErrorCode == "backup_path_invalid" && rejectedBeforeBackup.Contains(task.Id),
                    task.CurrentStage));
        }
        return dispositions;
    }

    internal static bool MetadataPassed(BackupSet? set, IReadOnlyList<BackupSetEvidence> evidence, BackupType type = BackupType.Full)
    {
        if (set is null || set.HasMetadataConflict || !set.SqlSuccessObserved || !CompleteBackup(set.Metadata, type)
            || !Passed(set.Assessment) || evidence.Count == 0) return false;
        var latest = evidence.Max(x => x.ReconciliationNumber);
        var round = evidence.Where(x => x.ReconciliationNumber == latest).ToArray();
        // FULL 的差异依赖是不适用；不能将活动基线 Verified 当成文件核对通过。
        foreach (var source in new[] { BackupSetEvidenceSource.Comparison, BackupSetEvidenceSource.BackupHeader, BackupSetEvidenceSource.Msdb })
        {
            var rows = round.Where(x => x.Source == source).ToArray();
            if (rows.Length != 1 || rows[0].BackupSetId != set.Id || !rows[0].SqlSuccessObserved
                || rows[0].TaskId != set.TaskId || rows[0].AttemptId != set.AttemptId
                || !Passed(rows[0].Assessment) || !CompleteBackup(rows[0].Metadata, type)) return false;
            _ = set.Metadata.Merge(rows[0].Metadata, out var conflict);
            if (conflict) return false;
        }
        return true;

        bool Passed(BackupSetAssessment assessment) => type == BackupType.Full
            ? assessment == BackupSetAssessment.NotApplicable
            : type == BackupType.Differential && assessment.State == BackupMetadataState.Known
                && assessment.Conclusion == DifferentialBaselineConclusion.Verified && set?.BaseBackupSetId is not null;
    }

    private static bool CompleteBackup(BackupSetMetadata m, BackupType type) =>
        m.Type.Value == type && m.IsCopyOnly.Value == false
        && m.BackupSetGuid.Value is { } guid && guid != Guid.Empty
        && m.DatabaseGuid.Value is { } database && database != Guid.Empty
        && m.FamilyGuid.Value is { } family && family != Guid.Empty
        && m.FirstLsn.Value is not null && m.LastLsn.Value is not null && m.CheckpointLsn.Value is not null
        && m.DatabaseBackupLsn.Value is not null
        && (type == BackupType.Full
            ? m.DifferentialBaseGuid.State == BackupMetadataState.NotApplicable && m.DifferentialBaseLsn.State == BackupMetadataState.NotApplicable
            : type == BackupType.Differential && m.DifferentialBaseGuid.Value is { } baseGuid && baseGuid != Guid.Empty
                && m.DifferentialBaseLsn.Value is not null && m.DatabaseBackupLsn.Value == m.DifferentialBaseLsn.Value)
        && m.FirstRecoveryForkId.Value is { } fork && fork != Guid.Empty && m.RecoveryForkId.Value == fork
        && m.ForkPointLsn.State != BackupMetadataState.Unknown
        && m.HasBackupChecksums.Value is not null && m.IsCompressed.Value is not null
        && m.IsDamaged.Value == false && m.IsSnapshot.Value == false && m.HasIncompleteMetadata.Value == false
        && m.SqlStartedLocal.Value is not null && m.SqlFinishedLocal.Value is not null;
}
