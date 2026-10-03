using Bunit;
using DbBackupManager.Web.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Web.Tests.Components;

public sealed class NavMenuTests : MudBlazorComponentTest
{
    [Fact]
    public void AnonymousNavigationLinksToHomeAndLogin()
    {
        var component = Render<NavMenu>();

        var home = component.Find("[data-testid='nav-link-home']");
        var login = component.Find("[data-testid='nav-link-login-menu']");

        Assert.Equal("/", home.GetAttribute("href"));
        Assert.Contains("工作台", home.TextContent, StringComparison.Ordinal);
        Assert.Equal("/login", login.GetAttribute("href"));
        Assert.Empty(component.FindAll("[data-testid='nav-link-sql-credentials']"));
        Assert.Empty(component.FindAll("[data-testid='nav-group-database']"));
    }

    [Fact]
    public void AuthenticatedNavigationGroupsBusinessEntries()
    {
        SignIn("管理员");
        var component = Render<NavMenu>();

        Assert.NotNull(component.Find("[data-testid='nav-group-database']"));
        Assert.NotNull(component.Find("[data-testid='nav-group-backup']"));
        Assert.NotNull(component.Find("[data-testid='nav-group-system']"));
        Assert.Equal("/servers", component.Find("[data-testid='nav-link-servers']").GetAttribute("href"));
        Assert.Equal("/sql-credentials", component.Find("[data-testid='nav-link-sql-credentials']").GetAttribute("href"));
        Assert.Equal("/file-credentials", component.Find("[data-testid='nav-link-file-credentials']").GetAttribute("href"));
        Assert.Equal("/notifications", component.Find("[data-testid='nav-link-notifications']").GetAttribute("href"));
        Assert.Equal("/storage-targets", component.Find("[data-testid='nav-link-storage-targets']").GetAttribute("href"));
        Assert.Equal("/backup-policies", component.Find("[data-testid='nav-link-backup-policies']").GetAttribute("href"));
        Assert.Equal("/scheduled-backup-policies", component.Find("[data-testid='nav-link-scheduled-backup-policies']").GetAttribute("href"));
        Assert.Throws<Bunit.ElementNotFoundException>(
            () => component.Find("[data-testid='nav-link-login-menu']"));
        Assert.Empty(component.FindAll("[data-testid='nav-pending-note']"));
    }

    [Theory]
    [InlineData("/servers", true, false, false)]
    [InlineData("/databases", true, false, false)]
    [InlineData("/backup-tasks", false, true, false)]
    [InlineData("/scheduled-backup-policies", false, true, false)]
    [InlineData("/backup-tasks/9f2b1c6d-0000-4000-8000-000000000000", false, true, false)]
    [InlineData("/storage-targets", false, true, false)]
    [InlineData("/backup-files", false, true, false)]
    [InlineData("/backups", false, true, false)]
    [InlineData("/sql-credentials", false, false, true)]
    [InlineData("/notifications", false, false, true)]
    [InlineData("/change-password", false, false, true)]
    [InlineData("/", false, false, false)]
    public void NavigationGroupsResolveOwningGroupForRoute(
        string route,
        bool databaseExpanded,
        bool backupExpanded,
        bool systemExpanded)
    {
        Assert.Equal(databaseExpanded, NavigationGroups.IsDatabase(route));
        Assert.Equal(backupExpanded, NavigationGroups.IsBackup(route));
        Assert.Equal(systemExpanded, NavigationGroups.IsSystem(route));
    }

    [Fact]
    public void CurrentRouteRendersOwningGroupExpanded()
    {
        SignIn("管理员");
        Services.GetRequiredService<NavigationManager>().NavigateTo("/servers");

        var component = Render<NavMenu>();

        // MudNavGroup 的展开状态通过标题按钮的 aria-expanded 表达，不使用内部样式类断言。
        var group = component.Find("[data-testid='nav-group-database']");
        var toggle = group.QuerySelector("button[aria-expanded]");

        Assert.NotNull(toggle);
        Assert.Equal("true", toggle.GetAttribute("aria-expanded"));
    }

    [Fact]
    public void StaticSsrNavigationExpandsAllGroupsSoLinksStayReachable()
    {
        SignIn("管理员");
        Services.GetRequiredService<NavigationManager>().NavigateTo("/change-password");

        var component = Render<NavMenu>(parameters => parameters
            .AddCascadingValue(StaticSsrHttpContext.Create()));

        // 静态页面无法响应点击展开，全部分组必须已展开，保证链接可达。
        foreach (var testId in new[] { "nav-group-database", "nav-group-backup", "nav-group-system" })
        {
            var toggle = component.Find($"[data-testid='{testId}'] button[aria-expanded]");
            Assert.Equal("true", toggle.GetAttribute("aria-expanded"));
        }

        Assert.NotNull(component.Find("[data-testid='nav-link-servers']"));
        Assert.NotNull(component.Find("[data-testid='nav-link-backup-tasks']"));
        Assert.NotNull(component.Find("[data-testid='nav-link-sql-credentials']"));
    }

    [Fact]
    public void NavigationIsExposedAsALandmarkWithChineseLabel()
    {
        var component = Render<NavMenu>();

        var navigation = component.Find("nav.app-nav");

        Assert.Equal("主导航", navigation.GetAttribute("aria-label"));
    }
}
