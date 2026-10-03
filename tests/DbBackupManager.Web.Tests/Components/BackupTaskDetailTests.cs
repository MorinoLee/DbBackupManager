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
        Assert.Contains("已验证本地副本", page.Markup, StringComparison.Ordinal);
        Assert.Contains("第一次调用", page.Find("[data-testid='task-attempts']").TextContent, StringComparison.Ordinal);
        Assert.Contains("等待 Worker 领取", page.Find("[data-testid='task-history']").TextContent, StringComparison.Ordinal);
        var files = page.Find("[data-testid='task-files']");
        Assert.Contains("本地", files.TextContent, StringComparison.Ordinal);
        Assert.Contains("远程", files.TextContent, StringComparison.Ordinal);
        Assert.Contains("删除失败", files.TextContent, StringComparison.Ordinal);
        Assert.Contains("主机密钥指纹不匹配", files.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("HostKeyMismatch", files.TextContent, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-testid='delete-file']"));
        var panel = page.Find("[data-testid='reconciliation-panel']");
        Assert.Contains("已完成核对次数：1", panel.TextContent, StringComparison.Ordinal);
        Assert.Contains("下次核对时间：", panel.TextContent, StringComparison.Ordinal);
        Assert.Contains("核对结果待确认", panel.TextContent, StringComparison.Ordinal);
        Assert.Contains("重新核对不会重新备份", panel.TextContent, StringComparison.Ordinal);
        Assert.NotNull(page.Find("[data-testid='reconcile-task']"));
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
    public void ReconciliationButtonHiddenWhenCannotRequest()
    {
        Authenticate();
        _service.CanRequest = false;
        var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='reconciliation-panel']")));
        Assert.Contains("重新核对不会重新备份", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-testid='reconcile-task']"));
        Assert.Empty(_service.ReconcileCalls);
    }

    [Fact]
    public void ReconciliationSuccessClearsRequestIdAndShowsFixedMessage()
    {
        Authenticate();
        var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='reconcile-task']")));

        page.Find("[data-testid='reconcile-task']").Click();
        page.WaitForAssertion(() => Assert.Contains("已提交重新核对，等待 Worker 处理", page.Markup, StringComparison.Ordinal));
        Assert.Single(_service.ReconcileCalls);
        var first = _service.ReconcileCalls[0].RequestId;

        page.Find("[data-testid='reconcile-task']").Click();
        page.WaitForAssertion(() => Assert.Equal(2, _service.ReconcileCalls.Count));
        Assert.NotEqual(first, _service.ReconcileCalls[1].RequestId);
        Assert.Empty(_service.CancelCalls);
    }

    [Fact]
    public void ReconciliationFailureReplaysTheSameRequestId()
    {
        Authenticate();
        _service.ReconcileCode = BackupManagementCode.Conflict;
        var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='reconcile-task']")));

        page.Find("[data-testid='reconcile-task']").Click();
        page.WaitForAssertion(() => Assert.Single(_service.ReconcileCalls));
        page.Find("[data-testid='reconcile-task']").Click();
        page.WaitForAssertion(() => Assert.Equal(2, _service.ReconcileCalls.Count));

        Assert.Equal(_service.ReconcileCalls[0].RequestId, _service.ReconcileCalls[1].RequestId);
        Assert.Contains("状态不允许此操作", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconciliationInFlightDisablesButtonAndDoesNotSendAgain()
    {
        Authenticate();
        var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='reconcile-task']")));

        var gate = new TaskCompletionSource<BackupManagementResult<Guid>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _service.ReconcileGate = gate;
        page.Find("[data-testid='reconcile-task']").Click();
        page.WaitForAssertion(() => Assert.True(page.Find("[data-testid='reconcile-task']").HasAttribute("disabled")));
        Assert.Single(_service.ReconcileCalls);

        _service.ReconcileGate = null;
        gate.SetResult(new BackupManagementResult<Guid>(BackupManagementCode.Succeeded, FakeManagement.TaskId));
        page.WaitForAssertion(() => Assert.Contains("已提交重新核对，等待 Worker 处理", page.Markup, StringComparison.Ordinal));
        Assert.Single(_service.ReconcileCalls);
    }

    [Fact]
    public void ReconciliationAuthenticationFailureClearsSensitiveContent()
    {
        Authenticate();
        _service.ReconcileCode = BackupManagementCode.AuthenticationRequired;
        var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='reconcile-task']")));

        page.Find("[data-testid='reconcile-task']").Click();
        page.WaitForAssertion(() => Assert.Contains("重新登录", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("外部执行结果尚未确定", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-testid='task-summary']"));
        Assert.Empty(page.FindAll("[data-testid='reconciliation-panel']"));
    }

    [Fact]
    public void SwitchingRouteClearsReconciliationRequestIdAndMessage()
    {
        Authenticate();
        _service.ReconcileCode = BackupManagementCode.Conflict;
        var host = Render<DetailHost>(parameters => parameters.Add(x => x.InitialId, FakeManagement.TaskId));
        host.WaitForAssertion(() => Assert.NotNull(host.Find("[data-testid='reconcile-task']")));

        host.Find("[data-testid='reconcile-task']").Click();
        host.WaitForAssertion(() => Assert.Single(_service.ReconcileCalls));
        Assert.Contains("状态不允许此操作", host.Markup, StringComparison.Ordinal);

        _service.ReconcileCode = BackupManagementCode.Succeeded;
        host.InvokeAsync(() => host.Instance.Show(FakeManagement.OtherTaskId));
        host.WaitForAssertion(() => Assert.Contains("另一备份", host.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("测试备份", host.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("状态不允许此操作", host.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("已提交重新核对，等待 Worker 处理", host.Markup, StringComparison.Ordinal);

        host.Find("[data-testid='reconcile-task']").Click();
        host.WaitForAssertion(() => Assert.Equal(2, _service.ReconcileCalls.Count));
        Assert.Equal(FakeManagement.TaskId, _service.ReconcileCalls[0].TaskId);
        Assert.Equal(FakeManagement.OtherTaskId, _service.ReconcileCalls[1].TaskId);
        Assert.NotEqual(_service.ReconcileCalls[0].RequestId, _service.ReconcileCalls[1].RequestId);
        Assert.Empty(_service.CancelCalls);
    }

    [Fact]
    public void InFlightReconciliationMustNotWriteAfterRouteSwitch()
    {
        Authenticate();
        var host = Render<DetailHost>(parameters => parameters.Add(x => x.InitialId, FakeManagement.TaskId));
        host.WaitForAssertion(() => Assert.NotNull(host.Find("[data-testid='reconcile-task']")));

        var gate = new TaskCompletionSource<BackupManagementResult<Guid>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _service.ReconcileGate = gate;
        host.Find("[data-testid='reconcile-task']").Click();
        host.WaitForAssertion(() => Assert.Single(_service.ReconcileCalls));

        host.InvokeAsync(() => host.Instance.Show(FakeManagement.OtherTaskId));
        _service.ReconcileGate = null;
        gate.SetResult(new BackupManagementResult<Guid>(BackupManagementCode.Succeeded, FakeManagement.TaskId));

        host.WaitForAssertion(() => Assert.Contains("另一备份", host.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("已提交重新核对，等待 Worker 处理", host.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("测试备份", host.Markup, StringComparison.Ordinal);
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

    [Fact]
    public void ConfirmFailedRequiresEvidenceAndReusesRequestAfterUncertainResponseWithoutRetrying()
    {
        Authenticate();
        var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));
        page.WaitForAssertion(() => Assert.True(page.Find("[data-testid='confirm-failed-task']").HasAttribute("disabled")));
        Assert.Empty(_service.ConfirmCalls);
        page.Find("[data-testid='confirm-failed-evidence']").Change(true);
        _service.ConfirmCode = BackupManagementCode.Unavailable;
        page.Find("[data-testid='confirm-failed-task']").Click();
        page.WaitForAssertion(() => Assert.Single(_service.ConfirmCalls));
        _service.ConfirmCode = BackupManagementCode.Succeeded;
        page.Find("[data-testid='confirm-failed-task']").Click();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='retry-task']")));
        Assert.Equal(2, _service.ConfirmCalls.Count);
        Assert.Equal(_service.ConfirmCalls[0], _service.ConfirmCalls[1]);
        Assert.True(_service.ConfirmCalls[0].EvidenceReviewed);
        Assert.Contains("已确认本阶段失败，未发起重试", page.Markup, StringComparison.Ordinal);
        Assert.Empty(_service.RetryCalls);
        page.Find("[data-testid='retry-task']").Click();
        page.WaitForAssertion(() => Assert.Single(_service.RetryCalls));
    }

    [Fact]
    public void ConfirmFailedIsHiddenDuringActiveLeaseOrOutsideNeedsAttention()
    {
        Authenticate();
        _service.CanRequest = false;
        var page = Render<Page>(parameters => parameters.Add(x => x.Id, FakeManagement.TaskId));
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='task-summary']")));
        Assert.Empty(page.FindAll("[data-testid='confirm-failed-task']"));
        _service.ServePendingCancelable = true;
        _notifier.Publish();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='cancel-task']")));
        Assert.Empty(page.FindAll("[data-testid='confirm-failed-evidence']"));
        Assert.Empty(_service.ConfirmCalls);
    }

    [Fact]
    public void RouteSwitchClearsConfirmationAndDiscardsOldConfirmationResponse()
    {
        Authenticate();
        var host = Render<DetailHost>(parameters => parameters.Add(x => x.InitialId, FakeManagement.TaskId));
        host.WaitForAssertion(() => Assert.NotNull(host.Find("[data-testid='confirm-failed-evidence']")));
        host.Find("[data-testid='confirm-failed-evidence']").Change(true);
        var gate = new TaskCompletionSource<BackupManagementResult<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.ConfirmGate = gate;
        host.Find("[data-testid='confirm-failed-task']").Click();
        host.WaitForAssertion(() => Assert.Single(_service.ConfirmCalls));
        host.InvokeAsync(() => host.Instance.Show(FakeManagement.OtherTaskId));
        _service.ConfirmGate = null;
        gate.SetResult(new(BackupManagementCode.Succeeded, FakeManagement.TaskId));
        host.WaitForAssertion(() => Assert.Contains("另一备份", host.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("已确认本阶段失败，未发起重试", host.Markup, StringComparison.Ordinal);
        Assert.True(host.Find("[data-testid='confirm-failed-task']").HasAttribute("disabled"));
        host.Find("[data-testid='confirm-failed-evidence']").Change(true);
        host.Find("[data-testid='confirm-failed-task']").Click();
        host.WaitForAssertion(() => Assert.Equal(2, _service.ConfirmCalls.Count));
        Assert.Equal(FakeManagement.OtherTaskId, _service.ConfirmCalls[1].TaskId);
        Assert.NotEqual(_service.ConfirmCalls[0].RequestId, _service.ConfirmCalls[1].RequestId);
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
                    @"\\synthetic\share\backup.bak"),
                new BackupFileSummary(TaskId, Guid.NewGuid(), "合成库", "测试实例", "测试备份", "", 2048, DateTimeOffset.UtcNow,
                    14, Guid.NewGuid(), "Remote", "Sftp", "演示目标", "DeleteFailed", DateTimeOffset.UtcNow,
                    "HostKeyMismatch", "/synthetic/backup.bak"),
            ]);

        public int DetailCalls;
        public Guid LastRequestedId;
        public BackupManagementCode Code = BackupManagementCode.Succeeded;
        public BackupManagementCode CancelCode = BackupManagementCode.Succeeded;
        public BackupManagementCode ReconcileCode = BackupManagementCode.Succeeded;
        public BackupManagementCode ConfirmCode = BackupManagementCode.Succeeded;
        public bool ServePendingCancelable;
        public bool CanRequest = true;
        public string? ReconciliationReasonCode = "synthetic_unknown";
        public TaskCompletionSource<BackupManagementResult<BackupTaskDetail>>? Gate;
        public TaskCompletionSource<BackupManagementResult<Guid>>? CancelGate;
        public TaskCompletionSource<BackupManagementResult<Guid>>? ReconcileGate;
        public TaskCompletionSource<BackupManagementResult<Guid>>? ConfirmGate;
        public List<(Guid TaskId, Guid RequestId, bool EvidenceReviewed)> ConfirmCalls { get; } = [];
        public List<Guid> RetryCalls { get; } = [];
        private readonly HashSet<Guid> _failedTasks = [];
        public List<(Guid TaskId, Guid RequestId)> CancelCalls { get; } = [];
        public List<(Guid TaskId, Guid RequestId)> ReconcileCalls { get; } = [];

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
        public Task<BackupManagementResult<ManualBackupPolicy>> SavePolicyAsync(AdminSession actor, Guid? id, string? version, ManualBackupInput input, CancellationToken token = default) => throw new NotSupportedException();
        public Task<BackupManagementResult<Guid>> StartAsync(AdminSession actor, Guid policyId, Guid requestId, CancellationToken token = default) => throw new NotSupportedException();
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
        public Task<BackupManagementResult<Guid>> RetryAsync(AdminSession actor, Guid taskId, Guid requestId, CancellationToken token = default)
        { RetryCalls.Add(taskId); return Task.FromResult(new BackupManagementResult<Guid>(BackupManagementCode.Succeeded, taskId)); }
        public Task<BackupManagementResult<Guid>> ConfirmFailedAsync(AdminSession actor, Guid taskId, Guid requestId, bool evidenceReviewed, CancellationToken token = default)
        {
            ConfirmCalls.Add((taskId, requestId, evidenceReviewed));
            if (ConfirmGate is not null) return ConfirmGate.Task;
            if (ConfirmCode == BackupManagementCode.Succeeded) _failedTasks.Add(taskId);
            return Task.FromResult(new BackupManagementResult<Guid>(ConfirmCode, taskId));
        }
        public Task<BackupManagementResult<Guid>> RequestReconciliationAsync(AdminSession actor, Guid taskId, Guid requestId, CancellationToken token = default)
        {
            ReconcileCalls.Add((taskId, requestId));
            if (ReconcileGate is not null)
            {
                return ReconcileGate.Task;
            }

            return Task.FromResult(new BackupManagementResult<Guid>(ReconcileCode,
                ReconcileCode == BackupManagementCode.Succeeded ? taskId : Guid.Empty));
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
                    Task = _failedTasks.Contains(taskId) ? detail.Task with { Status = "Failed" } : detail.Task,
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
