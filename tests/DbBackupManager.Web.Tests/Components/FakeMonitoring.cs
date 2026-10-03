using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;

namespace DbBackupManager.Web.Tests.Components;

internal sealed class FakeMonitoring : IBackupMonitoringService
{
    public int Calls { get; private set; }
    public BackupSearch? LastSearch { get; private set; }
    public BackupManagementCode Code { get; set; } = BackupManagementCode.Succeeded;
    public BackupOverview Overview { get; set; } = new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(-24),
        121, 4, 3, 118, 25, 1, 2, 3, new("Online", DateTimeOffset.UtcNow),
        [new(Guid.NewGuid(), "测试备份", "合成库", "Succeeded", "VerifyLocal", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false, 4096)], [], []);
    public BackupPage<BackupFileSummary> Files { get; set; } = new([
        new(Guid.NewGuid(), Guid.NewGuid(), "合成库", "测试实例", "测试配置", @"D:\Synthetic\backup.bak", 4096, DateTimeOffset.UtcNow, 7,
            Guid.NewGuid(), "Local", "Smb", null, "Available", null, null, @"\\synthetic\share\backup.bak"),
        new(Guid.NewGuid(), Guid.NewGuid(), "合成库", "测试实例", "测试配置", "", 4096, DateTimeOffset.UtcNow, 14,
            Guid.NewGuid(), "Remote", "Sftp", "演示目标", "DeleteFailed", DateTimeOffset.UtcNow, "HostKeyMismatch",
            "/synthetic/backup.bak")], 0, 21);
    public TaskCompletionSource<BackupManagementResult<BackupPage<BackupFileSummary>>>? PendingFiles { get; set; }
    public Task<BackupManagementResult<BackupOverview>> OverviewAsync(AdminSession actor, CancellationToken token = default)
    { Calls++; return Task.FromResult(new BackupManagementResult<BackupOverview>(Code, Overview)); }
    public Task<BackupManagementResult<BackupPage<BackupTaskSummary>>> TasksAsync(AdminSession actor, BackupSearch search, CancellationToken token = default)
    { Calls++; LastSearch = search; return Task.FromResult(new BackupManagementResult<BackupPage<BackupTaskSummary>>(Code, new(Overview.RecentTasks, search.Page, 1))); }
    public Task<BackupManagementResult<BackupPage<BackupFileSummary>>> FilesAsync(AdminSession actor, BackupSearch search, CancellationToken token = default)
    {
        Calls++; LastSearch = search;
        var pending = PendingFiles; PendingFiles = null;
        return pending?.Task ?? Task.FromResult(new BackupManagementResult<BackupPage<BackupFileSummary>>(Code, Files));
    }
}
