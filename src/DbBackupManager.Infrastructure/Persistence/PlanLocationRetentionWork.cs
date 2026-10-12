using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal static class PlanLocationRetentionWork
{
    internal const string ClaimedReason = "plan.retention.claimed";
    internal const string StagingClaimedReason = "plan.staging.claimed";
    internal const string ReleasedReason = "plan.retention.protected";
    internal const string DeletedReason = "plan.retention.deleted";
    internal const string StagingRetainedReason = "remote.staging_retained";

    /// <summary>
    /// 只有计划任务、v3 命名规则且已关联备份集的副本由计划保留处理。其余租约保持原样。
    /// </summary>
    internal static bool IsPlanCopy(BackupFile file, BackupTask? task, BackupTaskSnapshot? snapshot) =>
        task?.PlanId is not null
        && snapshot is not null
        && snapshot.DatabaseId == file.DatabaseId
        && snapshot.FileNameRuleVersion == "v3"
        && file.BackupSetId is not null;

    internal static async Task<HashSet<Guid>> PlanCopyIdsAsync(
        PlatformDbContext db,
        Guid databaseId,
        CancellationToken cancellationToken)
    {
        var files = await db.BackupFiles.AsNoTracking()
            .Where(file => file.DatabaseId == databaseId)
            .ToListAsync(cancellationToken);
        var taskIds = files.Select(file => file.TaskId).Distinct().ToArray();
        var tasks = await db.BackupTasks.AsNoTracking()
            .Where(task => taskIds.Contains(task.Id))
            .ToListAsync(cancellationToken);
        var snapshots = await db.BackupTaskSnapshots.AsNoTracking()
            .Where(snapshot => taskIds.Contains(snapshot.TaskId))
            .ToListAsync(cancellationToken);
        var taskById = tasks.ToDictionary(task => task.Id);
        var snapshotByTask = snapshots.GroupBy(snapshot => snapshot.TaskId)
            .ToDictionary(group => group.Key, group => group.First());
        return files
            .Where(file => IsPlanCopy(
                file,
                taskById.GetValueOrDefault(file.TaskId),
                snapshotByTask.GetValueOrDefault(file.TaskId)))
            .Select(file => file.Id)
            .ToHashSet();
    }

    internal static async Task<IReadOnlyList<PlanLocationAssessment>> LoadAsync(
        PlatformDbContext db,
        Guid databaseId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var versionId = await db.BackupPlans.AsNoTracking()
            .Where(plan => plan.DatabaseId == databaseId)
            .Select(plan => EF.Property<Guid?>(plan, "_currentVersionId"))
            .SingleOrDefaultAsync(cancellationToken);
        var version = versionId is { } id
            ? await db.BackupPlanVersions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            : null;
        var sets = await db.BackupSets.AsNoTracking().Where(set => set.DatabaseId == databaseId).ToListAsync(cancellationToken);
        var files = await db.BackupFiles.AsNoTracking().Where(file => file.DatabaseId == databaseId).ToListAsync(cancellationToken);
        var taskIds = files.Select(file => file.TaskId).Distinct().ToArray();
        var snapshots = await db.BackupTaskSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.DatabaseId == databaseId || taskIds.Contains(snapshot.TaskId))
            .ToListAsync(cancellationToken);
        var snapshotTaskIds = snapshots.Select(snapshot => snapshot.TaskId).Distinct().ToArray();
        var tasks = await db.BackupTasks.AsNoTracking()
            .Where(task => snapshotTaskIds.Contains(task.Id))
            .ToListAsync(cancellationToken);
        var admitted = await db.BackupAttempts.AsNoTracking()
            .Where(attempt => attempt.AdmittedFullBackupSetId != null && snapshotTaskIds.Contains(attempt.TaskId))
            .Select(attempt => new { attempt.TaskId, attempt.AdmittedFullBackupSetId })
            .ToListAsync(cancellationToken);
        var evidence = await LoadActiveBaselineAsync(db, databaseId, cancellationToken);
        var taskById = tasks.ToDictionary(task => task.Id);
        var snapshotByTask = snapshots.GroupBy(snapshot => snapshot.TaskId).ToDictionary(group => group.Key, group => group.First());
        var activeTasks = tasks.Where(task => task.PlanId is not null && IsOpen(task.Status)).Select(task => task.Id).ToHashSet();
        var admittedSets = admitted
            .Where(item => taskById.TryGetValue(item.TaskId, out var task) && IsOpen(task.Status))
            .Select(item => item.AdmittedFullBackupSetId!.Value)
            .ToHashSet();
        var setInputs = sets.Select(set =>
        {
            var task = taskById.GetValueOrDefault(set.TaskId);
            return new PlanRecoverySetInput(
                set.Id,
                set.Metadata.Type.State == BackupMetadataState.Known ? set.Metadata.Type.Value : null,
                set.Completion.Source == BackupCompletionTimeSource.Unknown ? null : set.Completion.CompletedAtUtc,
                set.BaseBackupSetId,
                set.Metadata.IsCopyOnly.State == BackupMetadataState.Known ? set.Metadata.IsCopyOnly.Value : null,
                Proven(set),
                task is not null && IsOpen(task.Status) || admittedSets.Contains(set.Id));
        }).ToArray();
        var copyInputs = files.Select(file =>
        {
            snapshotByTask.TryGetValue(file.TaskId, out var snapshot);
            taskById.TryGetValue(file.TaskId, out var task);
            var owned = task?.PlanId is not null
                && snapshot is not null
                && snapshot.DatabaseId == file.DatabaseId
                && snapshot.FileNameRuleVersion == "v3"
                && file.BackupSetId is not null
                && (file.Location == BackupFileLocation.Local
                    ? file.DatabaseServerId == snapshot.ServerId
                    : file.StorageTargetId == snapshot.StorageTargetId);
            var staging = snapshot?.StorageMode == BackupStorageMode.RemoteOnly && file.Location == BackupFileLocation.Local;
            return new PlanRecoveryCopyInput(
                file.Id,
                file.BackupSetId,
                file.Location,
                file.StorageTargetId,
                file.Status,
                staging,
                owned,
                staging ? snapshot!.StorageTargetId : null,
                activeTasks.Any(id => id != file.TaskId));
        }).ToArray();
        var windows = new List<PlanLocationWindow> { new(BackupFileLocation.Local, null, version?.LocalRecoveryWindowDays) };
        var remoteTargets = files
            .Where(file => file.Location == BackupFileLocation.Remote && file.StorageTargetId is not null)
            .Select(file => file.StorageTargetId!.Value)
            .ToHashSet();
        if (version?.StorageTargetId is { } currentTarget)
        {
            remoteTargets.Add(currentTarget);
        }

        windows.AddRange(remoteTargets.Select(target => new PlanLocationWindow(
            BackupFileLocation.Remote,
            target,
            version?.RemoteRecoveryWindowDays)));
        var unproven = sets.Any(set => set.Metadata.Type.State != BackupMetadataState.Known
            || set.Metadata.Type.Value == BackupType.Differential && !Proven(set));
        var baseline = ResolveBaseline(version?.Mode, evidence, sets);
        return PlanLocationRecovery.Evaluate(
                nowUtc,
                baseline,
                unproven,
                activeTasks.Count > 0,
                windows,
                setInputs,
                copyInputs)
            .Select(result => new PlanLocationAssessment(
                result.Location,
                result.StorageTargetId,
                result.WindowDays,
                result.Conclusion,
                result.Copies))
            .ToArray();
    }

    internal static async Task ReleaseSupersededAsync(
        PlatformDbContext db,
        Guid databaseId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var planCopies = await PlanCopyIdsAsync(db, databaseId, cancellationToken);
        var assessments = await LoadAsync(db, databaseId, nowUtc, cancellationToken);
        var decisions = assessments.SelectMany(assessment => assessment.Copies).ToDictionary(copy => copy.FileId);
        var pending = await db.BackupFiles.AsTracking()
            .Where(file => file.DatabaseId == databaseId && file.Status == BackupFileStatus.DeletePending)
            .ToListAsync(cancellationToken);
        var changed = false;
        foreach (var file in pending)
        {
            // 过期租约留给事务外的文件探测。删除执行者持有会话锁时，未到期租约不可能正处在解开之后。
            if (!planCopies.Contains(file.Id)
                || file.DeletionLeaseExpiresAtUtc is null
                || file.DeletionLeaseExpiresAtUtc <= nowUtc
                || (decisions.TryGetValue(file.Id, out var decision) && PlanLocationRecovery.RetainsPhysicalLease(decision)))
            {
                continue;
            }

            file.CancelDeletionLease(nowUtc);
            db.BackupFileStateChanges.Add(new BackupFileStateChange(
                Guid.NewGuid(),
                file.Id,
                file.TaskId,
                BackupFileStatus.DeletePending,
                BackupFileStatus.Available,
                ReleasedReason,
                nowUtc));
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    internal static Dictionary<Guid, PlanCopyRetentionDecision> Decisions(IReadOnlyList<PlanLocationAssessment> assessments) =>
        assessments.SelectMany(assessment => assessment.Copies).ToDictionary(copy => copy.FileId);

    /// <summary>
    /// 只有已经确认远端验证的 RemoteOnly 暂存，才在解开后写下清理时间。
    /// 没有远端验证证据时不补造时间。
    /// </summary>
    internal static async Task NoteStagingCleanedAsync(
        PlatformDbContext db,
        BackupFile file,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var snapshot = await db.BackupTaskSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TaskId == file.TaskId, cancellationToken);
        if (snapshot?.StorageMode != BackupStorageMode.RemoteOnly || file.Location != BackupFileLocation.Local)
        {
            return;
        }

        var attempt = await db.BackupAttempts.AsTracking()
            .SingleOrDefaultAsync(item => item.Id == file.AttemptId && item.TaskId == file.TaskId, cancellationToken);
        if (attempt?.RemoteValidatedAtUtc is not { } validated || attempt.LocalCleanupCompletedAtUtc is not null)
        {
            return;
        }

        attempt.RecordLocalCleanupCompleted(nowUtc < validated ? validated : nowUtc);
    }

    private static bool Proven(BackupSet set)
    {
        if (set.HasMetadataConflict
            || set.Metadata.Type.State != BackupMetadataState.Known
            || set.Metadata.IsCopyOnly.State != BackupMetadataState.Known
            || set.Completion.Source == BackupCompletionTimeSource.Unknown
            || set.Completion.CompletedAtUtc is null)
        {
            return false;
        }

        return set.Metadata.Type.Value switch
        {
            BackupType.Full => set.Assessment.State == BackupMetadataState.NotApplicable,
            BackupType.Differential => set.Assessment.Conclusion == DifferentialBaselineConclusion.Verified
                && set.BaseBackupSetId is not null,
            BackupType.Log => true,
            _ => false,
        };
    }

    private static ActiveBaseline ResolveBaseline(
        BackupPlanMode? mode,
        IReadOnlyList<BackupSetEvidence> evidence,
        IReadOnlyList<BackupSet> sets)
    {
        if (evidence.Count == 0)
        {
            return mode is BackupPlanMode.FullAndDifferential or BackupPlanMode.FullAndDifferentialAndLog
                ? ActiveBaseline.Unresolved
                : ActiveBaseline.None;
        }

        var conclusions = evidence.Select(row => row.Assessment.Conclusion).Distinct().ToArray();
        if (conclusions.Length != 1 || conclusions[0] is null)
        {
            return ActiveBaseline.Unresolved;
        }

        return conclusions[0] switch
        {
            DifferentialBaselineConclusion.Unmanaged => ActiveBaseline.None,
            DifferentialBaselineConclusion.Verified => KnownBaseline(evidence, sets),
            _ => ActiveBaseline.Unresolved,
        };
    }

    private static ActiveBaseline KnownBaseline(IReadOnlyList<BackupSetEvidence> evidence, IReadOnlyList<BackupSet> sets)
    {
        var guids = evidence
            .Select(row => row.Metadata.DifferentialBaseGuid.Value)
            .Where(guid => guid is not null && guid != Guid.Empty)
            .Select(guid => guid!.Value)
            .Distinct()
            .ToArray();
        if (guids.Length != 1)
        {
            return ActiveBaseline.Unresolved;
        }

        var matches = sets.Where(set => set.Metadata.BackupSetGuid.Value == guids[0]
            && set.Metadata.Type.Value == BackupType.Full
            && set.Metadata.IsCopyOnly.Value == false
            && !set.HasMetadataConflict).ToArray();
        return matches.Length == 1 ? ActiveBaseline.Known(matches[0].Id) : ActiveBaseline.Unresolved;
    }

    private static async Task<IReadOnlyList<BackupSetEvidence>> LoadActiveBaselineAsync(
        PlatformDbContext db,
        Guid databaseId,
        CancellationToken cancellationToken)
    {
        var latest = await (
            from row in db.BackupSetEvidence.AsNoTracking()
            join snapshot in db.BackupTaskSnapshots.AsNoTracking() on row.TaskId equals snapshot.TaskId
            where snapshot.DatabaseId == databaseId && row.Kind == BackupSetEvidenceKind.ActiveBaseline
            orderby row.ObservedAtUtc descending, row.EntryNumber descending
            select new { row.MutationId, row.ObservedAtUtc }).FirstOrDefaultAsync(cancellationToken);
        if (latest is null)
        {
            return [];
        }

        return await (
            from row in db.BackupSetEvidence.AsNoTracking()
            join snapshot in db.BackupTaskSnapshots.AsNoTracking() on row.TaskId equals snapshot.TaskId
            where snapshot.DatabaseId == databaseId
                && row.Kind == BackupSetEvidenceKind.ActiveBaseline
                && row.MutationId == latest.MutationId
                && row.ObservedAtUtc == latest.ObservedAtUtc
            select row).ToListAsync(cancellationToken);
    }

    private static bool IsOpen(BackupTaskStatus status) =>
        status is BackupTaskStatus.Pending or BackupTaskStatus.Running or BackupTaskStatus.NeedsAttention;
}
