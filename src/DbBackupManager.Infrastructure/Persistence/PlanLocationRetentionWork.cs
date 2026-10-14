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
        var evidence = (await LoadActiveBaselinesAsync(db, [databaseId], cancellationToken)).GetValueOrDefault(databaseId) ?? [];
        var taskById = tasks.ToDictionary(task => task.Id);
        var snapshotByTask = snapshots.GroupBy(snapshot => snapshot.TaskId).ToDictionary(group => group.Key, group => group.First());
        var activeTasks = tasks.Where(task => task.PlanId is not null && IsOpen(task.Status)).Select(task => task.Id).ToHashSet();
        var admittedSets = admitted
            .Where(item => taskById.TryGetValue(item.TaskId, out var task) && IsOpen(task.Status))
            .Select(item => item.AdmittedFullBackupSetId!.Value)
            .ToHashSet();
        var baseline = ResolveBaseline(version?.Mode, evidence, sets);
        var assessments = ProjectDatabase(
            version,
            baseline,
            sets.Any(AffectsGlobalUnproven),
            activeTasks,
            admittedSets,
            sets,
            files,
            taskById,
            snapshotByTask,
            nowUtc);
        return new PlanLocationSurface(version is not null, baseline, assessments);
    }

    /// <summary>
    /// 为本页副本计算保留原因。窗口类原因只在该位置的依赖闭包已经装入并核对过数量后才采用；
    /// 证明不了仍是未知，不能把没看到的历史说成可删除。与本页无关的副本不返回原因。
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

        var sets = new List<BackupSet>();
        var closureComplete = await ExtendClosureAsync(db, files, sets, cancellationToken);
        var facts = await LoadPageFactsAsync(db, files, cancellationToken);
        closureComplete &= await EnsureBaselineSetsAsync(db, files, sets, facts, cancellationToken);
        var preliminary = ProjectPage(files, sets, facts, nowUtc);
        var reasons = new Dictionary<Guid, string>();
        var needsProof = new List<BackupFile>();
        var filesById = files.ToDictionary(file => file.Id);
        foreach (var copy in preliminary.Values.SelectMany(assessments => assessments.SelectMany(item => item.Copies)))
        {
            if (!pageSet.Contains(copy.FileId))
            {
                continue;
            }

            if (BackupFileRetentionScope.DependsOnLocationGraph(copy.Reason))
            {
                needsProof.Add(filesById[copy.FileId]);
            }
            else
            {
                reasons[copy.FileId] = copy.Reason;
            }
        }

        if (needsProof.Count == 0)
        {
            return reasons;
        }

        if (!closureComplete)
        {
            MarkClosureIncomplete(reasons, needsProof);
            return reasons;
        }

        var proofDatabases = needsProof.Select(file => file.DatabaseId).Distinct().ToArray();
        var proofDatabaseSet = proofDatabases.ToHashSet();
        await LoadCriticalLocationCopiesAsync(db, files, needsProof, facts, nowUtc, cancellationToken);
        closureComplete = await ExtendClosureAsync(db, files, sets, cancellationToken);
        closureComplete &= await LoadRemainingProofSetsAsync(db, files, sets, proofDatabases, cancellationToken);
        if (!closureComplete)
        {
            MarkClosureIncomplete(reasons, needsProof);
            return reasons;
        }

        facts = await LoadPageFactsAsync(db, files, cancellationToken);
        closureComplete = await EnsureBaselineSetsAsync(db, files, sets, facts, cancellationToken);
        if (!closureComplete)
        {
            MarkClosureIncomplete(reasons, needsProof);
            return reasons;
        }

        var unsafeCounts = await CountUnsafeUnloadedAsync(db, files, sets, needsProof, facts, nowUtc, cancellationToken);
        var projected = ProjectPage(files, sets, facts, nowUtc);
        var proven = new HashSet<Guid>();
        foreach (var file in needsProof)
        {
            if (unsafeCounts.GetValueOrDefault(LocationKey.Of(file)) == 0)
            {
                proven.Add(file.Id);
            }
        }

        var needsProofIds = needsProof.Select(file => file.Id).ToHashSet();
        foreach (var (databaseId, assessments) in projected)
        {
            if (!proofDatabaseSet.Contains(databaseId))
            {
                continue;
            }

            foreach (var (fileId, reason) in BackupFileRetentionScope.Select(pageSet, assessments, proven))
            {
                if (needsProofIds.Contains(fileId))
                {
                    reasons[fileId] = reason;
                }
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

    /// <summary>
    /// 闭包每轮只向外扩一跳。走满 6 轮仍有新增时视为不完整，调用方必须把依赖位置图的原因标成未知。
    /// </summary>
    private static async Task<bool> ExtendClosureAsync(
        PlatformDbContext db,
        List<BackupFile> files,
        List<BackupSet> sets,
        CancellationToken cancellationToken)
    {
        for (var guard = 0; guard < 6; guard++)
        {
            var changed = false;
            var setIds = sets.Select(set => set.Id).ToHashSet();
            var missingSetIds = files
                .Where(file => file.BackupSetId is { } setId && setIds.Add(setId))
                .Select(file => file.BackupSetId!.Value)
                .Distinct()
                .ToArray();
            if (missingSetIds.Length > 0)
            {
                changed |= AddSets(sets, await db.BackupSets.AsNoTracking()
                    .Where(set => missingSetIds.Contains(set.Id))
                    .ToListAsync(cancellationToken));
                setIds = sets.Select(set => set.Id).ToHashSet();
            }

            var baseIds = sets
                .Where(set => set.BaseBackupSetId is { } baseId && setIds.Add(baseId))
                .Select(set => set.BaseBackupSetId!.Value)
                .Distinct()
                .ToArray();
            if (baseIds.Length > 0)
            {
                changed |= AddSets(sets, await db.BackupSets.AsNoTracking()
                    .Where(set => baseIds.Contains(set.Id))
                    .ToListAsync(cancellationToken));
                setIds = sets.Select(set => set.Id).ToHashSet();
            }

            var loadedIds = setIds.ToArray();
            if (loadedIds.Length > 0)
            {
                var dependents = await db.BackupSets.AsNoTracking()
                    .Where(set => set.BaseBackupSetId != null
                        && loadedIds.Contains(set.BaseBackupSetId.Value)
                        && !loadedIds.Contains(set.Id))
                    .ToListAsync(cancellationToken);
                if (dependents.Count > 0)
                {
                    changed |= AddSets(sets, dependents);
                    setIds = sets.Select(set => set.Id).ToHashSet();
                }
            }

            var knownSetIds = setIds.ToArray();
            var loadedFileIds = files.Select(file => file.Id).ToHashSet();
            if (knownSetIds.Length > 0)
            {
                var copies = await db.BackupFiles.AsNoTracking()
                    .Where(file => file.BackupSetId != null
                        && knownSetIds.Contains(file.BackupSetId.Value)
                        && !loadedFileIds.Contains(file.Id))
                    .ToListAsync(cancellationToken);
                if (copies.Count > 0)
                {
                    var ids = files.Select(file => file.Id).ToHashSet();
                    var added = false;
                    foreach (var copy in copies)
                    {
                        if (ids.Add(copy.Id))
                        {
                            files.Add(copy);
                            added = true;
                        }
                    }

                    changed |= added;
                }
            }

            if (!changed)
            {
                return true;
            }
        }

        return false;
    }

    private static void MarkClosureIncomplete(Dictionary<Guid, string> reasons, IReadOnlyList<BackupFile> needsProof)
    {
        foreach (var file in needsProof)
        {
            reasons[file.Id] = RecoveryProtectionReason.WindowUnknown;
        }
    }

    /// <summary>
    /// 本地副本在领域和表约束里都不能带存储目标。加载、分组和数量核对都把本地折成空目标；
    /// 远程沿用实际目标。异常行只计入未证明数量，不读进行，也不送进恢复判断。
    /// </summary>
    private readonly record struct LocationKey(Guid DatabaseId, BackupFileLocation Location, Guid? StorageTargetId)
    {
        public static LocationKey Of(BackupFile file) => Of(file.DatabaseId, file.Location, file.StorageTargetId);

        public static LocationKey Of(Guid databaseId, BackupFileLocation location, Guid? storageTargetId) =>
            new(databaseId, location, location == BackupFileLocation.Local ? null : storageTargetId);
    }

    /// <summary>
    /// 只读取会改变本页窗口或暂存结论的副本。锚点之前的已证明完整/差异副本留在库里，
    /// 随后用 SQL 数量确认它们确实可以被忽略。
    /// </summary>
    private static async Task LoadCriticalLocationCopiesAsync(
        PlatformDbContext db,
        List<BackupFile> files,
        IReadOnlyList<BackupFile> needsProof,
        PageFacts facts,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var loaded = files.Select(file => file.Id).ToHashSet();
        foreach (var key in needsProof.Select(LocationKey.Of).Distinct())
        {
            var pending = FilesAt(db, key).Where(file => !loaded.Contains(file.Id));
            if (TryGetWindow(WindowDays(facts, key), nowUtc, out var windowStart))
            {
                var anchor = await AnchorFullAtAsync(db, key, windowStart, cancellationToken);
                if (anchor is { } anchorAt)
                {
                    pending = ExceptOlderThanAnchor(pending, db, anchorAt);
                }
            }
            else
            {
                pending = pending.Where(file => db.BackupSets.Any(set => set.Id == file.BackupSetId
                    && (set.Metadata.Type.State != BackupMetadataState.Known
                        || (set.Metadata.Type.Value == BackupType.Differential
                            && (set.HasMetadataConflict
                                || set.Metadata.IsCopyOnly.State != BackupMetadataState.Known
                                || set.Completion.Source == BackupCompletionTimeSource.Unknown
                                || set.Completion.CompletedAtUtc == null
                                || set.Assessment.Conclusion != DifferentialBaselineConclusion.Verified
                                || set.BaseBackupSetId == null)))));
            }

            foreach (var row in await pending.ToListAsync(cancellationToken))
            {
                if (loaded.Add(row.Id))
                {
                    files.Add(row);
                }
            }
        }
    }

    /// <summary>
    /// 尚未装入、且会改变全局未证明或依赖保护的备份集。筛选在 SQL 中完成，不把其余备份集读进内存。
    /// </summary>
    private static async Task<bool> LoadRemainingProofSetsAsync(
        PlatformDbContext db,
        List<BackupFile> files,
        List<BackupSet> sets,
        Guid[] databaseIds,
        CancellationToken cancellationToken)
    {
        var loadedIds = sets.Select(set => set.Id).ToArray();
        var relevant = await db.BackupSets.AsNoTracking()
            .Where(set => databaseIds.Contains(set.DatabaseId) && !loadedIds.Contains(set.Id))
            .Where(set => set.Metadata.Type.State != BackupMetadataState.Known
                || (set.Metadata.Type.Value == BackupType.Differential
                    && (set.HasMetadataConflict
                        || set.Metadata.IsCopyOnly.State != BackupMetadataState.Known
                        || set.Completion.Source == BackupCompletionTimeSource.Unknown
                        || set.Completion.CompletedAtUtc == null
                        || set.Assessment.Conclusion != DifferentialBaselineConclusion.Verified
                        || set.BaseBackupSetId == null))
                || (set.BaseBackupSetId != null && loadedIds.Contains(set.BaseBackupSetId.Value)))
            .ToListAsync(cancellationToken);
        if (!AddSets(sets, relevant))
        {
            return true;
        }

        return await ExtendClosureAsync(db, files, sets, cancellationToken);
    }

    private static async Task<Dictionary<LocationKey, int>> CountUnsafeUnloadedAsync(
        PlatformDbContext db,
        IReadOnlyList<BackupFile> files,
        IReadOnlyList<BackupSet> sets,
        IReadOnlyList<BackupFile> needsProof,
        PageFacts facts,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var keys = needsProof.Select(LocationKey.Of).Distinct().ToArray();
        var unsafeCounts = keys.ToDictionary(key => key, _ => 0);
        var loadedIds = files.Select(file => file.Id).ToArray();
        var loadedSetIds = sets.Select(set => set.Id).ToArray();
        var databases = keys.Select(key => key.DatabaseId).Distinct().ToArray();
        var grouped = await db.BackupFiles.AsNoTracking()
            .Where(file => databases.Contains(file.DatabaseId))
            .GroupBy(file => new { file.DatabaseId, file.Location, file.StorageTargetId })
            .Select(group => new LocationCount(group.Key.DatabaseId, group.Key.Location, group.Key.StorageTargetId, group.Count()))
            .ToListAsync(cancellationToken);
        foreach (var row in grouped)
        {
            if (row.Location == BackupFileLocation.Local && row.StorageTargetId is not null
                && unsafeCounts.ContainsKey(new LocationKey(row.DatabaseId, BackupFileLocation.Local, null)))
            {
                unsafeCounts[new LocationKey(row.DatabaseId, BackupFileLocation.Local, null)] += row.Count;
            }

            if (row.Location == BackupFileLocation.Remote && row.StorageTargetId is null)
            {
                foreach (var key in keys.Where(key => key.DatabaseId == row.DatabaseId && key.Location == BackupFileLocation.Remote))
                {
                    unsafeCounts[key] += row.Count;
                }
            }
        }

        foreach (var key in keys)
        {
            var unloaded = FilesAt(db, key).Where(file => !loadedIds.Contains(file.Id));
            if (!TryGetWindow(WindowDays(facts, key), nowUtc, out var windowStart))
            {
                unsafeCounts[key] += await unloaded.CountAsync(
                    file => (file.BackupSetId != null && loadedSetIds.Contains(file.BackupSetId.Value))
                        || db.BackupSets.Any(set => set.Id == file.BackupSetId
                            && (set.Metadata.Type.State != BackupMetadataState.Known
                                || (set.Metadata.Type.Value == BackupType.Differential
                                    && (set.HasMetadataConflict
                                        || set.Metadata.IsCopyOnly.State != BackupMetadataState.Known
                                        || set.Completion.Source == BackupCompletionTimeSource.Unknown
                                        || set.Completion.CompletedAtUtc == null
                                        || set.Assessment.Conclusion != DifferentialBaselineConclusion.Verified
                                        || set.BaseBackupSetId == null))
                                || (set.BaseBackupSetId != null && loadedSetIds.Contains(set.BaseBackupSetId.Value)))),
                    cancellationToken);
                continue;
            }

            var anchor = await AnchorFullAtAsync(db, key, windowStart, cancellationToken);
            if (anchor is null)
            {
                unsafeCounts[key] += await unloaded.CountAsync(cancellationToken);
                continue;
            }

            var anchorAt = anchor.Value;
            unsafeCounts[key] += await ExceptOlderThanAnchor(unloaded, db, anchorAt)
                .CountAsync(cancellationToken);
            unsafeCounts[key] += await unloaded.CountAsync(
                file => file.BackupSetId != null && loadedSetIds.Contains(file.BackupSetId.Value),
                cancellationToken);
            unsafeCounts[key] += await unloaded.CountAsync(
                file => db.BackupSets.Any(set => set.Id == file.BackupSetId
                    && set.Metadata.Type.Value == BackupType.Differential
                    && db.BackupFiles.Count(copy => copy.BackupSetId == set.Id
                        && copy.DatabaseId == key.DatabaseId
                        && copy.Location == key.Location
                        && copy.StorageTargetId == key.StorageTargetId
                        && copy.Status != BackupFileStatus.Deleted) != 1),
                cancellationToken);
        }

        return unsafeCounts;
    }

    private static IQueryable<BackupFile> FilesAt(PlatformDbContext db, LocationKey key) =>
        db.BackupFiles.AsNoTracking().Where(file =>
            file.DatabaseId == key.DatabaseId
            && file.Location == key.Location
            && file.StorageTargetId == key.StorageTargetId);

    private static IQueryable<BackupFile> ExceptOlderThanAnchor(
        IQueryable<BackupFile> files,
        PlatformDbContext db,
        DateTimeOffset anchor) =>
        files.Where(file => !db.BackupSets.Any(set => set.Id == file.BackupSetId
            && !set.HasMetadataConflict
            && set.Metadata.Type.State == BackupMetadataState.Known
            && set.Metadata.IsCopyOnly.State == BackupMetadataState.Known
            && set.Completion.Source != BackupCompletionTimeSource.Unknown
            && set.Completion.CompletedAtUtc != null
            && set.Completion.CompletedAtUtc < anchor
            && ((set.Metadata.Type.Value == BackupType.Full && set.Assessment.State == BackupMetadataState.NotApplicable)
                || (set.Metadata.Type.Value == BackupType.Differential
                    && set.Assessment.Conclusion == DifferentialBaselineConclusion.Verified
                    && set.BaseBackupSetId != null))));

    /// <summary>
    /// 窗口前最新的已证明完整备份不晚于真正的还原点锚。比它更早的已证明副本不会改变本页原因。
    /// 只有已证明可用（Available）的副本能当锚点：缺失、待删或已删除的副本不能证明这一段窗口。
    /// </summary>
    private static async Task<DateTimeOffset?> AnchorFullAtAsync(
        PlatformDbContext db,
        LocationKey key,
        DateTimeOffset windowStart,
        CancellationToken cancellationToken)
    {
        var completions = db.BackupSets.AsNoTracking()
            .Where(set => set.DatabaseId == key.DatabaseId
                && set.Metadata.Type.Value == BackupType.Full
                && !set.HasMetadataConflict
                && set.Metadata.Type.State == BackupMetadataState.Known
                && set.Metadata.IsCopyOnly.State == BackupMetadataState.Known
                && set.Completion.Source != BackupCompletionTimeSource.Unknown
                && set.Completion.CompletedAtUtc != null
                && set.Assessment.State == BackupMetadataState.NotApplicable
                && set.Completion.CompletedAtUtc < windowStart
                && db.BackupFiles.Count(file => file.BackupSetId == set.Id
                    && file.DatabaseId == key.DatabaseId
                    && file.Location == key.Location
                    && file.StorageTargetId == key.StorageTargetId
                    && file.Status == BackupFileStatus.Available) == 1)
            .Select(set => set.Completion.CompletedAtUtc);
        return await completions.OrderByDescending(value => value).FirstOrDefaultAsync(cancellationToken);
    }

    private static int? WindowDays(PageFacts facts, LocationKey key)
    {
        var pointer = facts.Plans.SingleOrDefault(plan => plan.DatabaseId == key.DatabaseId);
        if (pointer?.VersionId is not { } versionId || !facts.Versions.TryGetValue(versionId, out var version))
        {
            return null;
        }

        return key.Location == BackupFileLocation.Local
            ? version.LocalRecoveryWindowDays
            : version.RemoteRecoveryWindowDays;
    }

    private static bool TryGetWindow(int? windowDays, DateTimeOffset nowUtc, out DateTimeOffset windowStart)
    {
        if (windowDays is null or < 1 or > 36_500)
        {
            windowStart = default;
            return false;
        }

        windowStart = nowUtc - TimeSpan.FromDays(windowDays.Value);
        return true;
    }

    private sealed record LocationCount(Guid DatabaseId, BackupFileLocation Location, Guid? StorageTargetId, int Count);

    private static async Task<PageFacts> LoadPageFactsAsync(
        PlatformDbContext db,
        IReadOnlyList<BackupFile> files,
        CancellationToken cancellationToken)
    {
        var databaseIds = files.Select(file => file.DatabaseId).Distinct().ToArray();
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
            select new OpenTask(snapshot.DatabaseId, task.Id)).ToListAsync(cancellationToken);
        var openTaskIds = openTasks.Select(item => item.TaskId).Distinct().ToArray();
        var admitted = openTaskIds.Length == 0
            ? []
            : await db.BackupAttempts.AsNoTracking()
                .Where(attempt => attempt.AdmittedFullBackupSetId != null && openTaskIds.Contains(attempt.TaskId))
                .Select(attempt => new AdmittedSet(attempt.TaskId, attempt.AdmittedFullBackupSetId!.Value))
                .ToListAsync(cancellationToken);
        var plans = await db.BackupPlans.AsNoTracking()
            .Where(plan => databaseIds.Contains(plan.DatabaseId))
            .Select(plan => new PlanPointer(plan.DatabaseId, EF.Property<Guid?>(plan, "_currentVersionId")))
            .ToListAsync(cancellationToken);
        var versionIds = plans.Where(plan => plan.VersionId is not null).Select(plan => plan.VersionId!.Value).Distinct().ToArray();
        var versions = versionIds.Length == 0
            ? []
            : await db.BackupPlanVersions.AsNoTracking().Where(version => versionIds.Contains(version.Id)).ToListAsync(cancellationToken);
        var evidence = await LoadActiveBaselinesAsync(db, databaseIds, cancellationToken);

        return new PageFacts(
            tasks.ToDictionary(task => task.Id),
            snapshots.GroupBy(snapshot => snapshot.TaskId).ToDictionary(group => group.Key, group => group.First()),
            openTasks,
            admitted,
            plans,
            versions.ToDictionary(version => version.Id),
            evidence);
    }

    private static async Task<bool> EnsureBaselineSetsAsync(
        PlatformDbContext db,
        List<BackupFile> files,
        List<BackupSet> sets,
        PageFacts facts,
        CancellationToken cancellationToken)
    {
        foreach (var (databaseId, evidence) in facts.Evidence)
        {
            var conclusions = evidence.Select(row => row.Assessment.Conclusion).Distinct().ToArray();
            if (conclusions.Length != 1 || conclusions[0] != DifferentialBaselineConclusion.Verified)
            {
                continue;
            }

            var guids = evidence
                .Select(row => row.Metadata.DifferentialBaseGuid.Value)
                .Where(guid => guid is not null && guid != Guid.Empty)
                .Select(guid => guid!.Value)
                .Distinct()
                .ToArray();
            var present = sets.Where(set => set.DatabaseId == databaseId)
                .Select(set => set.Metadata.BackupSetGuid.Value)
                .Where(guid => guid is not null)
                .Select(guid => guid!.Value)
                .ToHashSet();
            var missing = guids.Where(guid => !present.Contains(guid)).ToArray();
            if (missing.Length == 0)
            {
                continue;
            }

            AddSets(sets, await db.BackupSets.AsNoTracking()
                .Where(set => set.DatabaseId == databaseId
                    && set.Metadata.BackupSetGuid.Value != null
                    && missing.Contains(set.Metadata.BackupSetGuid.Value.Value))
                .ToListAsync(cancellationToken));
        }

        return await ExtendClosureAsync(db, files, sets, cancellationToken);
    }

    private static bool AddSets(List<BackupSet> sets, IEnumerable<BackupSet> rows)
    {
        var ids = sets.Select(set => set.Id).ToHashSet();
        var added = false;
        foreach (var row in rows)
        {
            if (ids.Add(row.Id))
            {
                sets.Add(row);
                added = true;
            }
        }

        return added;
    }

    private static Dictionary<Guid, PlanLocationAssessment[]> ProjectPage(
        IReadOnlyList<BackupFile> files,
        IReadOnlyList<BackupSet> sets,
        PageFacts facts,
        DateTimeOffset nowUtc)
    {
        var projected = new Dictionary<Guid, PlanLocationAssessment[]>();
        foreach (var databaseId in files.Select(file => file.DatabaseId).Distinct())
        {
            var databaseFiles = files.Where(file => file.DatabaseId == databaseId).ToArray();
            var databaseSets = sets.Where(set => set.DatabaseId == databaseId).ToArray();
            var openIds = facts.OpenTasks.Where(item => item.DatabaseId == databaseId).Select(item => item.TaskId).ToHashSet();
            var admittedSets = facts.Admitted.Where(item => openIds.Contains(item.TaskId)).Select(item => item.SetId).ToHashSet();
            var pointer = facts.Plans.SingleOrDefault(plan => plan.DatabaseId == databaseId);
            var version = pointer?.VersionId is { } versionId && facts.Versions.TryGetValue(versionId, out var found)
                ? found
                : null;
            var evidence = facts.Evidence.GetValueOrDefault(databaseId) ?? [];
            projected[databaseId] = ProjectDatabase(
                version,
                ResolveBaseline(version?.Mode, evidence, databaseSets),
                databaseSets.Any(AffectsGlobalUnproven),
                openIds,
                admittedSets,
                databaseSets,
                databaseFiles,
                facts.Tasks,
                facts.Snapshots,
                nowUtc);
        }

        return projected;
    }

    private sealed record OpenTask(Guid DatabaseId, Guid TaskId);

    private sealed record AdmittedSet(Guid TaskId, Guid SetId);

    private sealed record PlanPointer(Guid DatabaseId, Guid? VersionId);

    private sealed record PageFacts(
        Dictionary<Guid, BackupTask> Tasks,
        Dictionary<Guid, BackupTaskSnapshot> Snapshots,
        List<OpenTask> OpenTasks,
        List<AdmittedSet> Admitted,
        List<PlanPointer> Plans,
        Dictionary<Guid, BackupPlanVersion> Versions,
        Dictionary<Guid, IReadOnlyList<BackupSetEvidence>> Evidence);

    private static bool AffectsGlobalUnproven(BackupSet set) =>
        set.Metadata.Type.State != BackupMetadataState.Known
        || set.Metadata.Type.Value == BackupType.Differential && !Proven(set);

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

    private static async Task<Dictionary<Guid, IReadOnlyList<BackupSetEvidence>>> LoadActiveBaselinesAsync(
        PlatformDbContext db,
        Guid[] databaseIds,
        CancellationToken cancellationToken)
    {
        var evidence = databaseIds.ToDictionary(
            id => id,
            _ => (IReadOnlyList<BackupSetEvidence>)Array.Empty<BackupSetEvidence>());
        if (databaseIds.Length == 0)
        {
            return evidence;
        }

        var latestTimes = await (
            from row in db.BackupSetEvidence.AsNoTracking()
            join snapshot in db.BackupTaskSnapshots.AsNoTracking() on row.TaskId equals snapshot.TaskId
            where databaseIds.Contains(snapshot.DatabaseId) && row.Kind == BackupSetEvidenceKind.ActiveBaseline
            group row by snapshot.DatabaseId
            into grouped
            select new { DatabaseId = grouped.Key, ObservedAtUtc = grouped.Max(row => row.ObservedAtUtc) }
        ).ToListAsync(cancellationToken);
        if (latestTimes.Count == 0)
        {
            return evidence;
        }

        var winners = latestTimes.ToDictionary(item => item.DatabaseId, item => item.ObservedAtUtc);
        var databaseIdSet = winners.Keys.ToArray();
        var observedAt = winners.Values.Distinct().ToArray();
        var candidates = await (
            from row in db.BackupSetEvidence.AsNoTracking()
            join snapshot in db.BackupTaskSnapshots.AsNoTracking() on row.TaskId equals snapshot.TaskId
            where databaseIdSet.Contains(snapshot.DatabaseId)
                && row.Kind == BackupSetEvidenceKind.ActiveBaseline
                && observedAt.Contains(row.ObservedAtUtc)
            select new { snapshot.DatabaseId, Row = row }).ToListAsync(cancellationToken);
        foreach (var (databaseId, observed) in winners)
        {
            var atTime = candidates
                .Where(item => item.DatabaseId == databaseId && item.Row.ObservedAtUtc == observed)
                .Select(item => item.Row)
                .ToArray();
            if (atTime.Length == 0)
            {
                continue;
            }

            var mutationId = atTime.OrderByDescending(row => row.EntryNumber).First().MutationId;
            evidence[databaseId] = atTime.Where(row => row.MutationId == mutationId).ToArray();
        }

        return evidence;
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
