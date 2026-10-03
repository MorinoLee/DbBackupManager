using System.Security.Claims;
using Bunit;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.Servers;
using DbBackupManager.Application.StorageTargets;
using DbBackupManager.Web.Authentication;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Page = DbBackupManager.Web.Components.Pages.BackupPolicies;

namespace DbBackupManager.Web.Tests.Components;

public sealed class BackupPoliciesTests : MudBlazorComponentTest
{
    private readonly FakeManagement _service = new();
    private readonly FakeTargets _targets = new();

    public BackupPoliciesTests()
    {
        Services.AddSingleton<IBackupManagementService>(_service);
        Services.AddSingleton<IStorageTargetManagementService>(_targets);
    }

    [Fact]
    public void AnonymousCannotReadOrStartBackup()
    {
        var page = Render<Page>();
        Assert.Equal(0, _service.ListCalls);
        Assert.Empty(page.FindAll("[data-testid='run-backup']"));
    }

    [Fact]
    public void AmbiguousSubmissionRetainsRequestIdForRetry()
    {
        Authenticate();
        var page = Render<Page>();
        _service.StartCode = BackupManagementCode.Unavailable;
        page.Find("[data-testid='run-backup']").Click();
        page.WaitForAssertion(() => Assert.Single(_service.Requests));
        _service.StartCode = BackupManagementCode.Succeeded;
        page.Find("[data-testid='run-backup']").Click();
        page.WaitForAssertion(() => Assert.Equal(2, _service.Requests.Count));
        Assert.Equal(_service.Requests[0], _service.Requests[1]);
        Assert.Contains("任务已提交", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulStartLinksToTaskDetail()
    {
        Authenticate();
        var page = Render<Page>();
        page.Find("[data-testid='run-backup']").Click();

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='started-task-link']")));
        Assert.Equal($"/backup-tasks/{_service.StartedTaskId}",
            page.Find("[data-testid='started-task-link']").GetAttribute("href"));
    }

    [Fact]
    public void EditDialogLocksDatabaseAndConflictKeepsInput()
    {
        Authenticate();
        var provider = Render<MudDialogProvider>();
        var page = Render<Page>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid='edit-policy']")));

        page.Find("[data-testid='edit-policy']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#backup-name")));

        provider.Find("#backup-name").Change("改名配置");
        _service.SaveCode = BackupManagementCode.Conflict;
        provider.Find("form").Submit();

        provider.WaitForAssertion(() => Assert.Equal("改名配置", _service.SavedPolicy?.Name));
        Assert.Equal(FakeManagement.Version, _service.SavedVersion);
        provider.WaitForAssertion(() => Assert.Contains("已改变", provider.Markup, StringComparison.Ordinal));
        Assert.Equal("改名配置", provider.Find("#backup-name").GetAttribute("value"));
    }

    [Fact]
    public void RemotePolicySaveKeepsTargetAndRetention()
    {
        Authenticate();
        var provider = Render<MudDialogProvider>();
        var page = Render<Page>();
        page.WaitForAssertion(() => Assert.Contains("本地加远程", page.Markup, StringComparison.Ordinal));
        page.FindAll("[data-testid='edit-policy']")[1].Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#backup-name")));
        provider.Find("#backup-name").Change("远程配置");
        provider.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Equal("远程配置", _service.SavedPolicy?.Name));
        Assert.Equal(2, _service.SavedPolicy?.StorageMode);
        Assert.Equal(FakeTargets.TargetId, _service.SavedPolicy?.StorageTargetId);
        Assert.Equal(14, _service.SavedPolicy?.RemoteRetentionDays);
        Assert.Equal(90, _service.SavedPolicy?.TransferTimeoutMinutes);
    }

    [Fact]
    public void RemoteModeWithoutTargetsShowsGuidance()
    {
        Authenticate();
        _targets.Items = [];
        var provider = Render<MudDialogProvider>();
        var page = Render<Page>();
        page.Find("[data-testid='new-backup-policy']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#backup-name")));
        var editor = provider.FindComponent<DbBackupManager.Web.Components.Pages.BackupPolicyEditDialog>();
        editor.Instance.Model.StorageMode = 2;
        editor.Render();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("[data-testid='no-storage-targets']")));
        Assert.Contains("/storage-targets", provider.Markup, StringComparison.Ordinal);
    }

    private void Authenticate()
    {
        SignIn("管理员");
        Authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("N")),
            new Claim(AdminAuthenticationDefaults.SecurityStampClaimType, "synthetic"));
    }

    private sealed class FakeManagement : IBackupManagementService
    {
        public const string Version = "AAAAAAAAAAA=";
        public readonly Guid StartedTaskId = Guid.NewGuid();
        private readonly ManualBackupPolicy _policy = new(Guid.NewGuid(), new(Guid.NewGuid(), "测试备份", 7, 120, 60, false, true), Version);
        private readonly ManualBackupPolicy _remote = new(Guid.NewGuid(),
            new(Guid.NewGuid(), "远程备份", 7, 120, 60, false, true, 2, FakeTargets.TargetId, 14, 90), Version);
        public int ListCalls;
        public List<Guid> Requests { get; } = [];
        public BackupManagementCode StartCode = BackupManagementCode.Succeeded;
        public BackupManagementCode SaveCode = BackupManagementCode.Succeeded;
        public ManualBackupInput? SavedPolicy { get; private set; }
        public string? SavedVersion { get; private set; }

        public Task<BackupManagementResult<BackupDashboard>> ListAsync(AdminSession actor, int page = 0, CancellationToken token = default)
        { ListCalls++; return Task.FromResult(new BackupManagementResult<BackupDashboard>(BackupManagementCode.Succeeded, new([], [_policy, _remote], [], false))); }
        public Task<BackupManagementResult<ManualBackupPolicy>> SavePolicyAsync(AdminSession actor, Guid? id, string? version, ManualBackupInput input, CancellationToken token = default)
        { SavedPolicy = input; SavedVersion = version; return Task.FromResult(new BackupManagementResult<ManualBackupPolicy>(SaveCode, _policy)); }
        public Task<BackupManagementResult<Guid>> StartAsync(AdminSession actor, Guid policyId, Guid requestId, CancellationToken token = default)
        { Requests.Add(requestId); return Task.FromResult(new BackupManagementResult<Guid>(StartCode, StartedTaskId)); }
        public Task<BackupManagementResult<Guid>> CancelAsync(AdminSession actor, Guid taskId, Guid requestId, CancellationToken token = default) => throw new NotSupportedException();
        public Task<BackupManagementResult<Guid>> RetryAsync(AdminSession actor, Guid taskId, Guid requestId, CancellationToken token = default) => throw new NotSupportedException();
        public Task<BackupManagementResult<Guid>> RequestReconciliationAsync(AdminSession actor, Guid taskId, Guid requestId, CancellationToken token = default) => throw new NotSupportedException();
        public Task<BackupManagementResult<Guid>> ConfirmFailedAsync(AdminSession actor, Guid taskId, Guid requestId, bool evidenceReviewed, CancellationToken token = default) => throw new NotSupportedException();
        public Task<BackupManagementResult<BackupTaskDetail>> DetailAsync(AdminSession actor, Guid taskId, CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class FakeTargets : IStorageTargetManagementService
    {
        public static readonly Guid TargetId = Guid.NewGuid();
        public IReadOnlyList<StorageTargetItem> Items { get; set; } =
        [
            new(TargetId, new("演示目标", 1, "synthetic-host", null, "synthetic-share", Guid.NewGuid(), null, true),
                "AAAAAAAAAAA="),
        ];

        public Task<ManagementResult<IReadOnlyList<StorageTargetItem>>> ListAsync(
            AdminSession actor, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ManagementResult<IReadOnlyList<StorageTargetItem>>(ManagementCode.Succeeded, Items));

        public Task<ManagementResult<StorageTargetItem>> SaveAsync(
            AdminSession actor, Guid? id, string? version, StorageTargetInput input,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ManagementResult<StorageTargetItem>> SetEnabledAsync(
            AdminSession actor, Guid id, string version, bool isEnabled,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ManagementResult<StorageTargetItem>> TestConnectionAsync(
            AdminSession actor, Guid id, string version,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
