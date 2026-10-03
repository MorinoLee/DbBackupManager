using System.Security.Claims;
using Bunit;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.SqlCredentials;
using DbBackupManager.Web.Authentication;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using SqlCredentialsPage = DbBackupManager.Web.Components.Pages.SqlCredentials;

namespace DbBackupManager.Web.Tests.Components;

public sealed class SqlCredentialsTests : MudBlazorComponentTest
{
    private readonly FakeCredentials _credentials = new();

    public SqlCredentialsTests()
    {
        Services.AddSingleton<ISqlCredentialService>(_credentials);
    }

    [Fact]
    public void AnonymousPageDoesNotReadProtectedCredentialList()
    {
        var page = Render<SqlCredentialsPage>();
        Assert.Equal(0, _credentials.ListCalls);
        Assert.Contains("请先登录", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyListHasCreateActionAndCreateClearsPassword()
    {
        Authenticate();
        var provider = Render<MudDialogProvider>();
        var page = Render<SqlCredentialsPage>();
        page.WaitForAssertion(() => Assert.Contains("暂无 SQL 凭据", page.Markup, StringComparison.Ordinal));
        page.Find("[data-testid='new-credential']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#credential-name")));
        provider.Find("#credential-name").Change("演示凭据");
        provider.Find("#credential-username").Change("synthetic-login");
        provider.Find("#credential-password").Change("synthetic-password");
        Assert.Equal("password", provider.Find("#credential-password").GetAttribute("type"));
        provider.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Equal(1, _credentials.CreateCalls));
        page.WaitForAssertion(() => Assert.Contains("凭据已保存", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("synthetic-password", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-password", provider.Markup, StringComparison.Ordinal);
        // 更新密码弹窗不预填旧密码，也不回显名称以外内容。
        page.Find("[data-testid='rotate-password']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#credential-password")));
        Assert.True(string.IsNullOrEmpty(provider.Find("#credential-password").GetAttribute("value")));
    }

    [Fact]
    public void FailedCreateClearsPasswordAndKeepsDialogForRetry()
    {
        Authenticate();
        _credentials.WriteCode = SqlCredentialResultCode.ProtectionUnavailable;
        var provider = Render<MudDialogProvider>();
        var page = Render<SqlCredentialsPage>();
        page.Find("[data-testid='new-credential']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#credential-name")));
        provider.Find("#credential-name").Change("演示凭据");
        provider.Find("#credential-username").Change("synthetic-login");
        provider.Find("#credential-password").Change("synthetic-password");
        provider.Find("form").Submit();
        // 失败时错误显示在弹窗内，弹窗保持打开且名称输入保留、密码清空。
        provider.WaitForAssertion(() => Assert.Contains("凭据加密暂不可用", provider.Markup, StringComparison.Ordinal));
        Assert.True(string.IsNullOrEmpty(provider.Find("#credential-password").GetAttribute("value")));
        Assert.Equal("演示凭据", provider.Find("#credential-name").GetAttribute("value"));
        Assert.False(provider.Find("button[type='submit']").HasAttribute("disabled"));
    }

    [Fact]
    public void ExpiredSessionClearsDataAndOffersFullLogin()
    {
        Authenticate();
        _credentials.ListCode = SqlCredentialResultCode.AuthenticationRequired;
        var page = Render<SqlCredentialsPage>();
        page.WaitForAssertion(() => Assert.Contains("重新登录", page.Markup, StringComparison.Ordinal));
        Assert.Empty(page.FindAll("[data-testid='new-credential']"));
    }

    [Fact]
    public void RevokedIdentityDuringCreateClearsListAndClosesEditor()
    {
        Authenticate();
        _credentials.Items = [new SqlCredentialItem(Guid.NewGuid(), "凭据甲", "sql-user", true, "AAAAAAAAAAA=")];
        var provider = Render<MudDialogProvider>();
        var page = Render<SqlCredentialsPage>();
        page.WaitForAssertion(() => Assert.Contains("凭据甲", page.Markup, StringComparison.Ordinal));

        // 服务端已撤销身份：新建保存返回 AuthenticationRequired。
        _credentials.WriteCode = SqlCredentialResultCode.AuthenticationRequired;
        page.Find("[data-testid='new-credential']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#credential-name")));
        provider.Find("#credential-name").Change("演示凭据");
        provider.Find("#credential-username").Change("synthetic-login");
        provider.Find("#credential-password").Change("synthetic-password");
        provider.Find("form").Submit();

        // 身份失效：清空受保护列表、关闭编辑流程并给出重新登录入口，不再展示已失去访问资格的配置。
        page.WaitForAssertion(() => Assert.Contains("重新登录", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("凭据甲", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-testid='new-credential']"));
        provider.WaitForAssertion(() => Assert.Empty(provider.FindAll("#credential-name")));
    }

    [Fact]
    public void RevokedIdentityDuringRotateClearsListAndClosesEditor()
    {
        Authenticate();
        _credentials.Items = [new SqlCredentialItem(Guid.NewGuid(), "凭据甲", "sql-user", true, "AAAAAAAAAAA=")];
        var provider = Render<MudDialogProvider>();
        var page = Render<SqlCredentialsPage>();
        page.WaitForAssertion(() => Assert.Contains("凭据甲", page.Markup, StringComparison.Ordinal));

        _credentials.WriteCode = SqlCredentialResultCode.AuthenticationRequired;
        page.Find("[data-testid='rotate-password']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#credential-password")));
        provider.Find("#credential-password").Change("synthetic-password");
        provider.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, _credentials.RotateCalls));
        page.WaitForAssertion(() => Assert.Contains("重新登录", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("凭据甲", page.Markup, StringComparison.Ordinal);
        provider.WaitForAssertion(() => Assert.Empty(provider.FindAll("#credential-password")));
    }

    private void Authenticate()
    {
        SignIn("管理员");
        Authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("N")),
            new Claim(AdminAuthenticationDefaults.SecurityStampClaimType, "synthetic-stamp"));
    }

    private sealed class FakeCredentials : ISqlCredentialService
    {
        public int ListCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public int RotateCalls { get; private set; }
        public SqlCredentialResultCode ListCode { get; set; } = SqlCredentialResultCode.Succeeded;
        public SqlCredentialResultCode WriteCode { get; set; } = SqlCredentialResultCode.Succeeded;
        public IReadOnlyList<SqlCredentialItem> Items { get; set; } = [];

        public Task<SqlCredentialResult<IReadOnlyList<SqlCredentialItem>>> ListAsync(AdminSession actor, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(new SqlCredentialResult<IReadOnlyList<SqlCredentialItem>>(ListCode, Items));
        }

        public Task<SqlCredentialResult<SqlCredentialItem>> CreateAsync(AdminSession actor, string name, string username, string password, CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            return Task.FromResult(new SqlCredentialResult<SqlCredentialItem>(WriteCode,
                WriteCode == SqlCredentialResultCode.Succeeded ? new(Guid.NewGuid(), name, username, true, "AAAAAAAAAAA=") : null));
        }

        public Task<SqlCredentialResult<SqlCredentialItem>> RotatePasswordAsync(AdminSession actor, Guid id, string version, string password, CancellationToken cancellationToken = default)
        {
            RotateCalls++;
            return Task.FromResult(new SqlCredentialResult<SqlCredentialItem>(WriteCode,
                WriteCode == SqlCredentialResultCode.Succeeded ? new(id, "凭据甲", "sql-user", true, version) : null));
        }

        public Task<SqlCredentialResult<SqlCredentialItem>> SetEnabledAsync(AdminSession actor, Guid id, string version, bool isEnabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
