using Bunit;
using DbBackupManager.Application.Identity;
using DbBackupManager.Web.Components.Pages;
using DbBackupManager.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Web.Tests.Components;

public sealed class AuthPageTests : MudBlazorComponentTest
{
    [Fact]
    public void LoginFormPostsToAccountLoginWithAntiforgeryAndSafeReturnUrl()
    {
        Services.AddSingleton<IAdminIdentityService>(new StubIdentityService(setupRequired: true));
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/login?error=invalid_credentials&returnUrl=/change-password");

        var component = Render<Login>();
        var form = component.Find("[data-testid='login-form']");
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal("/account/login", form.GetAttribute("action"));
        Assert.NotNull(component.Find("input[name='__RequestVerificationToken']"));
        Assert.Equal("/change-password", component.Find("input[name='returnUrl']").GetAttribute("value"));
        var password = component.Find("[data-testid='login-password']");
        Assert.Equal("password", password.GetAttribute("type"));
        Assert.Contains("auth-form-field__input", password.ClassList);
        Assert.Contains("用户名或密码不正确", component.Markup, StringComparison.Ordinal);
        Assert.Contains("前往首次设置", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginDoesNotThrowWhenCascadedHttpContextIsDisposed()
    {
        Services.AddSingleton<IAdminIdentityService>(new StubIdentityService(setupRequired: false));
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/login?returnUrl=/change-password");

        IRenderedComponent<Login>? component = null;
        var exception = Record.Exception(() =>
        {
            component = Render<Login>(parameters => parameters
                .AddCascadingValue(DisposedHttpContext.Create()));
        });

        Assert.Null(exception);
        Assert.NotNull(component);
        Assert.Equal("/change-password", component.Find("input[name='returnUrl']").GetAttribute("value"));
        Assert.Throws<Bunit.ElementNotFoundException>(() => component.Find("[data-testid='auth-error']"));
    }

    [Fact]
    public void LoginIgnoresExternalReturnUrlAndMapsPasswordChangedStatus()
    {
        Services.AddSingleton<IAdminIdentityService>(new StubIdentityService(setupRequired: false));
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/login?status=password_changed&returnUrl=https://example.invalid/redirect");

        var component = Render<Login>();

        Assert.Contains("密码已修改，请使用新密码重新登录", component.Markup, StringComparison.Ordinal);
        Assert.Throws<Bunit.ElementNotFoundException>(() => component.Find("input[name='returnUrl']"));
        Assert.Throws<Bunit.ElementNotFoundException>(() => component.Find("[data-testid='setup-required-hint']"));
    }

    [Fact]
    public void SetupShowsFormWhenRequired()
    {
        Services.AddSingleton<IAdminIdentityService>(new StubIdentityService(setupRequired: true));
        var required = Render<Setup>();
        var form = required.Find("[data-testid='setup-form']");
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal("/account/setup", form.GetAttribute("action"));
        Assert.Equal("password", required.Find("[data-testid='setup-password']").GetAttribute("type"));
    }

    [Fact]
    public void SetupHidesFormAfterAdministratorExists()
    {
        Services.AddSingleton<IAdminIdentityService>(new StubIdentityService(setupRequired: false));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/setup?error=setup_unavailable");
        var completed = Render<Setup>();
        Assert.Throws<Bunit.ElementNotFoundException>(() => completed.Find("[data-testid='setup-form']"));
        Assert.Contains("首次设置已完成，不能重复创建管理员", completed.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangePasswordFormPostsToAccountAction()
    {
        SignIn("管理员");
        Services.GetRequiredService<NavigationManager>().NavigateTo("/change-password?error=validation_failed");
        var component = Render<ChangePassword>();

        var form = component.Find("[data-testid='change-password-form']");
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal("/account/change-password", form.GetAttribute("action"));
        Assert.Equal("password", component.Find("[data-testid='current-password']").GetAttribute("type"));
        Assert.Equal("password", component.Find("[data-testid='new-password']").GetAttribute("type"));
        Assert.Contains("输入内容不符合要求", component.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessDeniedPageDoesNotShowInternalDetails()
    {
        var component = Render<AccessDenied>();

        Assert.Contains("没有权限查看该页面", component.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", component.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Stack", component.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("validation_failed", "输入内容不符合要求。")]
    [InlineData("invalid_credentials", "用户名或密码不正确。")]
    [InlineData("setup_unavailable", "首次设置已完成，不能重复创建管理员。")]
    [InlineData("authentication_required", "请先登录。")]
    [InlineData("concurrency_conflict", "提交冲突，请刷新后重试。")]
    [InlineData("rate_limit_exceeded", "请求过于频繁，请稍后重试。")]
    public void ErrorCodesMapToSafeChineseMessages(string code, string expected)
    {
        Assert.Equal(expected, AuthPageMessages.ForError(code));
    }

    [Theory]
    [InlineData("/change-password", "/change-password")]
    [InlineData("/", "/")]
    [InlineData("https://example.invalid", null)]
    [InlineData("//evil.example", null)]
    [InlineData("/login?x=1", "/login?x=1")]
    public void ReturnUrlOnlyKeepsLocalPaths(string? input, string? expected)
    {
        Assert.Equal(expected, AuthPageMessages.SanitizeReturnUrl(input));
    }

    private sealed class StubIdentityService(bool setupRequired) : IAdminIdentityService
    {
        public Task<bool> IsSetupRequiredAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(setupRequired);

        public Task<AdminIdentityResult> SetupAsync(
            string? username,
            string? password,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AdminIdentityResult(AdminIdentityResultCode.SetupUnavailable));

        public Task<AdminIdentityResult> AuthenticateAsync(
            string? username,
            string? password,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AdminIdentityResult(AdminIdentityResultCode.InvalidCredentials));

        public Task<AdminIdentityResult> ValidateSessionAsync(
            Guid adminUserId,
            string? securityStamp,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AdminIdentityResult(AdminIdentityResultCode.AuthenticationRequired));

        public Task<AdminIdentityResult> ChangePasswordAsync(
            Guid adminUserId,
            string? securityStamp,
            string? currentPassword,
            string? newPassword,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AdminIdentityResult(AdminIdentityResultCode.AuthenticationRequired));

        public Task RecordCsrfFailureAsync(
            Guid? actorAdminUserId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
