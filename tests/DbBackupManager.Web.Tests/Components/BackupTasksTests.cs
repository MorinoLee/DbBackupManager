using System.Security.Claims;
using Bunit;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Web.Authentication;
using DbBackupManager.Web.BackupTasks;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Page = DbBackupManager.Web.Components.Pages.BackupTasks;

namespace DbBackupManager.Web.Tests.Components;

public sealed class BackupTasksTests : MudBlazorComponentTest
{
    private readonly FakeManagement _service = new();
    private readonly TaskRefreshNotifier _notifier = new();
    private readonly CircuitReconnectNotifier _reconnect = new();

    public BackupTasksTests()
    {
        Services.AddSingleton<IBackupManagementService>(_service);
        Services.AddSingleton<IBackupMonitoringService>(_service);
        Services.AddSingleton(_notifier);
        Services.AddSingleton(_reconnect);
    }

    [Fact]
    public void AnonymousCannotReadTasks()
    {
        var page = Render<Page>();
        Assert.Equal(0, _service.ListCalls);
        Assert.Empty(page.FindAll("[data-testid='task-detail']"));
    }

    [Fact]
    public void TaskListShowsChineseStatusAndLinksToDetailWithPage()
    {
        Authenticate();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/backup-tasks?page=1");
        var page = Render<Page>();

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='task-detail']")));
        Assert.Equal(1, _service.LastPage);
        Assert.Contains("待核对", page.Markup, StringComparison.Ordinal);
        Assert.Equal($"/backup-tasks/{FakeManagement.TaskId}?page=1&back=%2Fbackup-tasks%3Fpage%3D1",
            page.Find("[data-testid='task-detail']").GetAttribute("href"));
    }

    [Fact]
    public void PagerNavigatesWithPageInAddress()
    {
        Authenticate();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/backup-tasks?page=1");
        var page = Render<Page>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='task-detail']")));

        page.Find("[data-testid='previous-page']").Click();
        Assert.EndsWith("/backup-tasks", navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidationRequeriesFactsAndRevokedIdentityClearsList()
    {
        Authenticate();
        var page = Render<Page>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='task-detail']")));
        var before = _service.ListCalls;
        _notifier.Publish();
        page.WaitForAssertion(() => Assert.True(_service.ListCalls > before));

        _service.ListCode = BackupManagementCode.AuthenticationRequired;
        _notifier.Publish();
        page.WaitForAssertion(() => Assert.Contains("重新登录", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("合成库", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-testid='task-detail']"));
    }

    [Fact]
    public void ReconnectRequeriesLatestFactsAfterDisconnectWindow()
    {
        Authenticate();
        var page = Render<Page>();
        page.WaitForAssertion(() => Assert.Contains("合成库", page.Markup, StringComparison.Ordinal));

        _service.DatabaseName = "断线后库名";
        var before = _service.ListCalls;
        _reconnect.Publish();
        page.WaitForAssertion(() => Assert.Contains("断线后库名", page.Markup, StringComparison.Ordinal));
        Assert.True(_service.ListCalls > before);
        Assert.DoesNotContain("合成库", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void InFlightOldQueryMustNotOverwriteNewerFacts()
    {
        Authenticate();
        var page = Render<Page>();
        page.WaitForAssertion(() => Assert.Contains("合成库", page.Markup, StringComparison.Ordinal));

        var oldGate = new TaskCompletionSource<BackupManagementResult<BackupDashboard>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newGate = new TaskCompletionSource<BackupManagementResult<BackupDashboard>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Pending.Enqueue(oldGate);
        _service.Pending.Enqueue(newGate);

        var before = _service.ListCalls;
        _reconnect.Publish();
        page.WaitForAssertion(() => Assert.True(_service.ListCalls > before));
        page.Find("[data-testid='refresh-tasks']").Click();
        page.WaitForAssertion(() => Assert.True(_service.ListCalls > before + 1));

        newGate.SetResult(new BackupManagementResult<BackupDashboard>(
            BackupManagementCode.Succeeded, FakeManagement.Dashboard("最新库")));
        page.WaitForAssertion(() => Assert.Contains("最新库", page.Markup, StringComparison.Ordinal));

        oldGate.SetResult(new BackupManagementResult<BackupDashboard>(
            BackupManagementCode.Succeeded, FakeManagement.Dashboard("过时库")));
        page.WaitForAssertion(() => Assert.Contains("最新库", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("过时库", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("合成库", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void DisposeStopsReconnectRefresh()
    {
        Authenticate();
        var page = Render<Page>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='task-detail']")));
        var before = _service.ListCalls;

        page.Instance.Dispose();
        _reconnect.Publish();
        _notifier.Publish();

        Assert.Equal(before, _service.ListCalls);
    }

    [Fact]
    public void PeriodicCalibrationRequeriesWithoutNotice()
    {
        Services.AddSingleton(new MonitoringRefreshOptions { CalibrationInterval = TimeSpan.FromMilliseconds(50) });
        Authenticate();
        var page = Render<Page>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='task-detail']")));

        page.WaitForAssertion(() => Assert.True(_service.ListCalls >= 3), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void CancelRequestUsesStableRequestIdUntilAcknowledged()
    {
        Authenticate();
        _service.TaskStatus = "Running";
        var page = Render<Page>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='cancel-task']")));

        _service.MutateCode = BackupManagementCode.Unavailable;
        page.Find("[data-testid='cancel-task']").Click();
        page.WaitForAssertion(() => Assert.Single(_service.CancelRequests));
        _service.MutateCode = BackupManagementCode.Succeeded;
        page.Find("[data-testid='cancel-task']").Click();
        page.WaitForAssertion(() => Assert.Equal(2, _service.CancelRequests.Count));
        Assert.Equal(_service.CancelRequests[0], _service.CancelRequests[1]);
    }

    private void Authenticate()
    {
        SignIn("管理员");
        Authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("N")),
            new Claim(AdminAuthenticationDefaults.SecurityStampClaimType, "synthetic"));
    }

    private sealed class FakeManagement : IBackupManagementService, IBackupMonitoringService
    {
        public async Task<BackupManagementResult<BackupPage<BackupTaskSummary>>> TasksAsync(AdminSession actor, BackupSearch search, CancellationToken token = default)
        {
            var result = await ListAsync(actor, search.Page, token);
            return new(result.Code, new(result.Value!.Tasks, search.Page, 21));
        }
        public Task<BackupManagementResult<BackupOverview>> OverviewAsync(AdminSession actor, CancellationToken token = default) => throw new NotSupportedException();
        public Task<BackupManagementResult<BackupPage<BackupFileSummary>>> FilesAsync(AdminSession actor, BackupSearch search, CancellationToken token = default) => throw new NotSupportedException();
        public static readonly Guid TaskId = Guid.NewGuid();
        public string TaskStatus = "NeedsAttention";
        public string DatabaseName = "合成库";
        public int ListCalls;
        public int LastPage { get; private set; }
        public Queue<TaskCompletionSource<BackupManagementResult<BackupDashboard>>> Pending { get; } = new();
        public List<Guid> CancelRequests { get; } = [];
        public BackupManagementCode ListCode = BackupManagementCode.Succeeded;
        public BackupManagementCode MutateCode = BackupManagementCode.Succeeded;

        public static BackupDashboard Dashboard(string databaseName)
        {
            var task = new BackupTaskSummary(TaskId, "测试备份", databaseName, "NeedsAttention", "Backup", DateTimeOffset.UtcNow, null, "synthetic_unknown", false, null);
            return new([], [], [task], false);
        }

        public Task<BackupManagementResult<BackupDashboard>> ListAsync(AdminSession actor, int page = 0, CancellationToken token = default)
        {
            ListCalls++;
            LastPage = page;
            if (Pending.TryDequeue(out var pending))
            {
                return pending.Task;
            }

            var task = new BackupTaskSummary(TaskId, "测试备份", DatabaseName, TaskStatus, "Backup", DateTimeOffset.UtcNow, null, "synthetic_unknown", false, null);
            return Task.FromResult(new BackupManagementResult<BackupDashboard>(ListCode, new([], [], [task], false)));
        }

        public Task<BackupManagementResult<ManualBackupPolicy>> SavePolicyAsync(AdminSession actor, Guid? id, string? version, ManualBackupInput input, CancellationToken token = default) => throw new NotSupportedException();
        public Task<BackupManagementResult<Guid>> StartAsync(AdminSession actor, Guid policyId, Guid requestId, CancellationToken token = default) => throw new NotSupportedException();
        public Task<BackupManagementResult<Guid>> CancelAsync(AdminSession actor, Guid taskId, Guid requestId, CancellationToken token = default)
        { CancelRequests.Add(requestId); return Task.FromResult(new BackupManagementResult<Guid>(MutateCode, taskId)); }
        public Task<BackupManagementResult<Guid>> RetryAsync(AdminSession actor, Guid taskId, Guid requestId, CancellationToken token = default) => throw new NotSupportedException();
        public Task<BackupManagementResult<Guid>> RequestReconciliationAsync(AdminSession actor, Guid taskId, Guid requestId, CancellationToken token = default) => throw new NotSupportedException();
        public Task<BackupManagementResult<Guid>> ConfirmFailedAsync(AdminSession actor, Guid taskId, Guid requestId, bool evidenceReviewed, CancellationToken token = default) => throw new NotSupportedException();
        public Task<BackupManagementResult<BackupTaskDetail>> DetailAsync(AdminSession actor, Guid taskId, CancellationToken token = default) => throw new NotSupportedException();
    }
}
