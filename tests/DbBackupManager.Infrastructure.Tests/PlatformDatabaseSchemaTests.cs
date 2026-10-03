using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class PlatformDatabaseSchemaTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Fact]
    public async Task CurrentHistoryIsAcceptedWithoutWriting()
    {
        await using var context = database.CreateContext();
        var before = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.True(await PlatformDatabaseSchema.IsCurrentAsync(context));
        Assert.Equal(before, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
    }

    [Theory]
    [InlineData("20260903061005_InitialPlatformSchema")]
    [InlineData("20260929084529_InitialPlatformSchema")]
    public async Task SameNameFromOldBaselineIsRejected(string oldMigration)
    {
        await using var context = database.CreateContext();
        var current = Assert.Single(context.Database.GetMigrations());
        try
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [__EFMigrationsHistory] SET [MigrationId] = {oldMigration} WHERE [MigrationId] = {current}");
            Assert.False(await PlatformDatabaseSchema.IsCurrentAsync(context));
            Assert.Equal(oldMigration, Assert.Single(await context.Database.GetAppliedMigrationsAsync()));
        }
        finally
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [__EFMigrationsHistory] SET [MigrationId] = {current} WHERE [MigrationId] = {oldMigration}");
        }
    }

    [Fact]
    public async Task UnexpectedMigrationAlongsideCurrentBaselineIsRejected()
    {
        await using var context = database.CreateContext();
        const string unexpected = "20260930000001_Unexpected";
        try
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES ({unexpected}, '10.0.11')");
            Assert.False(await PlatformDatabaseSchema.IsCurrentAsync(context));
        }
        finally
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM [__EFMigrationsHistory] WHERE [MigrationId] = {unexpected}");
        }
    }

    [Fact]
    public async Task MissingAppliedMigrationIsRejected()
    {
        await using var context = database.CreateContext();
        var current = Assert.Single(context.Database.GetMigrations());
        try
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM [__EFMigrationsHistory] WHERE [MigrationId] = {current}");
            Assert.False(await PlatformDatabaseSchema.IsCurrentAsync(context));
        }
        finally
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES ({current}, '10.0.11')");
        }
    }
}
