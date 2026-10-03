using System.Security.Claims;
using Bunit;
using DbBackupManager.Application.FileCredentials;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.Servers;
using DbBackupManager.Application.SqlCredentials;
using DbBackupManager.Web.Authentication;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using ServersPage = DbBackupManager.Web.Components.Pages.Servers;

namespace DbBackupManager.Web.Tests.Components;

public sealed class ServersTests : MudBlazorComponentTest
{
    private readonly FakeManagement _service = new();

    public ServersTests()
    {
        Services.AddSingleton<IServerManagementService>(_service);
        var credentials = new FakeCredentials();
        Services.AddSingleton<ISqlCredentialService>(credentials);
        Services.AddSingleton<IFileCredentialService>(credentials);
    }

    [Fact]
    public void AnonymousPageDoesNotReadConfiguration()
    {
        var page = Render<ServersPage>();
        Assert.Equal(0, _service.ListCalls);
        Assert.Contains("请先登录", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpiredIdentityClearsInventoryAndEditingActions()
    {
        Authenticate();
        _service.Code = ManagementCode.AuthenticationRequired;
        var page = Render<ServersPage>();
        page.WaitForAssertion(() => Assert.Contains("重新登录", page.Markup, StringComparison.Ordinal));
        Assert.Empty(page.FindAll("[data-testid='new-server']"));
        Assert.DoesNotContain("synthetic-host", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void InstancesAreGroupedUnderOwningServerCard()
    {
        Authenticate();
        var page = Render<ServersPage>();

        var card = page.Find("[data-testid='server-card']");
        Assert.Contains("synthetic-server", card.TextContent, StringComparison.Ordinal);
        Assert.Contains("synthetic-instance", card.TextContent, StringComparison.Ordinal);

        var viewDatabases = card.QuerySelector("[data-testid='view-databases']");
        Assert.NotNull(viewDatabases);
        Assert.Equal($"/databases?instance={FakeManagement.InstanceId}", viewDatabases.GetAttribute("href"));
    }

    [Fact]
    public void DiscoverySucceedsAndGuidesToDatabaseList()
    {
        Authenticate();
        var page = Render<ServersPage>();
        page.Find("[data-testid='discover-instance']").Click();
        page.WaitForAssertion(() => Assert.Contains("数据库发现完成", page.Markup, StringComparison.Ordinal));
        Assert.Contains("数据库清单", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbingOneInstanceShowsLoadingOnlyOnItsOwningServerCard()
    {
        Authenticate();
        _service.Inventory = FakeManagement.TwoServerInventory();
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.ProbeStarted = probeStarted;
        _service.ReleaseProbe = releaseProbe;
        var page = Render<ServersPage>();

        var cards = page.FindAll("[data-testid='server-card']");
        Assert.Equal(2, cards.Count);

        // 不 await：保持探测在飞行中，才能观察卡片级加载状态。
        var probing = cards[0].QuerySelector("[data-testid='probe-instance']")!.ClickAsync(new MouseEventArgs());
        await probeStarted.Task;

        page.WaitForAssertion(() =>
        {
            var tables = page.FindComponents<MudTable<InstanceItem>>();
            Assert.Equal(2, tables.Count);
            Assert.True(tables[0].Instance.Loading);
            Assert.False(tables[1].Instance.Loading);
        });

        releaseProbe.SetResult();
        await probing;

        page.WaitForAssertion(() => Assert.All(
            page.FindComponents<MudTable<InstanceItem>>(),
            table => Assert.False(table.Instance.Loading)));
    }

    [Fact]
    public void ServerEditDialogPassesVersionAndConflictKeepsInputForCorrection()
    {
        Authenticate();
        var provider = Render<MudDialogProvider>();
        var page = Render<ServersPage>();

        page.Find("[data-testid='edit-server']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#server-name")));

        provider.Find("#server-name").Change("新名称");
        _service.Code = ManagementCode.Conflict;
        provider.Find("form").Submit();

        provider.WaitForAssertion(() => Assert.Equal("新名称", _service.SavedServer?.Name));
        Assert.Equal(FakeManagement.Version, _service.SavedVersion);
        // 冲突时弹窗保持打开并保留输入，错误显示在弹窗内。
        provider.WaitForAssertion(() => Assert.Contains("配置已更新", provider.Markup, StringComparison.Ordinal));
        Assert.Equal("新名称", provider.Find("#server-name").GetAttribute("value"));
    }

    [Fact]
    public void ServerEditDialogClosesAfterSuccessfulSave()
    {
        Authenticate();
        var provider = Render<MudDialogProvider>();
        var page = Render<ServersPage>();

        page.Find("[data-testid='edit-server']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#server-name")));
        provider.Find("#server-name").Change("新名称");
        provider.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("服务器已保存", page.Markup, StringComparison.Ordinal));
        provider.WaitForAssertion(() => Assert.Empty(provider.FindAll("#server-name")));
    }

    [Fact]
    public void InstanceTrustExceptionShowsPersistentWarningAndReasonField()
    {
        Authenticate();
        var provider = Render<MudDialogProvider>();
        var page = Render<ServersPage>();

        page.Find("[data-testid='edit-instance']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("[data-testid='trust-certificate']")));
        provider.Find("[data-testid='trust-certificate']").Change(true);

        provider.WaitForAssertion(() => Assert.Contains("证书信任例外原因", provider.Markup, StringComparison.Ordinal));
        Assert.Contains("跳过此实例的服务器证书验证", provider.Markup, StringComparison.Ordinal);
    }

    private void Authenticate()
    {
        SignIn("管理员");
        Authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("N")),
            new Claim(AdminAuthenticationDefaults.SecurityStampClaimType, "synthetic-stamp"));
    }

    private sealed class FakeManagement : IServerManagementService
    {
        public const string Version = "AAAAAAAAAAA=";
        public static readonly Guid ServerId = Guid.NewGuid(), InstanceId = Guid.NewGuid(), CredentialId = Guid.NewGuid();
        private static readonly ServerItem Server = new(ServerId, new("synthetic-server", @"D:\Synthetic", 1, "synthetic-host", null, "synthetic-share", CredentialId, null, null, true), Version);
        private static readonly InstanceItem Instance = new(InstanceId, new(ServerId, "synthetic-instance", "synthetic-host", CredentialId, false, null, 15, true), Version, "Unknown", null, null, null, null);
        public ManagementCode Code { get; set; } = ManagementCode.Succeeded;
        public int ListCalls { get; private set; }
        public ServerInput? SavedServer { get; private set; }
        public string? SavedVersion { get; private set; }
        public ServerInventory Inventory { get; set; } = new([Server], [Instance], []);
        public TaskCompletionSource? ProbeStarted { get; set; }
        public TaskCompletionSource? ReleaseProbe { get; set; }

        /// <summary>两台服务器各带一个实例，用于验证卡片级状态不会互相串扰。</summary>
        public static ServerInventory TwoServerInventory()
        {
            var secondServerId = Guid.NewGuid();
            var secondServer = new ServerItem(
                secondServerId,
                new("synthetic-server-2", @"D:\Synthetic", 1, "synthetic-host-2", null, "synthetic-share", CredentialId, null, null, true),
                Version);
            var secondInstance = new InstanceItem(
                Guid.NewGuid(),
                new(secondServerId, "synthetic-instance-2", "synthetic-host-2", CredentialId, false, null, 15, true),
                Version,
                "Unknown",
                null,
                null,
                null,
                null);
            return new([Server, secondServer], [Instance, secondInstance], []);
        }

        public Task<ManagementResult<ServerInventory>> ListAsync(AdminSession actor, CancellationToken cancellationToken = default)
        { ListCalls++; return Task.FromResult(new ManagementResult<ServerInventory>(Code, Inventory)); }

        public Task<ManagementResult<ServerItem>> SaveServerAsync(AdminSession actor, Guid? id, string? version, ServerInput input, CancellationToken cancellationToken = default)
        { SavedServer = input; SavedVersion = version; return Task.FromResult(new ManagementResult<ServerItem>(Code, Server)); }

        public Task<ManagementResult<InstanceItem>> SaveInstanceAsync(AdminSession actor, Guid? id, string? version, InstanceInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<ManagementResult<InstanceItem>> ProbeAsync(AdminSession actor, Guid id, string version, bool discover, CancellationToken cancellationToken = default)
        {
            ProbeStarted?.TrySetResult();
            if (ReleaseProbe is { } gate)
            {
                await gate.Task;
            }

            return new ManagementResult<InstanceItem>(Code, Inventory.Instances.Single(item => item.Id == id));
        }

        public Task<ManagementResult<DatabaseItem>> SetManagedAsync(AdminSession actor, Guid id, string version, bool managed, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeCredentials : ISqlCredentialService, IFileCredentialService
    {
        Task<SqlCredentialResult<IReadOnlyList<SqlCredentialItem>>> ISqlCredentialService.ListAsync(AdminSession actor, CancellationToken cancellationToken) =>
            Task.FromResult(new SqlCredentialResult<IReadOnlyList<SqlCredentialItem>>(SqlCredentialResultCode.Succeeded, [new(FakeManagement.CredentialId, "SQL", "user", true, FakeManagement.Version)]));
        Task<FileCredentialResult<IReadOnlyList<FileCredentialItem>>> IFileCredentialService.ListAsync(AdminSession actor, CancellationToken cancellationToken) =>
            Task.FromResult(new FileCredentialResult<IReadOnlyList<FileCredentialItem>>(FileCredentialResultCode.Succeeded, [new(FakeManagement.CredentialId, "SMB", "user", FileCredentialKind.SmbPassword, true, FakeManagement.Version)]));
        public Task<SqlCredentialResult<SqlCredentialItem>> CreateAsync(AdminSession actor, string name, string username, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FileCredentialResult<FileCredentialItem>> CreateAsync(AdminSession actor, string name, string username, FileCredentialKind kind, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        Task<SqlCredentialResult<SqlCredentialItem>> ISqlCredentialService.RotatePasswordAsync(AdminSession actor, Guid id, string version, string password, CancellationToken cancellationToken) => throw new NotSupportedException();
        Task<FileCredentialResult<FileCredentialItem>> IFileCredentialService.RotatePasswordAsync(AdminSession actor, Guid id, string version, string password, CancellationToken cancellationToken) => throw new NotSupportedException();
        Task<SqlCredentialResult<SqlCredentialItem>> ISqlCredentialService.SetEnabledAsync(AdminSession actor, Guid id, string version, bool isEnabled, CancellationToken cancellationToken) => throw new NotSupportedException();
        Task<FileCredentialResult<FileCredentialItem>> IFileCredentialService.SetEnabledAsync(AdminSession actor, Guid id, string version, bool isEnabled, CancellationToken cancellationToken) => throw new NotSupportedException();
        Task<FileCredentialResult<FileCredentialItem>> IFileCredentialService.CreatePrivateKeyAsync(AdminSession actor, string name, string username, string privateKey, string? passphrase, CancellationToken cancellationToken) => throw new NotSupportedException();
        Task<FileCredentialResult<FileCredentialItem>> IFileCredentialService.RotatePrivateKeyAsync(AdminSession actor, Guid id, string version, string privateKey, string? passphrase, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
