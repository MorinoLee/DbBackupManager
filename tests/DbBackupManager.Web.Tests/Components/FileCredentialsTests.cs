using System.Security.Claims;
using Bunit;
using DbBackupManager.Application.FileCredentials;
using DbBackupManager.Application.Identity;
using DbBackupManager.Web.Authentication;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using FileCredentialsPage = DbBackupManager.Web.Components.Pages.FileCredentials;

namespace DbBackupManager.Web.Tests.Components;

public sealed class FileCredentialsTests : MudBlazorComponentTest
{
    private readonly FakeCredentials _credentials = new();

    public FileCredentialsTests()
    {
        Services.AddSingleton<IFileCredentialService>(_credentials);
    }

    [Fact]
    public void AnonymousPageDoesNotReadProtectedCredentialList()
    {
        var page = Render<FileCredentialsPage>();
        Assert.Equal(0, _credentials.ListCalls);
        Assert.Contains("请先登录", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyListHasCreateActionAndCreateClearsPassword()
    {
        Authenticate();
        var provider = Render<MudDialogProvider>();
        var page = Render<FileCredentialsPage>();
        page.WaitForAssertion(() => Assert.Contains("暂无暂存访问凭据", page.Markup, StringComparison.Ordinal));
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
        // 更新密码弹窗不预填旧密码。
        page.Find("[data-testid='rotate-password']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#credential-password")));
        Assert.True(string.IsNullOrEmpty(provider.Find("#credential-password").GetAttribute("value")));
    }

    [Fact]
    public void FailedCreateClearsPasswordAndKeepsDialogForRetry()
    {
        Authenticate();
        _credentials.WriteCode = FileCredentialResultCode.ProtectionUnavailable;
        var provider = Render<MudDialogProvider>();
        var page = Render<FileCredentialsPage>();
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
        _credentials.ListCode = FileCredentialResultCode.AuthenticationRequired;
        var page = Render<FileCredentialsPage>();
        page.WaitForAssertion(() => Assert.Contains("重新登录", page.Markup, StringComparison.Ordinal));
        Assert.Empty(page.FindAll("[data-testid='new-credential']"));
    }

    [Fact]
    public void RevokedIdentityDuringCreateClearsListAndClosesEditor()
    {
        Authenticate();
        _credentials.Items = [new FileCredentialItem(Guid.NewGuid(), "凭据甲", "share-user", FileCredentialKind.SmbPassword, true, "AAAAAAAAAAA=")];
        var provider = Render<MudDialogProvider>();
        var page = Render<FileCredentialsPage>();
        page.WaitForAssertion(() => Assert.Contains("凭据甲", page.Markup, StringComparison.Ordinal));

        // 服务端已撤销身份：新建保存返回 AuthenticationRequired。
        _credentials.WriteCode = FileCredentialResultCode.AuthenticationRequired;
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
        _credentials.Items = [new FileCredentialItem(Guid.NewGuid(), "凭据甲", "share-user", FileCredentialKind.SmbPassword, true, "AAAAAAAAAAA=")];
        var provider = Render<MudDialogProvider>();
        var page = Render<FileCredentialsPage>();
        page.WaitForAssertion(() => Assert.Contains("凭据甲", page.Markup, StringComparison.Ordinal));

        _credentials.WriteCode = FileCredentialResultCode.AuthenticationRequired;
        page.Find("[data-testid='rotate-password']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#credential-password")));
        provider.Find("#credential-password").Change("synthetic-password");
        provider.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Equal(1, _credentials.RotateCalls));
        page.WaitForAssertion(() => Assert.Contains("重新登录", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("凭据甲", page.Markup, StringComparison.Ordinal);
        provider.WaitForAssertion(() => Assert.Empty(provider.FindAll("#credential-password")));
    }

    [Fact]
    public void PrivateKeyCreateClearsSecretAndDoesNotEchoKey()
    {
        Authenticate();
        var provider = Render<MudDialogProvider>();
        var page = Render<FileCredentialsPage>();
        page.Find("[data-testid='new-credential']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#credential-name")));
        var editor = provider.FindComponent<DbBackupManager.Web.Components.Pages.FileCredentialEditDialog>();
        editor.Instance.Model.Kind = FileCredentialKind.SftpPrivateKey;
        editor.Render();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#credential-private-key")));
        provider.Find("#credential-name").Change("演示私钥");
        provider.Find("#credential-username").Change("sftp-user");
        provider.Find("#credential-private-key").Change("synthetic-private-key");
        provider.Find("#credential-passphrase").Change("synthetic-passphrase");
        provider.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Equal(1, _credentials.CreatePrivateKeyCalls));
        Assert.Equal("synthetic-private-key", _credentials.LastPrivateKey);
        Assert.Equal("synthetic-passphrase", _credentials.LastPassphrase);
        page.WaitForAssertion(() => Assert.Contains("凭据已保存", page.Markup, StringComparison.Ordinal));
        Assert.Contains("SFTP 私钥", page.Markup, StringComparison.Ordinal);
        Assert.Contains("已设置口令", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-private-key", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-passphrase", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-private-key", provider.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void RotatePrivateKeyClearsSecretAndKeepsDialogOnFailure()
    {
        Authenticate();
        _credentials.Items =
        [
            new FileCredentialItem(Guid.NewGuid(), "私钥甲", "sftp-user", FileCredentialKind.SftpPrivateKey, true,
                "AAAAAAAAAAA=", true),
        ];
        _credentials.WriteCode = FileCredentialResultCode.ProtectionUnavailable;
        var provider = Render<MudDialogProvider>();
        var page = Render<FileCredentialsPage>();
        page.WaitForAssertion(() => Assert.Contains("私钥甲", page.Markup, StringComparison.Ordinal));
        page.Find("[data-testid='rotate-private-key']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#credential-private-key")));
        provider.Find("#credential-private-key").Change("synthetic-private-key");
        provider.Find("form").Submit();
        provider.WaitForAssertion(() => Assert.Contains("凭据加密暂不可用", provider.Markup, StringComparison.Ordinal));
        Assert.Equal(1, _credentials.RotatePrivateKeyCalls);
        Assert.True(string.IsNullOrEmpty(provider.Find("#credential-private-key").GetAttribute("value")));
        Assert.DoesNotContain("synthetic-private-key", provider.Markup, StringComparison.Ordinal);
    }

    private void Authenticate()
    {
        SignIn("管理员");
        Authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("N")),
            new Claim(AdminAuthenticationDefaults.SecurityStampClaimType, "synthetic-stamp"));
    }

    private sealed class FakeCredentials : IFileCredentialService
    {
        public int ListCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public int RotateCalls { get; private set; }
        public int CreatePrivateKeyCalls { get; private set; }
        public int RotatePrivateKeyCalls { get; private set; }
        public FileCredentialResultCode ListCode { get; set; } = FileCredentialResultCode.Succeeded;
        public FileCredentialResultCode WriteCode { get; set; } = FileCredentialResultCode.Succeeded;
        public IReadOnlyList<FileCredentialItem> Items { get; set; } = [];
        public string? LastPrivateKey { get; private set; }
        public string? LastPassphrase { get; private set; }

        public Task<FileCredentialResult<IReadOnlyList<FileCredentialItem>>> ListAsync(AdminSession actor, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(new FileCredentialResult<IReadOnlyList<FileCredentialItem>>(ListCode, Items));
        }

        public Task<FileCredentialResult<FileCredentialItem>> CreateAsync(AdminSession actor, string name, string username, FileCredentialKind kind, string password, CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            return Task.FromResult(new FileCredentialResult<FileCredentialItem>(WriteCode,
                WriteCode == FileCredentialResultCode.Succeeded ? new(Guid.NewGuid(), name, username, kind, true, "AAAAAAAAAAA=") : null));
        }

        public Task<FileCredentialResult<FileCredentialItem>> RotatePasswordAsync(AdminSession actor, Guid id, string version, string password, CancellationToken cancellationToken = default)
        {
            RotateCalls++;
            return Task.FromResult(new FileCredentialResult<FileCredentialItem>(WriteCode,
                WriteCode == FileCredentialResultCode.Succeeded ? new(id, "凭据甲", "share-user", FileCredentialKind.SmbPassword, true, version) : null));
        }

        public Task<FileCredentialResult<FileCredentialItem>> SetEnabledAsync(AdminSession actor, Guid id, string version, bool isEnabled, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FileCredentialResult<FileCredentialItem>> CreatePrivateKeyAsync(AdminSession actor, string name, string username, string privateKey, string? passphrase, CancellationToken cancellationToken = default)
        {
            CreatePrivateKeyCalls++;
            LastPrivateKey = privateKey;
            LastPassphrase = passphrase;
            return Task.FromResult(new FileCredentialResult<FileCredentialItem>(WriteCode,
                WriteCode == FileCredentialResultCode.Succeeded
                    ? new(Guid.NewGuid(), name, username, FileCredentialKind.SftpPrivateKey, true, "AAAAAAAAAAA=",
                        !string.IsNullOrEmpty(passphrase))
                    : null));
        }

        public Task<FileCredentialResult<FileCredentialItem>> RotatePrivateKeyAsync(AdminSession actor, Guid id, string version, string privateKey, string? passphrase, CancellationToken cancellationToken = default)
        {
            RotatePrivateKeyCalls++;
            LastPrivateKey = privateKey;
            LastPassphrase = passphrase;
            return Task.FromResult(new FileCredentialResult<FileCredentialItem>(WriteCode,
                WriteCode == FileCredentialResultCode.Succeeded
                    ? new(id, "私钥甲", "sftp-user", FileCredentialKind.SftpPrivateKey, true, version,
                        !string.IsNullOrEmpty(passphrase))
                    : null));
        }
    }
}
