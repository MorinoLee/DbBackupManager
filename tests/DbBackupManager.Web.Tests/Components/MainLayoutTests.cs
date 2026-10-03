using Bunit;
using DbBackupManager.Web.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Web.Tests.Components;

public sealed class MainLayoutTests : MudBlazorComponentTest
{
    private const string DrawerToggleSelector = "[data-testid='drawer-toggle']";
    private const string ThemeToggleSelector = "[data-testid='theme-toggle']";

    [Fact]
    public void ShellRendersBrandNavigationAndPageContent()
    {
        var component = RenderLayout();

        Assert.Contains("DbBackupManager", component.Markup, StringComparison.Ordinal);
        Assert.Equal("页面内容", component.Find("[data-testid='layout-body']").TextContent);
        Assert.Equal("/", component.Find("[data-testid='nav-link-home']").GetAttribute("href"));
    }

    [Fact]
    public void DrawerToggleFlipsStateAndKeepsAccessibleLabelInSync()
    {
        var component = RenderLayout();

        var beforeClick = ReadDrawerToggleState(component);
        component.Find(DrawerToggleSelector).Click();
        var afterClick = ReadDrawerToggleState(component);

        Assert.NotEqual(beforeClick.IsExpanded, afterClick.IsExpanded);
        AssertLabelMatchesState(beforeClick);
        AssertLabelMatchesState(afterClick);
    }

    [Fact]
    public void ThemeToggleSwitchesBetweenLightAndDarkMode()
    {
        var component = RenderLayout();

        Assert.Equal("切换到深色主题", component.Find(ThemeToggleSelector).GetAttribute("aria-label"));

        component.Find(ThemeToggleSelector).Click();

        Assert.Equal("切换到浅色主题", component.Find(ThemeToggleSelector).GetAttribute("aria-label"));

        component.Find(ThemeToggleSelector).Click();

        Assert.Equal("切换到深色主题", component.Find(ThemeToggleSelector).GetAttribute("aria-label"));
    }

    [Fact]
    public async Task DisposedLayoutNoLongerReactsToNavigation()
    {
        RenderLayout();
        var navigationManager = Services.GetRequiredService<NavigationManager>();

        await DisposeComponentsAsync();

        var exception = Record.Exception(() => navigationManager.NavigateTo("/health/live"));

        Assert.Null(exception);
    }

    [Fact]
    public void DisposedHttpContextStillShowsInteractiveShellControls()
    {
        IRenderedComponent<MainLayout>? component = null;
        var exception = Record.Exception(() =>
        {
            component = Render<MainLayout>(parameters => parameters
                .AddCascadingValue(DisposedHttpContext.Create())
                .Add(layout => layout.Body, "<p data-testid=\"layout-body\">页面内容</p>"));
        });

        Assert.Null(exception);
        Assert.NotNull(component);
        Assert.NotNull(component.Find(DrawerToggleSelector));
        Assert.NotNull(component.Find(ThemeToggleSelector));
    }

    [Fact]
    public void StaticSsrShellShowsPlainAccountLinksAndPostLogout()
    {
        SignIn("管理员");
        var component = Render<MainLayout>(parameters => parameters
            .AddCascadingValue(StaticSsrHttpContext.Create())
            .Add(layout => layout.Body, "<p data-testid=\"layout-body\">页面内容</p>"));

        // Static SSR 页面（如修改密码）没有交互事件处理器：下拉菜单、抽屉与主题按钮不可出现，
        // 账号操作必须是纯链接与普通 POST 表单。
        Assert.Empty(component.FindAll("[data-testid='user-menu-button']"));
        Assert.Empty(component.FindAll(DrawerToggleSelector));
        Assert.Empty(component.FindAll(ThemeToggleSelector));
        Assert.Equal("管理员", component.Find("[data-testid='current-username']").TextContent);
        Assert.Equal("/change-password",
            component.Find("[data-testid='nav-link-change-password']").GetAttribute("href"));

        var logout = component.Find("[data-testid='logout-form']");
        Assert.Equal("post", logout.GetAttribute("method"));
        Assert.Equal("/account/logout", logout.GetAttribute("action"));
        Assert.NotNull(component.Find("input[name='__RequestVerificationToken']"));
    }

    [Fact]
    public void AnonymousShellShowsLoginAction()
    {
        var component = RenderLayout();

        Assert.Equal("/login", component.Find("[data-testid='nav-link-login']").GetAttribute("href"));
        Assert.Throws<Bunit.ElementNotFoundException>(
            () => component.Find("[data-testid='logout-form']"));
    }

    [Fact]
    public void AuthenticatedShellShowsUsernameChangePasswordAndPostLogout()
    {
        SignIn("管理员");
        var component = RenderLayout();

        Assert.Equal("管理员", component.Find("[data-testid='current-username']").TextContent);

        // 用户菜单内容在打开后才渲染；先展开菜单再断言菜单项与登出表单。
        component.Find("[data-testid='user-menu-button']").Click();

        component.WaitForAssertion(() =>
            Assert.Equal("/change-password", component.Find("[data-testid='nav-link-change-password']").GetAttribute("href")));

        var logout = component.Find("[data-testid='logout-form']");
        Assert.Equal("post", logout.GetAttribute("method"));
        Assert.Equal("/account/logout", logout.GetAttribute("action"));
        Assert.NotNull(component.Find("input[name='__RequestVerificationToken']"));
        Assert.Throws<Bunit.ElementNotFoundException>(
            () => component.Find("[data-testid='nav-link-login']"));
    }

    private static DrawerToggleState ReadDrawerToggleState(IRenderedComponent<MainLayout> component)
    {
        var toggle = component.Find(DrawerToggleSelector);

        return new DrawerToggleState(
            string.Equals(toggle.GetAttribute("aria-expanded"), "true", StringComparison.Ordinal),
            toggle.GetAttribute("aria-label"));
    }

    private static void AssertLabelMatchesState(DrawerToggleState state)
    {
        Assert.Equal(state.IsExpanded ? "收起导航菜单" : "展开导航菜单", state.Label);
    }

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        return Render<MainLayout>(parameters => parameters.Add(
            layout => layout.Body,
            "<p data-testid=\"layout-body\">页面内容</p>"));
    }

    private sealed record DrawerToggleState(bool IsExpanded, string? Label);
}
