using System.Data;
using System.Data.Common;
using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.BackupManagement;

public static class BackupManagementRegistration
{
    public static IServiceCollection AddBackupManagement(this IServiceCollection services)
    {
        services.AddScoped<IBackupManagementService, BackupManagementService>();
        services.AddScoped<IBackupMonitoringService, BackupMonitoringService>();
        services.AddScoped<IBackupPlanCatalogService, BackupPlanCatalogService>();
        services.AddScoped<ITaskEventStore, TaskEventStore>();
        services.AddScoped<ITaskEventRetentionStore, TaskEventRetentionStore>();
        return services;
    }
}
internal sealed class BackupManagementService(
    IDbContextFactory<PlatformDbContext> factory,
    IBackupTaskExecutionStore store,
    TimeProvider clock) : IBackupManagementService
{
    public Task<BackupManagementResult<BackupDashboard>> ListAsync(AdminSession actor, int page = 0, CancellationToken token = default) => Guard(async () =>
    {
        if (page is < 0 or > 10000) throw new Rejected(BackupManagementCode.Invalid);
        await using var db = await factory.CreateDbContextAsync(token);
        await Authorize(db, actor, clock.GetUtcNow(), token);
        var choices = await (from d in db.ManagedDatabases
                             join i in db.DatabaseInstances on d.InstanceId equals i.Id
                             join s in db.DatabaseServers on i.ServerId equals s.Id
                             where d.IsManaged && d.IsAvailable && !d.IsSystemDatabase && i.IsEnabled && s.IsEnabled
                                 && i.ConnectionStatus == SqlConnectionStatus.Connected
                             orderby s.Name, i.Name, d.DatabaseName
                             select new BackupDatabaseChoice(d.Id, s.Name + " / " + i.Name + " / " + d.DatabaseName)).ToListAsync(token);
        var rows = await Summaries(db, page: page).ToListAsync(token);
        var pageRows = await BackupSurfaceReader.EnrichTasksAsync(db, rows.Take(50).ToArray(), token);
        return new BackupManagementResult<BackupDashboard>(BackupManagementCode.Succeeded,
            new(choices, pageRows, rows.Count > 50));
    });
    public Task<BackupManagementResult<Guid>> CancelAsync(AdminSession actor, Guid taskId, Guid requestId, CancellationToken token = default) =>
        Mutate(actor, taskId, requestId, false, token);
    public Task<BackupManagementResult<Guid>> RetryAsync(AdminSession actor, Guid taskId, Guid requestId, CancellationToken token = default) =>
        Mutate(actor, taskId, requestId, true, token);
    public Task<BackupManagementResult<Guid>> RequestReconciliationAsync(AdminSession actor, Guid taskId, Guid requestId, CancellationToken token = default) => Guard(async () =>
    {
        if (requestId == Guid.Empty || taskId == Guid.Empty) throw new Rejected(BackupManagementCode.Invalid);
        var result = await store.RequestReconciliationAsync(new BackupTaskMutationCommand(
            taskId,
            requestId,
            DateTimeOffset.UtcNow,
            actor.AdminUserId,
            actor.SecurityStamp), token);
        return new BackupManagementResult<Guid>(MapCode(result.Code), result.Value?.TaskId ?? Guid.Empty);
    });
    public Task<BackupManagementResult<Guid>> ConfirmFailedAsync(AdminSession actor, Guid taskId, Guid requestId, bool evidenceReviewed, CancellationToken token = default) => Guard(async () =>
    {
        if (requestId == Guid.Empty || taskId == Guid.Empty || !evidenceReviewed)
            throw new Rejected(BackupManagementCode.Invalid);
        var result = await store.ConfirmNeedsAttentionAsync(new ConfirmNeedsAttentionCommand(
            taskId, requestId, DateTimeOffset.UtcNow, BackupReconciliationOutcome.Failed,
            "admin_confirmed_failed", "管理员已核对外部执行及相关文件、记录，确认本阶段失败。",
            actor.AdminUserId, actor.SecurityStamp), token);
        return new BackupManagementResult<Guid>(MapCode(result.Code), result.Value?.TaskId ?? Guid.Empty);
    });
    private Task<BackupManagementResult<Guid>> Mutate(AdminSession actor, Guid taskId, Guid requestId, bool retry, CancellationToken token) => Guard(async () =>
    {
        if (requestId == Guid.Empty || taskId == Guid.Empty) throw new Rejected(BackupManagementCode.Invalid);
        var command = new BackupTaskMutationCommand(taskId, requestId, DateTimeOffset.UtcNow, actor.AdminUserId, actor.SecurityStamp);
        var result = retry ? await store.RetryFailedAsync(command, token) : await store.RequestCancellationAsync(command, token);
        return new BackupManagementResult<Guid>(MapCode(result.Code), result.Value?.TaskId ?? Guid.Empty);
    });
    public Task<BackupManagementResult<BackupTaskDetail>> DetailAsync(AdminSession actor, Guid taskId, CancellationToken token = default) => Guard(async () =>
    {
        await using var db = await factory.CreateDbContextAsync(token); await Authorize(db, actor, clock.GetUtcNow(), token);
        var task = await Summaries(db, taskId).SingleOrDefaultAsync(token) ?? throw new Rejected(BackupManagementCode.NotFound);
        task = (await BackupSurfaceReader.EnrichTasksAsync(db, [task], token))[0];
        var history = await db.BackupTaskStateChanges.Where(x => x.TaskId == taskId).OrderByDescending(x => x.Id).Take(200)
            .Select(x => new BackupTaskHistory(x.OccurredAtUtc, x.ToStatus.ToString(), x.ToStage == null ? null : x.ToStage.ToString(), x.ReasonCode)).ToListAsync(token);
        var attempts = await db.BackupAttempts.Where(x => x.TaskId == taskId).OrderByDescending(x => x.AttemptNumber).Take(100)
            .Select(x => new BackupAttemptSummary(x.AttemptNumber, x.BackupInvocationStatus.ToString(), x.SourceLengthBytes, x.LocalVerifiedAtUtc)).ToListAsync(token);
        var now = clock.GetUtcNow();
        var files = await BackupSurfaceReader.EnrichFilesAsync(db, await FileProjections(db, taskId).ToListAsync(token), now, token);
        var path = files.FirstOrDefault(x => x.Location == nameof(BackupFileLocation.Local))?.SqlPath;
        var reconciliation = await db.BackupTasks.Where(x => x.Id == taskId).Select(x => new BackupReconciliationSummary(
            x.ReconciliationAttemptCount,
            x.NextReconciliationAtUtc,
            x.Status == BackupTaskStatus.NeedsAttention ? x.ErrorCode : null,
            x.Status == BackupTaskStatus.NeedsAttention
                && (x.LeaseToken == null || x.LeaseExpiresAtUtc <= now))).SingleAsync(token);
        return new BackupManagementResult<BackupTaskDetail>(BackupManagementCode.Succeeded, new(task, history, attempts, path, reconciliation, files));
    });
    private static IQueryable<BackupTaskSummary> Summaries(PlatformDbContext db, Guid? taskId = null, int page = 0)
    {
        var query = from t in db.BackupTasks
                    join s in db.BackupTaskSnapshots on t.Id equals s.TaskId
                    join f in db.BackupFiles.Where(file => file.Location == BackupFileLocation.Local)
                        on t.Id equals f.TaskId into files
                    from f in files.DefaultIfEmpty()
                    where taskId == null || t.Id == taskId
                    orderby t.CreatedAtUtc descending, t.Id descending
                    select new { Task = t, Snapshot = s, File = f };
        return query.Skip(page * 50).Take(51).Select(x => new BackupTaskSummary(x.Task.Id, x.Snapshot.PolicyName,
            x.Snapshot.DatabaseName, x.Task.Status.ToString(), x.Task.CurrentStage == null ? null : x.Task.CurrentStage.ToString(),
            x.Task.CreatedAtUtc, x.Task.CompletedAtUtc, x.Task.ErrorCode, x.Task.CancellationRequestedAtUtc != null,
            x.File == null ? null : x.File.LengthBytes));
    }
    private static IQueryable<BackupFileSummary> FileProjections(PlatformDbContext db, Guid taskId) =>
        from file in db.BackupFiles
        join attempt in db.BackupAttempts on file.AttemptId equals attempt.Id
        join snapshot in db.BackupTaskSnapshots on file.TaskId equals snapshot.TaskId
        join target in db.StorageTargets on file.StorageTargetId equals target.Id into targets
        from target in targets.DefaultIfEmpty()
        where file.TaskId == taskId
        orderby file.Location, file.ValidatedAtUtc
        select new BackupFileSummary(
            file.TaskId, file.DatabaseId, snapshot.DatabaseName, snapshot.InstanceName, snapshot.PolicyName,
            file.Location == BackupFileLocation.Local ? attempt.LocalSqlFilePath : "",
            file.LengthBytes, file.ValidatedAtUtc, file.RetentionDays, file.Id,
            file.Location.ToString(), file.Protocol.ToString(), target == null ? null : target.Name,
            file.Status.ToString(), file.DeletedAtUtc, file.DeletionErrorCode, file.Path);

    private static BackupManagementCode MapCode(BackupTaskStoreResultCode c) => c switch
    {
        BackupTaskStoreResultCode.Succeeded or BackupTaskStoreResultCode.AlreadyApplied or BackupTaskStoreResultCode.AlreadyExists => BackupManagementCode.Succeeded,
        BackupTaskStoreResultCode.AuthenticationRequired => BackupManagementCode.AuthenticationRequired,
        BackupTaskStoreResultCode.NotFound => BackupManagementCode.NotFound,
        BackupTaskStoreResultCode.ConfigurationUnavailable => BackupManagementCode.Invalid,
        _ => BackupManagementCode.Conflict,
    };
    private static async Task Authorize(PlatformDbContext db, AdminSession actor, DateTimeOffset nowUtc, CancellationToken token)
    {
        var admin = await db.AdminUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actor.AdminUserId, token);
        if (admin is not { IsEnabled: true } || admin.IsLockedOut(nowUtc)
            || !string.Equals(admin.SecurityStamp, actor.SecurityStamp, StringComparison.Ordinal)) throw new Rejected(BackupManagementCode.AuthenticationRequired);
    }
    private static async Task<BackupManagementResult<T>> Guard<T>(Func<Task<BackupManagementResult<T>>> action)
    {
        try { return await action(); }
        catch (Rejected e) { return new(e.Code); }
        catch (ArgumentException) { return new(BackupManagementCode.Invalid); }
        catch (DbUpdateConcurrencyException) { return new(BackupManagementCode.Conflict); }
        catch (DbUpdateException e) when (e.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 }) { return new(BackupManagementCode.Conflict); }
        catch (Exception e) when (e is DbException or DbUpdateException or InvalidOperationException) { return new(BackupManagementCode.Unavailable); }
    }
    private sealed class Rejected(BackupManagementCode code) : Exception { public BackupManagementCode Code { get; } = code; }
}
internal sealed class TaskEventStore(IDbContextFactory<PlatformDbContext> factory) : ITaskEventStore
{
    public async Task<IReadOnlyList<TaskInvalidation>> ReadPendingAsync(int take, CancellationToken token = default)
    {
        if (take is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(take));
        }

        await using var db = await factory.CreateDbContextAsync(token);
        return await db.TaskEvents.Where(x => x.PublishedAtUtc == null).OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.EventId)
            .Take(take).Select(x => new TaskInvalidation(x.EventId, x.TaskId, x.OccurredAtUtc)).ToListAsync(token);
    }
    public async Task AcknowledgeAsync(IReadOnlyList<Guid> ids, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var events = await db.TaskEvents.AsTracking().Where(x => ids.Contains(x.EventId) && x.PublishedAtUtc == null).ToListAsync(token);
        foreach (var e in events) e.MarkPublished(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(token);
    }
}
