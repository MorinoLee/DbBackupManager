using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupSets;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed record PlanLocationSurface(
    bool HasCurrentPlan,
    ActiveBaseline Baseline,
    IReadOnlyList<PlanLocationAssessment> Assessments);

internal static class PlanLocationRetentionWork
{
    internal static async Task<IReadOnlyList<PlanLocationAssessment>> LoadAsync(
        PlatformDbContext db,
        Guid databaseId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken) =>
        (await LoadSurfaceAsync(db, databaseId, nowUtc, cancellationToken)).Assessments;

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

    internal static async Task<PlanLocationSurface> LoadSurfaceAsync(
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
        var assessments = PlanLocationRecovery.Evaluate(
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
        return new PlanLocationSurface(version is not null, baseline, assessments);
    }

    /// <summary>
    /// 只加载本页副本、它们的备份集和同一备份集的其他副本。某个库还有未加载的历史时，窗口类原因保持未知。
    /// </summary>
    internal static async Task<IReadOnlyDictionary<Guid, string>> ReasonsForFilesAsync(
        PlatformDbContext db,
        IReadOnlyCollection<Guid> fileIds,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var pageIds = fileIds.Distinct().ToArray();
        var pageSet = pageIds.ToHashSet();
        var files = await db.BackupFiles.AsNoTracking()
            .Where(file => pageIds.Contains(file.Id))
            .ToListAsync(cancellationToken);
        if (files.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var setIds = files.Where(file => file.BackupSetId is not null).Select(file => file.BackupSetId!.Value).Distinct().ToArray();
        var sets = setIds.Length == 0
            ? new List<BackupSet>()
            : await db.BackupSets.AsNoTracking().Where(set => setIds.Contains(set.Id)).ToListAsync(cancellationToken);
        var loadedSetIds = sets.Select(set => set.Id).ToHashSet();
        var baseIds = sets
            .Where(set => set.BaseBackupSetId is { } baseId && loadedSetIds.Add(baseId))
            .Select(set => set.BaseBackupSetId!.Value)
            .Distinct()
            .ToArray();
        if (baseIds.Length > 0)
        {
            sets.AddRange(await db.BackupSets.AsNoTracking().Where(set => baseIds.Contains(set.Id)).ToListAsync(cancellationToken));
        }

        var knownSetIds = sets.Select(set => set.Id).ToArray();
        if (knownSetIds.Length > 0)
        {
            files.AddRange(await db.BackupFiles.AsNoTracking()
                .Where(file => file.BackupSetId != null && knownSetIds.Contains(file.BackupSetId.Value) && !pageIds.Contains(file.Id))
                .ToListAsync(cancellationToken));
        }

        var databaseIds = files.Select(file => file.DatabaseId).Distinct().ToArray();
        var fileCounts = await db.BackupFiles.AsNoTracking()
            .Where(file => databaseIds.Contains(file.DatabaseId))
            .GroupBy(file => file.DatabaseId)
            .Select(group => new { DatabaseId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var setCounts = await db.BackupSets.AsNoTracking()
            .Where(set => databaseIds.Contains(set.DatabaseId))
            .GroupBy(set => set.DatabaseId)
            .Select(group => new { DatabaseId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var fileCountByDatabase = fileCounts.ToDictionary(item => item.DatabaseId, item => item.Count);
        var setCountByDatabase = setCounts.ToDictionary(item => item.DatabaseId, item => item.Count);
        var taskIds = files.Select(file => file.TaskId).Distinct().ToArray();
        var snapshots = await db.BackupTaskSnapshots.AsNoTracking()
            .Where(snapshot => taskIds.Contains(snapshot.TaskId))
            .ToListAsync(cancellationToken);
        var tasks = await db.BackupTasks.AsNoTracking()
            .Where(task => taskIds.Contains(task.Id))
            .ToListAsync(cancellationToken);
        var openTasks = await (
            from task in db.BackupTasks.AsNoTracking()
            join snapshot in db.BackupTaskSnapshots.AsNoTracking() on task.Id equals snapshot.TaskId
            where databaseIds.Contains(snapshot.DatabaseId)
                && task.PlanId != null
                && (task.Status == BackupTaskStatus.Pending
                    || task.Status == BackupTaskStatus.Running
                    || task.Status == BackupTaskStatus.NeedsAttention)
            select new { snapshot.DatabaseId, TaskId = task.Id }).ToListAsync(cancellationToken);
        var openTaskIds = openTasks.Select(item => item.TaskId).Distinct().ToArray();
        var admitted = openTaskIds.Length == 0
            ? []
            : await db.BackupAttempts.AsNoTracking()
                .Where(attempt => attempt.AdmittedFullBackupSetId != null && openTaskIds.Contains(attempt.TaskId))
                .Select(attempt => new { attempt.TaskId, SetId = attempt.AdmittedFullBackupSetId!.Value })
                .ToListAsync(cancellationToken);
        var plans = await db.BackupPlans.AsNoTracking()
            .Where(plan => databaseIds.Contains(plan.DatabaseId))
            .Select(plan => new { plan.DatabaseId, VersionId = EF.Property<Guid?>(plan, "_currentVersionId") })
            .ToListAsync(cancellationToken);
        var versionIds = plans.Where(plan => plan.VersionId is not null).Select(plan => plan.VersionId!.Value).Distinct().ToArray();
        var versions = versionIds.Length == 0
            ? []
            : await db.BackupPlanVersions.AsNoTracking().Where(version => versionIds.Contains(version.Id)).ToListAsync(cancellationToken);
        var versionById = versions.ToDictionary(version => version.Id);
        var taskById = tasks.ToDictionary(task => task.Id);
        var snapshotByTask = snapshots.GroupBy(snapshot => snapshot.TaskId)
            .ToDictionary(group => group.Key, group => group.First());
        var reasons = new Dictionary<Guid, string>();
        foreach (var databaseId in databaseIds)
        {
            var databaseFiles = files.Where(file => file.DatabaseId == databaseId).ToArray();
            var databaseSets = sets.Where(set => set.DatabaseId == databaseId).ToArray();
            var complete = fileCountByDatabase.GetValueOrDefault(databaseId) == databaseFiles.Length
                && setCountByDatabase.GetValueOrDefault(databaseId) == databaseSets.Length;
            var openIds = openTasks.Where(item => item.DatabaseId == databaseId).Select(item => item.TaskId).ToHashSet();
            var admittedSets = admitted.Where(item => openIds.Contains(item.TaskId)).Select(item => item.SetId).ToHashSet();
            var pointer = plans.SingleOrDefault(plan => plan.DatabaseId == databaseId);
            var version = pointer?.VersionId is { } versionId && versionById.TryGetValue(versionId, out var found)
                ? found
                : null;
            var evidence = await LoadActiveBaselineAsync(db, databaseId, cancellationToken);
            var unproven = !complete || databaseSets.Any(set =>
                set.Metadata.Type.State != BackupMetadataState.Known
                || set.Metadata.Type.Value == BackupType.Differential && !Proven(set));
            var assessments = ProjectDatabase(
                version,
                ResolveBaseline(version?.Mode, evidence, databaseSets),
                unproven,
                openIds,
                admittedSets,
                databaseSets,
                databaseFiles,
                taskById,
                snapshotByTask,
                nowUtc);
            foreach (var (fileId, reason) in BackupFileRetentionScope.Select(pageSet, assessments, complete))
            {
                reasons[fileId] = reason;
            }
        }

        return reasons;
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

    private static PlanLocationAssessment[] ProjectDatabase(
        BackupPlanVersion? version,
        ActiveBaseline baseline,
        bool unproven,
        HashSet<Guid> activeTasks,
        HashSet<Guid> admittedSets,
        IReadOnlyList<BackupSet> sets,
        IReadOnlyList<BackupFile> files,
        Dictionary<Guid, BackupTask> taskById,
        Dictionary<Guid, BackupTaskSnapshot> snapshotByTask,
        DateTimeOffset nowUtc)
    {
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
}
