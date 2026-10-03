using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.BackupManagement;

internal sealed class WorkerHeartbeatStore(IDbContextFactory<PlatformDbContext> factory) : IWorkerHeartbeatStore
{
    public async Task RegisterAsync(Guid processId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            MERGE [WorkerHeartbeats] WITH (HOLDLOCK) AS target
            USING (SELECT 1 AS Id) AS source ON target.Id = source.Id
            WHEN MATCHED THEN UPDATE SET ProcessId = {processId}, LastSeenAtUtc = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), IsStopped = 0
            WHEN NOT MATCHED THEN INSERT (Id, ProcessId, LastSeenAtUtc, IsStopped)
                VALUES (1, {processId}, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), 0);
            """, token);
    }

    public async Task PulseAsync(Guid processId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [WorkerHeartbeats] SET LastSeenAtUtc = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')
            WHERE Id = 1 AND ProcessId = {processId} AND IsStopped = 0;
            """, token);
    }

    public async Task StopAsync(Guid processId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [WorkerHeartbeats] SET IsStopped = 1 WHERE Id = 1 AND ProcessId = {processId};", token);
    }
}
