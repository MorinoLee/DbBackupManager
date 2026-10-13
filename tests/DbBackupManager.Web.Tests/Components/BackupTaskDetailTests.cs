using System.Security.Claims;
using Bunit;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Web.Authentication;
using DbBackupManager.Web.BackupTasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Page = DbBackupManager.Web.Components.Pages.BackupTaskDetail;

namespace DbBackupManager.Web.Tests.Components;

public sealed class BackupTaskDetailTests : MudBlazorComponentTest
{
    private readonly FakeManagement _service = new();
    private readonly TaskRefreshNotifier _notifier = new();
    private readonly CircuitReconnectNotifier _reconnect = new();

    public BackupTaskDetailTests()
    {
        Services.AddSingleton<IBackupManagementService>(_service);
        Services.AddSingleton(_notifier);
        Services.AddSingleton(_reconnect);
    }

    [Fact]
    public void AnonymousCannotReadDetail()
    {
        var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));
        Assert.Equal(0, _service.DetailCalls);
        Assert.Empty(page.FindAll("[data-testid='task-summary']"));
    }

    [Fact]
    public void DetailShowsSummaryWarningAttemptsAndHistory()
    {
        Authenticate();
        var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='task-summary']")));
        Assert.Contains("外部执行结果尚未确定", page.Markup, StringComparison.Ordinal);
        var facts = page.Find("[data-testid='task-facts']");
        Assert.Contains("SQL 执行：未知", facts.TextContent, StringComparison.Ordinal);
        Assert.Contains("文件校验：未知", facts.TextContent, StringComparison.Ordinal);
        Assert.Contains("传输：未知", facts.TextContent, StringComparison.Ordinal);
        Assert.Contains("恢复依赖：未知", facts.TextContent, StringComparison.Ordinal);
        Assert.Contains("计划版本：未知", facts.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("成功", facts.TextContent, StringComparison.Ordinal);
        Assert.Contains("已验证本地副本", page.Markup, StringComparison.Ordinal);
        Assert.Contains("第一次调用", page.Find("[data-testid='task-attempts']").TextContent, StringComparison.Ordinal);
        Assert.Contains("等待 Worker 领取", page.Find("[data-testid='task-history']").TextContent, StringComparison.Ordinal);
        var files = page.Find("[data-testid='task-files']");
        Assert.Contains("本地", files.TextContent, StringComparison.Ordinal);
        Assert.Contains("远程", files.TextContent, StringComparison.Ordinal);
        Assert.Contains("删除失败", files.TextContent, StringComparison.Ordinal);
        Assert.Contains("主机密钥指纹不匹配", files.TextContent, StringComparison.Ordinal);
        Assert.Contains("已超出恢复窗口，且不再被恢复依赖需要", files.TextContent, StringComparison.Ordinal);
        Assert.Contains("远端还没有确认基准完整备份，暂时不能恢复", files.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("outside_window_unneeded", files.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("remote_base_unconfirmed", files.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("HostKeyMismatch", files.TextContent, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-testid='delete-file']"));
        var panel = page.Find("[data-testid='reconciliation-panel']");
        Assert.Contains("已完成核对次数：1", panel.TextContent, StringComparison.Ordinal);
        Assert.Contains("下次核对时间：", panel.TextContent, StringComparison.Ordinal);
        Assert.Contains("核对结果待确认", panel.TextContent, StringComparison.Ordinal);
        Assert.Contains("当前没有自动核对、重试或确认失败", panel.TextContent, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-testid='reconcile-task']"));
        Assert.Empty(page.FindAll("[data-testid='retry-task']"));
        Assert.Empty(page.FindAll("[data-testid='confirm-failed-task']"));
        Assert.DoesNotContain("synthetic_unknown", panel.TextContent, StringComparison.Ordinal);
        // 错误码收进技术详情，不在摘要或核对面板直接外露。
        Assert.Contains("synthetic_unknown", page.Find("[data-testid='technical-details']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void BackLinkPreservesListPage()
    {
        Authenticate();
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/backup-tasks/{FakeManagement.TaskId}?page=2");
        var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='back-to-tasks']")));
        Assert.Equal("/backup-tasks?page=2", page.Find("[data-testid='back-to-tasks']").GetAttribute("href"));
    }

    [Fact]
    public void InvalidationRequeriesDetailAndRevokedIdentityClearsSensitiveContent()
    {
        Authenticate();
        var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));
        page.WaitForAssertion(() => Assert.Contains("外部执行结果尚未确定", page.Markup, StringComparison.Ordinal));
        var before = _service.DetailCalls;
        _notifier.Publish();
        page.WaitForAssertion(() => Assert.True(_service.DetailCalls > before));

        _service.Code = BackupManagementCode.AuthenticationRequired;
        _notifier.Publish();
        page.WaitForAssertion(() => Assert.Contains("重新登录", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("外部执行结果尚未确定", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-testid='task-summary']"));
    }

    [Fact]
    public void ReconnectRequeriesLatestFactsAfterDisconnectWindow()
    {
        Authenticate();
        var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));
        page.WaitForAssertion(() => Assert.Contains("外部执行结果尚未确定", page.Markup, StringComparison.Ordinal));

        _service.Code = BackupManagementCode.NotFound;
        var before = _service.DetailCalls;
        _reconnect.Publish();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='empty-back-to-tasks']")));
        Assert.True(_service.DetailCalls > before);
        Assert.DoesNotContain("外部执行结果尚未确定", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconnectDuringQueryTriggersCompensationRequery()
    {
        Authenticate();
        var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));
        page.WaitForAssertion(() => Assert.Contains("外部执行结果尚未确定", page.Markup, StringComparison.Ordinal));

        var gate = new TaskCompletionSource<BackupManagementResult<BackupTaskDetail>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Gate = gate;
        var before = _service.DetailCalls;
        _reconnect.Publish();
        page.WaitForAssertion(() => Assert.True(_service.DetailCalls > before));

        _reconnect.Publish();
        _service.Gate = null;
        gate.SetResult(new BackupManagementResult<BackupTaskDetail>(
            BackupManagementCode.Succeeded, FakeManagement.DetailFor(FakeManagement.TaskId)));

        page.WaitForAssertion(() => Assert.True(_service.DetailCalls > before + 1));
    }

    [Fact]
    public void DisposeStopsReconnectRefresh()
    {
        Authenticate();
        var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));
        page.WaitForAssertion(() => Assert.Contains("外部执行结果尚未确定", page.Markup, StringComparison.Ordinal));
        var before = _service.DetailCalls;

        page.Instance.Dispose();
        _reconnect.Publish();
        _notifier.Publish();

        Assert.Equal(before, _service.DetailCalls);
    }

    [Fact]
    public void NotFoundShowsEmptyStateWithBackEntry()
    {
        Authenticate();
        _service.Code = BackupManagementCode.NotFound;
        var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='empty-back-to-tasks']")));
        Assert.Equal("/backup-tasks", page.Find("[data-testid='empty-back-to-tasks']").GetAttribute("href"));
    }

    [Fact]
    public void NoticeArrivingDuringQueryTriggersCompensationRequery()
    {
        Authenticate();
        var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));
        page.WaitForAssertion(() => Assert.Contains("外部执行结果尚未确定", page.Markup, StringComparison.Ordinal));

        var gate = new TaskCompletionSource<BackupManagementResult<BackupTaskDetail>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Gate = gate;
        var before = _service.DetailCalls;
        _notifier.Publish();
        page.WaitForAssertion(() => Assert.True(_service.DetailCalls > before));

        // 查询进行期间到达最后一次通知：必须被记录，而不是随忙碌丢弃。
        _notifier.Publish();
        _service.Gate = null;
        gate.SetResult(new BackupManagementResult<BackupTaskDetail>(
            BackupManagementCode.Succeeded, FakeManagement.DetailFor(FakeManagement.TaskId)));

        // 查询完成后补偿一次重新查询，避免长期停留在旧状态。
        page.WaitForAssertion(() => Assert.True(_service.DetailCalls > before + 1));
    }

    [Fact]
    public void PeriodicCalibrationRequeriesWithoutNotice()
    {
        var original = Page.CalibrationInterval;
        Page.CalibrationInterval = TimeSpan.FromMilliseconds(50);
        try
        {
            Authenticate();
            var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));
            page.WaitForAssertion(() => Assert.Contains("外部执行结果尚未确定", page.Markup, StringComparison.Ordinal));

            // 不再发布任何通知，周期校准仍应继续向 Platform DB 对齐。
            page.WaitForAssertion(() => Assert.True(_service.DetailCalls >= 3), TimeSpan.FromSeconds(5));
        }
        finally
        {
            Page.CalibrationInterval = original;
        }
    }

    [Fact]
    public void SwitchingRouteIdClearsPreviousTaskStateAndMutationRequestIds()
    {
        Authenticate();
        _service.ServePendingCancelable = true;
        _service.CancelCode = BackupManagementCode.Conflict;
        var host = Render<DetailHost>(parameters => parameters.Add(x => x.InitialId, FakeManagement.TaskId));
        host.WaitForAssertion(() => Assert.NotNull(host.Find("[data-testid='cancel-task']")));

        // 任务 A 取消失败：RequestId 保留用于幂等重试。
        host.Find("[data-testid='cancel-task']").Click();
        host.WaitForAssertion(() => Assert.Single(_service.CancelCalls));

        // 组件复用切换到任务 B：旧状态清空、按新 Id 重新查询，且不得复用任务 A 的 RequestId。
        _service.CancelCode = BackupManagementCode.Succeeded;
        host.InvokeAsync(() => host.Instance.Show(FakeManagement.OtherTaskId));
        host.WaitForAssertion(() => Assert.Contains("另一备份", host.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("测试备份", host.Markup, StringComparison.Ordinal);

        host.Find("[data-testid='cancel-task']").Click();
        host.WaitForAssertion(() => Assert.Equal(2, _service.CancelCalls.Count));

        Assert.Equal(FakeManagement.TaskId, _service.CancelCalls[0].TaskId);
        Assert.Equal(FakeManagement.OtherTaskId, _service.CancelCalls[1].TaskId);
        Assert.NotEqual(_service.CancelCalls[0].RequestId, _service.CancelCalls[1].RequestId);
    }

    [Fact]
    public void InFlightOldTaskResponseMustNotOverwriteAfterRouteSwitch()
    {
        Authenticate();
        var host = Render<DetailHost>(parameters => parameters.Add(x => x.InitialId, FakeManagement.TaskId));
        host.WaitForAssertion(() => Assert.Contains("测试备份", host.Markup, StringComparison.Ordinal));

        // 暂停任务 A 的一次通知刷新查询。
        var gate = new TaskCompletionSource<BackupManagementResult<BackupTaskDetail>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Gate = gate;
        var before = _service.DetailCalls;
        _notifier.Publish();
        host.WaitForAssertion(() => Assert.True(_service.DetailCalls > before));

        // 切换到任务 B：A 摘要清空，B 的查询因 A 在途登记为待补偿。
        host.InvokeAsync(() => host.Instance.Show(FakeManagement.OtherTaskId));
        host.WaitForAssertion(() => Assert.DoesNotContain("测试备份", host.Markup, StringComparison.Ordinal));

        // B 的补偿查询返回 Unavailable，随后才释放 A 的旧查询并成功返回 A 详情。
        _service.Code = BackupManagementCode.Unavailable;
        _service.Gate = null;
        gate.SetResult(new BackupManagementResult<BackupTaskDetail>(
            BackupManagementCode.Succeeded, FakeManagement.DetailFor(FakeManagement.TaskId)));

        // A 的过期响应必须被丢弃：页面显示 B 查询的失败错误，而不是重新出现 A 的内容。
        host.WaitForAssertion(() => Assert.NotNull(host.Find("[role='alert']")));
        Assert.DoesNotContain("测试备份", host.Markup, StringComparison.Ordinal);
        Assert.Equal(FakeManagement.OtherTaskId, _service.LastRequestedId);
    }

    [Fact]
    public void InFlightOldTaskFailureMustNotOverwriteNewTaskSuccess()
    {
        Authenticate();
        var host = Render<DetailHost>(parameters => parameters.Add(x => x.InitialId, FakeManagement.TaskId));
        host.WaitForAssertion(() => Assert.Contains("测试备份", host.Markup, StringComparison.Ordinal));

        var gate = new TaskCompletionSource<BackupManagementResult<BackupTaskDetail>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Gate = gate;
        _notifier.Publish();
        host.WaitForAssertion(() => Assert.True(_service.DetailCalls > 1));

        host.InvokeAsync(() => host.Instance.Show(FakeManagement.OtherTaskId));

        // 旧查询返回错误，新任务查询正常：错误不得写回，B 正常显示。
        _service.Gate = null;
        gate.SetResult(new BackupManagementResult<BackupTaskDetail>(BackupManagementCode.Unavailable, null));

        host.WaitForAssertion(() => Assert.Contains("另一备份", host.Markup, StringComparison.Ordinal));
        Assert.Empty(host.FindAll("[role='alert']"));
    }

    [Fact]
    public void MutationInFlightDuringRouteSwitchDoesNotWriteMessageOrReuseRequest()
    {
        Authenticate();
        _service.ServePendingCancelable = true;
        var host = Render<DetailHost>(parameters => parameters.Add(x => x.InitialId, FakeManagement.TaskId));
        host.WaitForAssertion(() => Assert.NotNull(host.Find("[data-testid='cancel-task']")));

        // 任务 A 的取消命令在途。
        var cancelGate = new TaskCompletionSource<BackupManagementResult<Guid>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _service.CancelGate = cancelGate;
        host.Find("[data-testid='cancel-task']").Click();
        host.WaitForAssertion(() => Assert.Single(_service.CancelCalls));

        // 命令执行中切换到 B，随后 A 的命令才成功返回。
        host.InvokeAsync(() => host.Instance.Show(FakeManagement.OtherTaskId));
        _service.CancelGate = null;
        cancelGate.SetResult(new BackupManagementResult<Guid>(BackupManagementCode.Succeeded, FakeManagement.TaskId));

        // 旧任务命令完成：不写成功消息；B 的补偿查询正常落地。
        host.WaitForAssertion(() => Assert.Contains("另一备份", host.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("已提交取消请求", host.Markup, StringComparison.Ordinal);
        Assert.Equal(FakeManagement.OtherTaskId, _service.LastRequestedId);
    }

    [Fact]
    public void KnownReconciliationReasonIsMappedWithoutEchoingCode()
    {
        Authenticate();
        _service.ReconciliationReasonCode = "reconciliation.file_missing";
        var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='reconciliation-panel']")));
        var panel = page.Find("[data-testid='reconciliation-panel']").TextContent;
        Assert.Contains("只读核对未找到任务专属备份文件", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("reconciliation.file_missing", panel, StringComparison.Ordinal);
    }

    /// <summary>模拟路由器复用页面组件实例：同一宿主只改变 Id 参数重新下发。</summary>
    private sealed class DetailHost : ComponentBase
    {
        private Guid _taskId;

        [Parameter]
        public Guid InitialId { get; set; }

        protected override void OnInitialized() => _taskId = InitialId;

        public void Show(Guid taskId)
        {
            _taskId = taskId;
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<Page>(0);
            builder.AddComponentParameter(1, nameof(Page.Id), _taskId);
            builder.CloseComponent();
        }
    }

    private void Authenticate()
    {
        SignIn("管理员");
        Authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("N")),
            new Claim(AdminAuthenticationDefaults.SecurityStampClaimType, "synthetic"));
    }

    private sealed class FakeManagement : IBackupManagementService
    {
        public static readonly Guid TaskId = Guid.NewGuid();
        public static readonly Guid OtherTaskId = Guid.NewGuid();
        private static readonly BackupTaskSummary Summary = new(TaskId, "测试备份", "合成库", "NeedsAttention", "Backup",
            DateTimeOffset.UtcNow, null, "synthetic_unknown", false, 2048);
        private static readonly BackupTaskDetail Detail = new(Summary,
            [new BackupTaskHistory(DateTimeOffset.UtcNow, "Pending", null, "task.created")],
            [new BackupAttemptSummary(1, "第一次调用", 2048, DateTimeOffset.UtcNow)],
            @"D:\Synthetic\backup.bak",
            new BackupReconciliationSummary(1, DateTimeOffset.UtcNow.AddMinutes(1), "synthetic_unknown", true),
            [
                new BackupFileSummary(TaskId, Guid.NewGuid(), "合成库", "测试实例", "测试备份", @"D:\Synthetic\backup.bak",
                    2048, DateTimeOffset.UtcNow, 7, Guid.NewGuid(), "Local", "Smb", null, "Available", null, null,
                    @"\\synthetic\share\backup.bak")
                {
                    BackupSetId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                    RetentionReason = "outside_window_unneeded",
                },
                new BackupFileSummary(TaskId, Guid.NewGuid(), "合成库", "测试实例", "测试备份", "", 2048, DateTimeOffset.UtcNow,
                    14, Guid.NewGuid(), "Remote", "Sftp", "演示目标", "DeleteFailed", DateTimeOffset.UtcNow,
                    "HostKeyMismatch", "/synthetic/backup.bak")
                {
                    DifferentialBaseBackupSetId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                    RetentionReason = "remote_base_unconfirmed",
                },
            ]);

        public int DetailCalls;
        public Guid LastRequestedId;
        public BackupManagementCode Code = BackupManagementCode.Succeeded;
        public BackupManagementCode CancelCode = BackupManagementCode.Succeeded;
        public bool ServePendingCancelable;
        public bool CanRequest = true;
        public string? ReconciliationReasonCode = "synthetic_unknown";
        public TaskCompletionSource<BackupManagementResult<BackupTaskDetail>>? Gate;
        public TaskCompletionSource<BackupManagementResult<Guid>>? CancelGate;
        public List<(Guid TaskId, Guid RequestId)> CancelCalls { get; } = [];

        public static BackupTaskDetail DetailFor(Guid taskId)
        {
            if (taskId == TaskId)
            {
                return Detail;
            }

            var summary = new BackupTaskSummary(taskId, "另一备份", "合成库乙", "NeedsAttention", "Backup",
                DateTimeOffset.UtcNow, null, null, false, null);
            return new BackupTaskDetail(summary, [], [], null,
                new BackupReconciliationSummary(0, DateTimeOffset.UtcNow, null, true), []);
        }

        public Task<BackupManagementResult<BackupDashboard>> ListAsync(AdminSession actor, int page = 0, CancellationToken token = default) => throw new NotSupportedException();
        public Task<BackupManagementResult<Guid>> CancelAsync(AdminSession actor, Guid taskId, Guid requestId, CancellationToken token = default)
        {
            CancelCalls.Add((taskId, requestId));
            if (CancelGate is not null)
            {
                return CancelGate.Task;
            }

            return Task.FromResult(new BackupManagementResult<Guid>(CancelCode,
                CancelCode == BackupManagementCode.Succeeded ? taskId : Guid.Empty));
        }
        public Task<BackupManagementResult<BackupTaskDetail>> DetailAsync(AdminSession actor, Guid taskId, CancellationToken token = default)
        {
            DetailCalls++;
            LastRequestedId = taskId;
            if (Gate is not null)
            {
                return Gate.Task;
            }

            return Task.FromResult(new BackupManagementResult<BackupTaskDetail>(Code, ResolveDetail(taskId)));
        }

        private BackupTaskDetail ResolveDetail(Guid taskId)
        {
            if (!ServePendingCancelable)
            {
                var detail = DetailFor(taskId);
                return detail with
                {
                    Task = detail.Task,
                    Reconciliation = new BackupReconciliationSummary(
                        detail.Reconciliation.AttemptCount,
                        detail.Reconciliation.NextAtUtc,
                        taskId == TaskId ? ReconciliationReasonCode : detail.Reconciliation.ReasonCode,
                        CanRequest),
                };
            }

            var name = taskId == TaskId ? "测试备份" : "另一备份";
            var database = taskId == TaskId ? "合成库" : "合成库乙";
            var summary = new BackupTaskSummary(taskId, name, database, "Pending", "Backup",
                DateTimeOffset.UtcNow, null, null, false, null);
            return new BackupTaskDetail(summary, [], [], null,
                new BackupReconciliationSummary(0, null, null, false), []);
        }
    }
}
