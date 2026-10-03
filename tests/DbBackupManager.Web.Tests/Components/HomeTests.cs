using System.Security.Claims;
using Bunit;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Web.Authentication;
using DbBackupManager.Web.BackupTasks;
using DbBackupManager.Web.Components.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Web.Tests.Components;

public sealed class HomeTests : MudBlazorComponentTest
{
    private readonly FakeMonitoring _service = new();
    private readonly TaskRefreshNotifier _notifier = new();
    public HomeTests()
    {
        Services.AddSingleton<IBackupMonitoringService>(_service);
        Services.AddSingleton(_notifier);
    }
    [Fact]
    public void HomeRequiresAdministratorPolicy()
    {
        var authorization = Assert.Single(typeof(Home).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal(AdminAuthenticationDefaults.AuthorizationPolicy, authorization.Policy);
    }
    [Fact]
    public void AnonymousHomeDoesNotQueryBusinessServices()
    {
        var page = Render<Home>();
        Assert.Equal(0, _service.Calls);
        Assert.NotNull(page.Find("[data-testid='home-login']"));
    }
    [Fact]
    public void OverviewShowsGlobalCountsAndIndependentHeartbeat()
    {
        Authenticate();
        var page = Render<Home>();
        page.WaitForAssertion(() => Assert.Contains("121", page.Find("[data-testid='dashboard-metrics']").TextContent, StringComparison.Ordinal));
        Assert.Contains("25", page.Find("[data-testid='dashboard-metrics']").TextContent, StringComparison.Ordinal);
        Assert.Contains("在线", page.Find("[data-testid='worker-presence']").TextContent, StringComparison.Ordinal);
        Assert.Contains("合成库", page.Find("[data-testid='recent-tasks']").TextContent, StringComparison.Ordinal);
        Assert.Contains("从未登记验证副本：3", page.Find("[data-testid='database-protection']").TextContent, StringComparison.Ordinal);
        Assert.Equal("/notifications", page.Find("[data-testid='dashboard-shortcuts'] a[href='/notifications']").GetAttribute("href"));
    }
    [Fact]
    public void EmptyInventoryGuidesToAddServerAndDoesNotInventOnlineState()
    {
        Authenticate();
        _service.Overview = _service.Overview with { ManagedDatabases = 0, Worker = new("Unknown", null) };
        var page = Render<Home>();
        page.WaitForAssertion(() => Assert.Equal("/servers", page.Find("[data-testid='dashboard-add-server']").GetAttribute("href")));
        Assert.Contains("尚无心跳", page.Find("[data-testid='worker-presence']").TextContent, StringComparison.Ordinal);
    }
    [Fact]
    public void RevokedIdentityClearsAllDashboardFacts()
    {
        Authenticate();
        var page = Render<Home>();
        page.WaitForAssertion(() => Assert.Contains("合成库", page.Markup, StringComparison.Ordinal));
        _service.Code = BackupManagementCode.AuthenticationRequired;
        _notifier.Publish();
        page.WaitForAssertion(() => Assert.Contains("重新登录", page.Markup, StringComparison.Ordinal));
        Assert.Empty(page.FindAll("[data-testid='dashboard-metrics']"));
        Assert.DoesNotContain("合成库", page.Markup, StringComparison.Ordinal);
    }
    private void Authenticate()
    {
        SignIn("管理员");
        Authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("N")),
            new Claim(AdminAuthenticationDefaults.SecurityStampClaimType, "synthetic"));
    }
}
