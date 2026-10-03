using DbBackupManager.Application.BackupTasks;

namespace DbBackupManager.Web.Tests;

internal sealed class PlatformReadinessStub(bool ready = true) : IPlatformDatabaseReadiness
{
    public bool IsReady { get; set; } = ready;
    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
