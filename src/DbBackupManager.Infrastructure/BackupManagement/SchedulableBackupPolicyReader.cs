using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.BackupManagement;

internal sealed class SchedulableBackupPolicyReader(
    IDbContextFactory<PlatformDbContext> factory) : ISchedulableBackupPolicyReader
{
    public async Task<IReadOnlyList<SchedulableBackupPolicy>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.BackupPolicies
            .AsNoTracking()
            .Where(policy => policy.IsEnabled
                && !policy.IsManualOnly
                && policy.ScheduleEffectiveFromUtc != null)
            .OrderBy(policy => policy.Id)
            .Select(policy => new SchedulableBackupPolicy(
                policy.Id,
                policy.ScheduleType,
                policy.LocalTime,
                policy.DaysOfWeek,
                policy.TimeZoneId,
                policy.ScheduleEffectiveFromUtc!.Value))
            .ToListAsync(cancellationToken);
    }
}
