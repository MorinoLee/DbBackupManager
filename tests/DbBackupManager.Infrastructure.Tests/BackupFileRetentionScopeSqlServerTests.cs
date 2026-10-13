using System.Data.Common;
using DbBackupManager.Application.BackupSets;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupFileRetentionScopeSqlServerTests(PlatformDatabaseSqlServerFixture database)
    : IClassFixture<PlatformDatabaseSqlServerFixture>
{
    [Fact]
    public async Task PageFileReasonsDoNotLoadEveryHistoricalCopy()
    {
        var work = await BackupSetTestData.WorkAsync(database);
        var command = BackupSetTestData.Command(work);
        await BackupSetTestData.RegisterAsync(database, command);
        using var provider = database.CreateServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<BackupSetRegistrationService>();
        var copy = Copy(work);
        copy.AssociateBackupSet(command.BackupSetId);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, (await service.RegisterCopyAsync(work.Lease, copy)).Code);
        var historicalWork = await BackupSetTestData.WorkAsync(database, work.Snapshot.Identity.DatabaseId);
        var historicalCommand = BackupSetTestData.Command(historicalWork);
        await BackupSetTestData.RegisterAsync(database, historicalCommand);
        var historical = Copy(historicalWork);
        historical.AssociateBackupSet(historicalCommand.BackupSetId);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, (await service.RegisterCopyAsync(historicalWork.Lease, historical)).Code);

        var other = await BackupSetTestData.WorkAsync(database);
        var otherCommand = BackupSetTestData.Command(other);
        await BackupSetTestData.RegisterAsync(database, otherCommand);
        var otherCopy = Copy(other);
        otherCopy.AssociateBackupSet(otherCommand.BackupSetId);
        Assert.Equal(BackupTaskStoreResultCode.Succeeded, (await service.RegisterCopyAsync(other.Lease, otherCopy)).Code);

        var collector = new SqlCommandCollector();
        await using var db = database.CreateContext(collector);
        var reasons = await PlanLocationRetentionWork.ReasonsForFilesAsync(
            db, [copy.Id], DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(reasons.ContainsKey(copy.Id));
        Assert.False(reasons.ContainsKey(historical.Id));
        Assert.False(reasons.ContainsKey(otherCopy.Id));
        var fileCommands = collector.Commands.Where(text => text.Contains("[BackupFiles]", StringComparison.Ordinal)).ToArray();
        var setCommands = collector.Commands.Where(text => text.Contains("[BackupSets]", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(fileCommands);
        Assert.DoesNotContain(fileCommands, LoadsEntireDatabase);
        Assert.DoesNotContain(setCommands, LoadsEntireDatabase);
    }

    private static bool LoadsEntireDatabase(string text)
    {
        var filtersByDatabase = text.Contains("[DatabaseId] =", StringComparison.Ordinal)
            || text.Contains("[DatabaseId]=", StringComparison.Ordinal)
            || text.Contains("[DatabaseId] IN", StringComparison.Ordinal);
        return filtersByDatabase && !text.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase);
    }

    private static BackupFile Copy(BackupExecutionWorkItem work) =>
        BackupFile.CreateLocal(
            Guid.NewGuid(),
            work.Task.TaskId,
            work.Attempt.Id,
            work.Snapshot.Identity.DatabaseId,
            work.Snapshot.Identity.ServerId,
            work.Snapshot.WorkerSourceEndpoint.Protocol,
            work.Attempt.WorkerSourceFilePath,
            1024,
            DateTimeOffset.UtcNow,
            7);

    private sealed class SqlCommandCollector : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
