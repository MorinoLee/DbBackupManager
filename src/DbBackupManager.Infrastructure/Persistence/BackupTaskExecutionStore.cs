using DbBackupManager.Application.BackupTasks;
using Microsoft.EntityFrameworkCore;

namespace DbBackupManager.Infrastructure.Persistence;

internal sealed class BackupTaskExecutionStore : IBackupTaskExecutionStore
{
    private readonly BackupTaskCommandStore _commands;

    public BackupTaskExecutionStore(IDbContextFactory<PlatformDbContext> contextFactory)
    {
        _commands = new BackupTaskCommandStore(new BackupTaskPersistence(contextFactory));
    }

    public Task<BackupTaskStoreResult<BackupTaskStateModel>> RequestCancellationAsync(
        BackupTaskMutationCommand command,
        CancellationToken cancellationToken = default) =>
        _commands.RequestCancellationAsync(command, cancellationToken);
}
