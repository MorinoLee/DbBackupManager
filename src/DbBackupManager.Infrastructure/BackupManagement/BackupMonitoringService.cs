using System.Data.Common;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.BackupManagement;

internal sealed class BackupMonitoringService(IDbContextFactory<PlatformDbContext> factory) : IBackupMonitoringService
{
    public Task<BackupManagementResult<BackupOverview>> OverviewAsync(AdminSession actor, CancellationToken token = default) => Read(actor, async db =>
    {
        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var since = now.AddHours(-24);
        var managed = db.ManagedDatabases.Where(x => x.IsManaged);
        var without = managed.Where(d => !db.BackupPolicies.Any(p => p.DatabaseId == d.Id && p.IsEnabled));
        var never = managed.Where(d => !db.BackupFiles.Any(f =>
            f.DatabaseId == d.Id && f.Status == BackupFileStatus.Available));
        var heartbeat = await db.WorkerHeartbeats.SingleOrDefaultAsync(token);
        var presence = heartbeat is null ? new WorkerPresence("Unknown", null)
            : new WorkerPresence(!heartbeat.IsStopped && heartbeat.LastSeenAtUtc <= now && heartbeat.LastSeenAtUtc >= now.AddSeconds(-90)
                ? "Online" : "Offline", heartbeat.LastSeenAtUtc);
        var rows = TaskRows(db);
        var failures = rows.Where(x => x.Task.Status == BackupTaskStatus.Failed && x.Task.CompletedAtUtc >= since && x.Task.CompletedAtUtc < now);
        var managedCount = await managed.CountAsync(token);
        var neverCount = await never.CountAsync(token);
        return new BackupOverview(now, since, managedCount, await without.CountAsync(token), neverCount, managedCount - neverCount,
            await db.BackupTasks.CountAsync(x => x.Status == BackupTaskStatus.Pending, token),
            await db.BackupTasks.CountAsync(x => x.Status == BackupTaskStatus.Running, token),
            await db.BackupTasks.CountAsync(x => x.Status == BackupTaskStatus.NeedsAttention, token),
            await failures.CountAsync(token), presence,
            await Summaries(rows.OrderByDescending(x => x.Task.CreatedAtUtc).ThenByDescending(x => x.Task.Id).Take(5)).ToListAsync(token),
            await Summaries(failures.OrderByDescending(x => x.Task.CompletedAtUtc).ThenByDescending(x => x.Task.Id).Take(5)).ToListAsync(token),
            await Files(FileRows(db).Where(x => x.File.Location == BackupFileLocation.Local)
                .OrderByDescending(x => x.File.ValidatedAtUtc).ThenByDescending(x => x.File.TaskId).Take(5)).ToListAsync(token));
    }, token);

    public Task<BackupManagementResult<BackupPage<BackupTaskSummary>>> TasksAsync(AdminSession actor, BackupSearch search, CancellationToken token = default) => Read(actor, async db =>
    {
        Validate(search, allowStatus: true);
        var rows = TaskRows(db);
        var text = search.Search?.Trim();
        if (!string.IsNullOrEmpty(text)) rows = rows.Where(x => x.Snapshot.DatabaseName.Contains(text) || x.Snapshot.PolicyName.Contains(text));
        if (!string.IsNullOrEmpty(search.Status))
        {
            var status = Enum.Parse<BackupTaskStatus>(search.Status);
            rows = rows.Where(x => x.Task.Status == status);
        }
        if (search.FromUtc is not null) rows = rows.Where(x => x.Task.CreatedAtUtc >= search.FromUtc);
        if (search.UntilUtc is not null) rows = rows.Where(x => x.Task.CreatedAtUtc < search.UntilUtc);
        var total = await rows.LongCountAsync(token);
        var ordered = search.OldestFirst ? rows.OrderBy(x => x.Task.CreatedAtUtc).ThenBy(x => x.Task.Id)
            : rows.OrderByDescending(x => x.Task.CreatedAtUtc).ThenByDescending(x => x.Task.Id);
        return new BackupPage<BackupTaskSummary>(await Summaries(ordered.Skip(search.Page * 20).Take(20)).ToListAsync(token), search.Page, total);
    }, token);

    public Task<BackupManagementResult<BackupPage<BackupFileSummary>>> FilesAsync(AdminSession actor, BackupSearch search, CancellationToken token = default) => Read(actor, async db =>
    {
        Validate(search, allowStatus: false);
        // 筛选作用于数据库投影，分页前执行；不逐项加载详情或探测文件系统。
        var rows = FileRows(db);
        var text = search.Search?.Trim();
        if (!string.IsNullOrEmpty(text)) rows = rows.Where(x => x.Snapshot.DatabaseName.Contains(text) || x.Snapshot.PolicyName.Contains(text));
        if (search.FromUtc is not null) rows = rows.Where(x => x.File.ValidatedAtUtc >= search.FromUtc);
        if (search.UntilUtc is not null) rows = rows.Where(x => x.File.ValidatedAtUtc < search.UntilUtc);
        var total = await rows.LongCountAsync(token);
        var ordered = search.OldestFirst ? rows.OrderBy(x => x.File.ValidatedAtUtc).ThenBy(x => x.File.TaskId)
            : rows.OrderByDescending(x => x.File.ValidatedAtUtc).ThenByDescending(x => x.File.TaskId);
        return new BackupPage<BackupFileSummary>(await Files(ordered.Skip(search.Page * 20).Take(20)).ToListAsync(token), search.Page, total);
    }, token);

    private static IQueryable<FileRow> FileRows(PlatformDbContext db) =>
        from f in db.BackupFiles
        join a in db.BackupAttempts on f.AttemptId equals a.Id
        join s in db.BackupTaskSnapshots on f.TaskId equals s.TaskId
        join t in db.StorageTargets on f.StorageTargetId equals t.Id into targets
        from t in targets.DefaultIfEmpty()
        select new FileRow { File = f, Attempt = a, Snapshot = s, Target = t };

    private static IQueryable<BackupFileSummary> Files(IQueryable<FileRow> rows) => rows.Select(x => new BackupFileSummary(
        x.File.TaskId, x.File.DatabaseId, x.Snapshot.DatabaseName, x.Snapshot.InstanceName, x.Snapshot.PolicyName,
        x.File.Location == BackupFileLocation.Local ? x.Attempt.LocalSqlFilePath : "",
        x.File.LengthBytes, x.File.ValidatedAtUtc, x.File.RetentionDays, x.File.Id,
        x.File.Location.ToString(), x.File.Protocol.ToString(), x.Target == null ? null : x.Target.Name,
        x.File.Status.ToString(), x.File.DeletedAtUtc, x.File.DeletionErrorCode, x.File.Path));

    private static IQueryable<TaskRow> TaskRows(PlatformDbContext db) =>
        from t in db.BackupTasks
        join s in db.BackupTaskSnapshots on t.Id equals s.TaskId
        join f in db.BackupFiles.Where(file => file.Location == BackupFileLocation.Local)
            on t.Id equals f.TaskId into files
        from f in files.DefaultIfEmpty()
        select new TaskRow { Task = t, Snapshot = s, File = f };

    private static IQueryable<BackupTaskSummary> Summaries(IQueryable<TaskRow> rows) => rows.Select(x =>
        new BackupTaskSummary(x.Task.Id, x.Snapshot.PolicyName, x.Snapshot.DatabaseName, x.Task.Status.ToString(),
            x.Task.CurrentStage == null ? null : x.Task.CurrentStage.ToString(), x.Task.CreatedAtUtc,
            x.Task.CompletedAtUtc, x.Task.ErrorCode, x.Task.CancellationRequestedAtUtc != null, x.File == null ? null : x.File.LengthBytes));

    private static void Validate(BackupSearch query, bool allowStatus)
    {
        if (query.Page is < 0 or > 10000 || query.Search?.Length > 200 || query.Search?.Any(char.IsControl) == true
            || query.FromUtc is not null && query.UntilUtc is not null && query.FromUtc >= query.UntilUtc
            || !string.IsNullOrEmpty(query.Status) && (!allowStatus || !Enum.GetNames<BackupTaskStatus>().Contains(query.Status)))
            throw new ArgumentException("查询条件无效。");
    }

    private async Task<BackupManagementResult<T>> Read<T>(AdminSession actor, Func<PlatformDbContext, Task<T>> query, CancellationToken token)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var admin = await db.AdminUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actor.AdminUserId, token);
            if (admin is not { IsEnabled: true } || admin.IsLockedOut(DateTimeOffset.UtcNow) || admin.SecurityStamp != actor.SecurityStamp)
                return new(BackupManagementCode.AuthenticationRequired);
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            return new(BackupManagementCode.Succeeded, await query(db));
        }
        catch (ArgumentException) { return new(BackupManagementCode.Invalid); }
        catch (Exception e) when (e is DbException or InvalidOperationException) { return new(BackupManagementCode.Unavailable); }
    }

    private sealed class TaskRow
    {
        public required BackupTask Task { get; init; }
        public required BackupTaskSnapshot Snapshot { get; init; }
        public BackupFile? File { get; init; }
    }

    private sealed class FileRow
    {
        public required BackupFile File { get; init; }
        public required BackupAttempt Attempt { get; init; }
        public required BackupTaskSnapshot Snapshot { get; init; }
        public StorageTarget? Target { get; init; }
    }
}
