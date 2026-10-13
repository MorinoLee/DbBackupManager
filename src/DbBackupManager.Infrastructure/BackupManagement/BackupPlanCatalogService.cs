using System.Data;
using System.Data.Common;
using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Application.Identity;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Domain.Entities;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DbBackupManager.Infrastructure.BackupManagement;

internal sealed class BackupPlanCatalogService(
    IDbContextFactory<PlatformDbContext> factory,
    TimeProvider clock,
    ILogger<BackupPlanCatalogService>? logger = null)
    : IBackupPlanCatalogService
{
    public Task<BackupPlanCatalogResult<IReadOnlyList<BackupPlanListItem>>> ListAsync(
        AdminSession actor, CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await Authorize(db, actor, clock.GetUtcNow(), cancellationToken);
            var plans = await Plans(db).ToListAsync(cancellationToken);
            IReadOnlyList<BackupPlanListItem> items = plans.Select(plan =>
            {
                var version = plan.CurrentVersion;
                return new BackupPlanListItem(
                    plan.Id,
                    plan.DatabaseId,
                    plan.Name,
                    plan.IsPaused,
                    Convert.ToBase64String(plan.RowVersion),
                    version.Number,
                    (int)version.Mode,
                    (int)version.StorageMode,
                    version.UseChecksum);
            }).ToArray();
            return new BackupPlanCatalogResult<IReadOnlyList<BackupPlanListItem>>(BackupPlanCatalogCode.Succeeded, items);
        });

    public Task<BackupPlanCatalogResult<BackupPlanView>> GetByDatabaseAsync(
        AdminSession actor, Guid databaseId, CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            if (databaseId == Guid.Empty)
            {
                throw new Rejected(BackupPlanCatalogCode.Invalid);
            }

            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await Authorize(db, actor, clock.GetUtcNow(), cancellationToken);
            var plan = await Plans(db).SingleOrDefaultAsync(item => item.DatabaseId == databaseId, cancellationToken)
                ?? throw new Rejected(BackupPlanCatalogCode.NotFound);
            return Succeeded(plan);
        });

    public Task<BackupPlanCatalogResult<BackupPlanView>> GetAsync(
        AdminSession actor, Guid planId, CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            if (planId == Guid.Empty)
            {
                throw new Rejected(BackupPlanCatalogCode.Invalid);
            }

            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await Authorize(db, actor, clock.GetUtcNow(), cancellationToken);
            var plan = await Plans(db).SingleOrDefaultAsync(item => item.Id == planId, cancellationToken)
                ?? throw new Rejected(BackupPlanCatalogCode.NotFound);
            return Succeeded(plan);
        });

    public Task<BackupPlanCatalogResult<BackupPlanView>> CreateAsync(
        AdminSession actor, BackupPlanInput input, CancellationToken cancellationToken = default) =>
        SaveAsync(actor, planId: null, version: null, input, cancellationToken);

    public Task<BackupPlanCatalogResult<BackupPlanView>> UpdateAsync(
        AdminSession actor, Guid planId, string? version, BackupPlanInput input, CancellationToken cancellationToken = default) =>
        SaveAsync(actor, planId, version, input, cancellationToken);

    public Task<BackupPlanCatalogResult<BackupRecoverySummary>> RecoveryAsync(
        AdminSession actor, Guid databaseId, CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            if (databaseId == Guid.Empty)
            {
                throw new Rejected(BackupPlanCatalogCode.Invalid);
            }

            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await Authorize(db, actor, clock.GetUtcNow(), cancellationToken);
            if (!await db.ManagedDatabases.AsNoTracking().AnyAsync(
                    item => item.Id == databaseId && !item.IsSystemDatabase, cancellationToken))
            {
                throw new Rejected(BackupPlanCatalogCode.NotFound);
            }

            // §6.2：没有当前计划时不合成恢复窗口，旧策略模型不获得新的计划恢复投影。
            var hasCurrentPlan = await db.BackupPlans.AsNoTracking().AnyAsync(
                plan => plan.DatabaseId == databaseId && EF.Property<Guid?>(plan, "_currentVersionId") != null,
                cancellationToken);
            if (!hasCurrentPlan)
            {
                await RemoteGapNotices.TryDrainPendingAsync(
                    factory,
                    databaseId,
                    cancellationToken,
                    logger,
                    RemoteGapNotices.OpportunisticLockTimeoutMilliseconds);
                return new BackupPlanCatalogResult<BackupRecoverySummary>(
                    BackupPlanCatalogCode.Succeeded,
                    new BackupRecoverySummary(databaseId, false, []));
            }

            var surface = await PlanLocationRetentionWork.LoadSurfaceAsync(
                db, databaseId, clock.GetUtcNow().ToUniversalTime(), cancellationToken);
            var locations = surface.Assessments.Select(item => BackupRecoverySurface.Project(
                item.Location,
                item.StorageTargetId,
                item.WindowDays,
                item.Conclusion,
                surface.Baseline,
                item.Copies.Select(copy => copy.Reason).ToArray())).ToArray();
            await RemoteGapNotices.TryDrainPendingAsync(
                factory,
                databaseId,
                cancellationToken,
                logger,
                RemoteGapNotices.OpportunisticLockTimeoutMilliseconds);
            return new BackupPlanCatalogResult<BackupRecoverySummary>(
                BackupPlanCatalogCode.Succeeded,
                new BackupRecoverySummary(databaseId, surface.HasCurrentPlan, locations));
        });

    private Task<BackupPlanCatalogResult<BackupPlanView>> SaveAsync(
        AdminSession actor, Guid? planId, string? version, BackupPlanInput input, CancellationToken cancellationToken) =>
        Guard(async () =>
        {
            if (input.DatabaseId == Guid.Empty || planId == Guid.Empty)
            {
                throw new Rejected(BackupPlanCatalogCode.Invalid);
            }

            var definition = BuildDefinition(input);
            await using var strategyDb = await factory.CreateDbContextAsync(cancellationToken);
            return await strategyDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                try
                {
                    await Authorize(db, actor, clock.GetUtcNow(), cancellationToken);
                    await RequireDatabaseAsync(db, input, cancellationToken);
                    await RequireTargetAsync(db, definition, input.IsPaused, cancellationToken);
                    BackupPlan plan;
                    var revised = false;
                    Guid? previousVersionId = null;
                    Guid? newVersionId = null;
                    if (planId is null)
                    {
                        var existingId = await db.BackupPlans.Where(item => item.DatabaseId == input.DatabaseId)
                            .Select(item => (Guid?)item.Id).SingleOrDefaultAsync(cancellationToken);
                        if (existingId is Guid id)
                        {
                            throw new PlanExists(id);
                        }

                        var now = clock.GetUtcNow().ToUniversalTime();
                        plan = BackupPlan.Create(Guid.NewGuid(), input.DatabaseId, input.Name, Guid.NewGuid(), definition, now);
                        if (input.IsPaused)
                        {
                            plan.Pause();
                        }

                        var versionId = plan.DetachCurrentVersion();
                        db.BackupPlans.Add(plan);
                        db.AuditRecords.Add(Audit(actor, "backup.plan.create", plan.Id));
                        await db.SaveChangesAsync(cancellationToken);
                        db.Entry(plan).Property<Guid?>("_currentVersionId").CurrentValue = versionId;
                    }
                    else
                    {
                        // 平台上下文默认不跟踪。不显式跟踪时，暂停和改名只留在内存，行版本不会变化。
                        plan = await db.BackupPlans.AsTracking().Include(item => item.Versions)
                                .SingleOrDefaultAsync(item => item.Id == planId, cancellationToken)
                            ?? throw new Rejected(BackupPlanCatalogCode.NotFound);
                        if (string.IsNullOrWhiteSpace(version))
                        {
                            throw new Rejected(BackupPlanCatalogCode.Invalid);
                        }

                        if (!string.Equals(version, Convert.ToBase64String(plan.RowVersion), StringComparison.Ordinal))
                        {
                            throw new Rejected(BackupPlanCatalogCode.VersionConflict);
                        }

                        if (plan.DatabaseId != input.DatabaseId)
                        {
                            throw new Rejected(BackupPlanCatalogCode.Invalid);
                        }

                        if (!string.Equals(plan.Name, input.Name, StringComparison.Ordinal))
                        {
                            plan.Rename(input.Name);
                        }

                        if (input.IsPaused)
                        {
                            plan.Pause();
                        }
                        else
                        {
                            plan.Resume();
                        }

                        previousVersionId = plan.CurrentVersionId;
                        var effective = LaterThan(clock.GetUtcNow().ToUniversalTime(), plan.CurrentVersion.EffectiveFromUtc);
                        newVersionId = Guid.NewGuid();
                        revised = plan.Revise(newVersionId.Value, definition, effective);
                        db.AuditRecords.Add(Audit(actor, "backup.plan.update", plan.Id));
                        if (revised)
                        {
                            db.Entry(plan).Property<Guid?>("_currentVersionId").CurrentValue = previousVersionId;
                        }
                    }

                    await db.SaveChangesAsync(cancellationToken);
                    if (revised && newVersionId is Guid createdVersionId)
                    {
                        db.Entry(plan).Property<Guid?>("_currentVersionId").CurrentValue = createdVersionId;
                        await db.SaveChangesAsync(cancellationToken);
                    }

                    await tx.CommitAsync(cancellationToken);
                    return Succeeded(plan);
                }
                catch (DbUpdateException exception) when (IsUniqueConstraint(exception))
                {
                    await using var lookup = await factory.CreateDbContextAsync(cancellationToken);
                    var existingId = await lookup.BackupPlans.AsNoTracking()
                        .Where(item => item.DatabaseId == input.DatabaseId)
                        .Select(item => (Guid?)item.Id)
                        .SingleOrDefaultAsync(cancellationToken);
                    if (existingId is Guid id)
                    {
                        return new BackupPlanCatalogResult<BackupPlanView>(
                            BackupPlanCatalogCode.AlreadyExists, ExistingPlanId: id);
                    }

                    throw;
                }
            });
        });

    private static IQueryable<BackupPlan> Plans(PlatformDbContext db) =>
        db.BackupPlans.AsNoTracking().Include(item => item.Versions);

    private static BackupPlanDefinition BuildDefinition(BackupPlanInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Any(char.IsControl))
        {
            throw new Rejected(BackupPlanCatalogCode.Invalid);
        }

        if (!Enum.IsDefined(typeof(BackupPlanMode), input.Mode)
            || !Enum.IsDefined(typeof(BackupStorageMode), input.StorageMode))
        {
            throw new Rejected(BackupPlanCatalogCode.Invalid);
        }

        if ((BackupPlanMode)input.Mode == BackupPlanMode.FullAndDifferentialAndLog)
        {
            throw new Rejected(BackupPlanCatalogCode.LogUnsupported);
        }

        try
        {
            return new BackupPlanDefinition(
                (BackupPlanMode)input.Mode,
                Schedule(input.FullSchedule),
                input.DifferentialSchedule is null ? null : Schedule(input.DifferentialSchedule),
                logInterval: null,
                input.TimeZoneId,
                (BackupStorageMode)input.StorageMode,
                input.StorageTargetId,
                input.LocalRecoveryWindowDays,
                input.RemoteRecoveryWindowDays,
                input.UseChecksum ?? true,
                input.UseCompression,
                input.BackupTimeoutMinutes,
                input.VerifyTimeoutMinutes,
                input.TransferTimeoutMinutes);
        }
        catch (ArgumentException)
        {
            throw new Rejected(BackupPlanCatalogCode.Invalid);
        }
    }

    private static RecurringBackupSchedule Schedule(BackupPlanScheduleInput input)
    {
        if (!Enum.IsDefined(typeof(BackupScheduleType), input.ScheduleType))
        {
            throw new Rejected(BackupPlanCatalogCode.Invalid);
        }

        return new RecurringBackupSchedule(
            (BackupScheduleType)input.ScheduleType,
            input.LocalTime,
            (BackupWeekdays)input.DaysOfWeek);
    }

    private static async Task RequireDatabaseAsync(PlatformDbContext db, BackupPlanInput input, CancellationToken token)
    {
        var valid = await (from database in db.ManagedDatabases
                           join instance in db.DatabaseInstances on database.InstanceId equals instance.Id
                           join server in db.DatabaseServers on instance.ServerId equals server.Id
                           where database.Id == input.DatabaseId && !database.IsSystemDatabase
                               && (input.IsPaused || database.IsManaged && database.IsAvailable && instance.IsEnabled
                                   && server.IsEnabled && instance.ConnectionStatus == SqlConnectionStatus.Connected)
                           select database.Id).AnyAsync(token);
        if (!valid)
        {
            throw new Rejected(BackupPlanCatalogCode.Invalid);
        }
    }

    private static async Task RequireTargetAsync(
        PlatformDbContext db, BackupPlanDefinition definition, bool paused, CancellationToken token)
    {
        if (definition.StorageMode == BackupStorageMode.LocalOnly)
        {
            return;
        }

        var valid = await (from target in db.StorageTargets
                           join credential in db.CredentialReferences on target.CredentialReferenceId equals credential.Id
                           where target.Id == definition.StorageTargetId && (paused || target.IsEnabled && credential.IsEnabled)
                           select target.Id).AnyAsync(token);
        if (!valid)
        {
            throw new Rejected(BackupPlanCatalogCode.Invalid);
        }
    }

    private static DateTimeOffset LaterThan(DateTimeOffset now, DateTimeOffset current) =>
        now <= current ? current.AddTicks(1) : now;

    private static AuditRecord Audit(AdminSession actor, string action, Guid planId) =>
        new(actor.AdminUserId, action, "BackupPlan", planId.ToString("N"), "Succeeded", null);

    private static BackupPlanCatalogResult<BackupPlanView> Succeeded(BackupPlan plan) =>
        new(BackupPlanCatalogCode.Succeeded, Map(plan));

    private static BackupPlanView Map(BackupPlan plan)
    {
        var version = plan.CurrentVersion;
        return new BackupPlanView(
            plan.Id,
            plan.DatabaseId,
            plan.Name,
            plan.IsPaused,
            Convert.ToBase64String(plan.RowVersion),
            version.Id,
            version.Number,
            version.Mode,
            version.FullSchedule,
            version.DifferentialSchedule,
            version.TimeZoneId,
            version.StorageMode,
            version.StorageTargetId,
            version.LocalRecoveryWindowDays,
            version.RemoteRecoveryWindowDays,
            version.UseChecksum,
            version.UseCompression,
            version.BackupTimeoutMinutes,
            version.VerifyTimeoutMinutes,
            version.TransferTimeoutMinutes,
            version.EffectiveFromUtc,
            []);
    }

    private static async Task Authorize(PlatformDbContext db, AdminSession actor, DateTimeOffset nowUtc, CancellationToken token)
    {
        var admin = await db.AdminUsers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == actor.AdminUserId, token);
        if (admin is not { IsEnabled: true }
            || admin.IsLockedOut(nowUtc)
            || !string.Equals(admin.SecurityStamp, actor.SecurityStamp, StringComparison.Ordinal))
        {
            throw new Rejected(BackupPlanCatalogCode.AuthenticationRequired);
        }
    }

    private static bool IsUniqueConstraint(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };

    private static async Task<BackupPlanCatalogResult<T>> Guard<T>(Func<Task<BackupPlanCatalogResult<T>>> action)
    {
        try
        {
            return await action();
        }
        catch (PlanExists exception)
        {
            return new BackupPlanCatalogResult<T>(BackupPlanCatalogCode.AlreadyExists, ExistingPlanId: exception.PlanId);
        }
        catch (Rejected exception)
        {
            return new BackupPlanCatalogResult<T>(exception.Code);
        }
        catch (ArgumentException)
        {
            return new BackupPlanCatalogResult<T>(BackupPlanCatalogCode.Invalid);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new BackupPlanCatalogResult<T>(BackupPlanCatalogCode.VersionConflict);
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException or InvalidOperationException)
        {
            return new BackupPlanCatalogResult<T>(BackupPlanCatalogCode.Unavailable);
        }
    }

    private sealed class Rejected(BackupPlanCatalogCode code) : Exception
    {
        public BackupPlanCatalogCode Code { get; } = code;
    }

    private sealed class PlanExists(Guid planId) : Exception
    {
        public Guid PlanId { get; } = planId;
    }
}
