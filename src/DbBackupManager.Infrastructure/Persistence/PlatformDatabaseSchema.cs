using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

public static class PlatformDatabaseSchema
{
    public static async Task<bool> IsCurrentAsync(
        PlatformDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var expected = context.Database.GetMigrations().ToArray();
        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        return expected.Length > 0
            && expected.SequenceEqual(applied, StringComparer.Ordinal)
            && !context.Database.HasPendingModelChanges();
    }
}
