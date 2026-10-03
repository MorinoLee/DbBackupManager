using Bunit;
using DbBackupManager.Web.Components.Layout;

namespace DbBackupManager.Web.Tests.Components;

public sealed class AuthLayoutTests : MudBlazorComponentTest
{
    [Fact]
    public void AuthLayoutShowsBrandAndContentWithoutAdminShell()
    {
        var component = Render<AuthLayout>(parameters => parameters.Add(
            layout => layout.Body,
            "<p data-testid=\"auth-body\">认证内容</p>"));

        Assert.Contains("DbBackupManager", component.Markup, StringComparison.Ordinal);
        Assert.Equal("认证内容", component.Find("[data-testid='auth-body']").TextContent);
        Assert.Empty(component.FindAll("[data-testid='drawer-toggle']"));
        Assert.Empty(component.FindAll("[data-testid='theme-toggle']"));
        Assert.Empty(component.FindAll("nav.app-nav"));
        Assert.Empty(component.FindAll("[data-testid='nav-link-login']"));
    }
}
