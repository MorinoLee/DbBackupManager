using System.Security.Claims;
using Bunit;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.Notifications;
using DbBackupManager.Application.Servers;
using DbBackupManager.Web.Authentication;
using Microsoft.Extensions.DependencyInjection;
using NotificationsPage = DbBackupManager.Web.Components.Pages.Notifications;

namespace DbBackupManager.Web.Tests.Components;

public sealed class NotificationsTests : MudBlazorComponentTest
{
    private static readonly Guid SettingsId = Guid.Parse("8f3c2a10-5d6e-4b91-9c7a-11d000000001");
    private readonly FakeSmtpSettings _smtp = new();

    public NotificationsTests()
    {
        Services.AddSingleton<ISmtpSettingsService>(_smtp);
    }

    [Fact]
    public void AnonymousPageDoesNotReadSmtpSettings()
    {
        var page = Render<NotificationsPage>();
        Assert.Equal(0, _smtp.GetCalls);
        Assert.Contains("请先登录", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("#smtp-host"));
    }

    [Fact]
    public void UnconfiguredSettingsShowEmptyStateAndFormDefaults()
    {
        Authenticate();
        var page = Render<NotificationsPage>();
        page.WaitForAssertion(() => Assert.Contains("尚未保存 SMTP 设置", page.Markup, StringComparison.Ordinal));
        Assert.Contains("暂无收件人", page.Markup, StringComparison.Ordinal);
        Assert.Contains("未设置凭据", page.Markup, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(page.Find("#smtp-host").GetAttribute("value")));
        Assert.True(page.Find("[data-testid='send-test']").HasAttribute("disabled"));
    }

    [Fact]
    public void SaveKeepsInputOnConflictAndDoesNotEchoSecrets()
    {
        Authenticate();
        _smtp.WriteCode = ManagementCode.Conflict;
        var page = Render<NotificationsPage>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("#smtp-host")));
        page.Find("#smtp-host").Change("smtp.keep.test");
        page.Find("#smtp-from").Change("ops@example.test");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("设置已被其他操作更新", page.Markup, StringComparison.Ordinal));
        Assert.Equal(1, _smtp.SaveCalls);
        Assert.Equal("smtp.keep.test", page.Find("#smtp-host").GetAttribute("value"));
        Assert.Equal("ops@example.test", page.Find("#smtp-from").GetAttribute("value"));
        Assert.DoesNotContain("protectedSecret", page.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SavePersistsSettingsThenPasswordIsWriteOnly()
    {
        Authenticate();
        var page = Render<NotificationsPage>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("#smtp-host")));
        page.Find("#smtp-host").Change("smtp.example.test");
        page.Find("#smtp-from").Change("ops@example.test");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("SMTP 设置已保存", page.Markup, StringComparison.Ordinal));
        Assert.Equal(1, _smtp.SaveCalls);

        page.Find("#smtp-username").Change("smtp-user");
        page.Find("#smtp-password").Change("synthetic-smtp-password");
        Assert.Equal("password", page.Find("#smtp-password").GetAttribute("type"));
        page.Find("[data-testid='save-smtp-password']").Click();
        page.WaitForAssertion(() => Assert.Contains("SMTP 密码已保存", page.Markup, StringComparison.Ordinal));
        Assert.Equal(1, _smtp.RotateCalls);
        Assert.Equal("synthetic-smtp-password", _smtp.LastPassword);
        Assert.Contains("已设置凭据", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-smtp-password", page.Markup, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(page.Find("#smtp-password").GetAttribute("value")));
        Assert.True(string.IsNullOrEmpty(page.Find("#smtp-username").GetAttribute("value")));
    }

    [Fact]
    public void FailedPasswordRotateClearsSecretAndKeepsSettings()
    {
        Authenticate();
        _smtp.Item = Configured(hasCredential: false);
        _smtp.WriteCode = ManagementCode.ValidationFailed;
        var page = Render<NotificationsPage>();
        page.WaitForAssertion(() => Assert.Contains("smtp.example.test", page.Markup, StringComparison.Ordinal));
        page.Find("#smtp-username").Change("smtp-user");
        page.Find("#smtp-password").Change("synthetic-smtp-password");
        page.Find("[data-testid='save-smtp-password']").Click();
        page.WaitForAssertion(() => Assert.Contains("请检查主机、端口、安全模式、发件人、收件人、凭据和版本信息", page.Markup, StringComparison.Ordinal));
        Assert.Equal("smtp.example.test", page.Find("#smtp-host").GetAttribute("value"));
        Assert.True(string.IsNullOrEmpty(page.Find("#smtp-password").GetAttribute("value")));
        Assert.DoesNotContain("synthetic-smtp-password", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAndRemoveRecipientThenQueueIdempotentTest()
    {
        Authenticate();
        _smtp.Item = Configured(enabled: true, recipients: []);
        var page = Render<NotificationsPage>();
        page.WaitForAssertion(() => Assert.Contains("暂无收件人", page.Markup, StringComparison.Ordinal));
        page.Find("#smtp-recipient").Change("ops@example.test");
        page.Find("[data-testid='add-recipient']").Click();
        page.WaitForAssertion(() => Assert.Contains("收件人已添加", page.Markup, StringComparison.Ordinal));
        Assert.Contains("ops@example.test", page.Find("[data-testid='recipient-list']").TextContent, StringComparison.Ordinal);

        page.Find("[data-testid='send-test']").Click();
        page.WaitForAssertion(() => Assert.Contains("测试通知已按当前请求登记", page.Markup, StringComparison.Ordinal));
        Assert.Equal(1, _smtp.QueueTestCalls);
        Assert.Equal(1, _smtp.UniqueTestIds);
        var requestId = page.Find("[data-testid='last-request-id']").TextContent;
        Assert.Contains(_smtp.LastRequestId.ToString(), requestId, StringComparison.Ordinal);

        page.Find("[data-testid='replay-test']").Click();
        page.WaitForAssertion(() => Assert.Equal(2, _smtp.QueueTestCalls));
        Assert.Equal(1, _smtp.UniqueTestIds);
        Assert.Contains(_smtp.LastRequestId.ToString(), page.Find("[data-testid='last-request-id']").TextContent, StringComparison.Ordinal);

        page.Find("[data-testid='remove-recipient']").Click();
        page.WaitForAssertion(() => Assert.Contains("收件人已移除", page.Markup, StringComparison.Ordinal));
        Assert.Contains("暂无收件人", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpiredSessionClearsProtectedFieldsAndOffersLogin()
    {
        Authenticate();
        _smtp.GetCode = ManagementCode.AuthenticationRequired;
        var page = Render<NotificationsPage>();
        page.WaitForAssertion(() => Assert.Contains("重新登录", page.Markup, StringComparison.Ordinal));
        Assert.Contains("登录状态已失效，请重新登录。", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("#smtp-host"));
        Assert.Empty(page.FindAll("#smtp-password"));
    }

    [Fact]
    public void RevokedIdentityDuringSaveClearsSettingsAndStopsEditing()
    {
        Authenticate();
        _smtp.Item = Configured();
        var page = Render<NotificationsPage>();
        page.WaitForAssertion(() => Assert.Contains("smtp.example.test", page.Markup, StringComparison.Ordinal));
        _smtp.WriteCode = ManagementCode.AuthenticationRequired;
        page.Find("#smtp-host").Change("smtp.keep.test");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("重新登录", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("smtp.example.test", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("smtp.keep.test", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("#smtp-host"));
    }

    [Fact]
    public void ToggleUsesChineseStatusAndDoesNotShowRawException()
    {
        Authenticate();
        _smtp.Item = Configured(enabled: true);
        var page = Render<NotificationsPage>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='toggle-smtp']")));
        page.Find("[data-testid='toggle-smtp']").Click();
        page.WaitForAssertion(() => Assert.Contains("SMTP 通知已停用", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Exception", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", page.Markup, StringComparison.Ordinal);
    }

    private void Authenticate()
    {
        SignIn("管理员");
        Authorization.SetClaims(
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("N")),
            new Claim(AdminAuthenticationDefaults.SecurityStampClaimType, "synthetic-stamp"));
    }

    private static SmtpSettingsItem Configured(
        bool enabled = true,
        bool hasCredential = true,
        IReadOnlyList<SmtpRecipientItem>? recipients = null) =>
        new(
            SettingsId,
            "smtp.example.test",
            587,
            1,
            "ops@example.test",
            30,
            enabled,
            hasCredential,
            recipients ?? [new SmtpRecipientItem(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "ops@example.test")],
            "AAAAAAAAAAA=");

    private sealed class FakeSmtpSettings : ISmtpSettingsService
    {
        private readonly HashSet<Guid> _tests = [];
        private int _serial = 1;

        public int GetCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public int RotateCalls { get; private set; }
        public int QueueTestCalls { get; private set; }
        public int UniqueTestIds => _tests.Count;
        public ManagementCode GetCode { get; set; } = ManagementCode.Succeeded;
        public ManagementCode WriteCode { get; set; } = ManagementCode.Succeeded;
        public SmtpSettingsItem Item { get; set; } = new(
            SettingsId, string.Empty, 587, 1, string.Empty, 30, false, false, [], string.Empty);
        public string? LastPassword { get; private set; }
        public Guid LastRequestId { get; private set; }

        public Task<ManagementResult<SmtpSettingsItem>> GetAsync(
            AdminSession actor, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Result(GetCode, Item);
        }

        public Task<ManagementResult<SmtpSettingsItem>> SaveAsync(
            AdminSession actor, string? version, SmtpSettingsInput input,
            CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            if (WriteCode != ManagementCode.Succeeded)
            {
                return Result(WriteCode);
            }

            Item = new(
                SettingsId,
                input.Host,
                input.Port,
                input.SecurityMode,
                input.FromAddress,
                input.TimeoutSeconds,
                input.IsEnabled,
                Item.HasCredential,
                Item.Recipients,
                NextVersion());
            return Result(ManagementCode.Succeeded, Item);
        }

        public Task<ManagementResult<SmtpSettingsItem>> SetEnabledAsync(
            AdminSession actor, string version, bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            if (WriteCode != ManagementCode.Succeeded)
            {
                return Result(WriteCode);
            }

            Item = Item with { IsEnabled = isEnabled, Version = NextVersion() };
            return Result(ManagementCode.Succeeded, Item);
        }

        public Task<ManagementResult<SmtpSettingsItem>> RotatePasswordAsync(
            AdminSession actor, string version, string username, string password,
            CancellationToken cancellationToken = default)
        {
            RotateCalls++;
            LastPassword = password;
            if (WriteCode != ManagementCode.Succeeded)
            {
                return Result(WriteCode);
            }

            Item = Item with { HasCredential = true, Version = NextVersion() };
            return Result(ManagementCode.Succeeded, Item);
        }

        public Task<ManagementResult<SmtpSettingsItem>> ClearPasswordAsync(
            AdminSession actor, string version, CancellationToken cancellationToken = default)
        {
            if (WriteCode != ManagementCode.Succeeded)
            {
                return Result(WriteCode);
            }

            Item = Item with { HasCredential = false, Version = NextVersion() };
            return Result(ManagementCode.Succeeded, Item);
        }

        public Task<ManagementResult<SmtpSettingsItem>> AddRecipientAsync(
            AdminSession actor, string version, string address,
            CancellationToken cancellationToken = default)
        {
            if (WriteCode != ManagementCode.Succeeded)
            {
                return Result(WriteCode);
            }

            var recipients = Item.Recipients
                .Append(new SmtpRecipientItem(Guid.NewGuid(), address))
                .ToArray();
            Item = Item with { Recipients = recipients, Version = NextVersion() };
            return Result(ManagementCode.Succeeded, Item);
        }

        public Task<ManagementResult<SmtpSettingsItem>> RemoveRecipientAsync(
            AdminSession actor, string version, Guid recipientId,
            CancellationToken cancellationToken = default)
        {
            if (WriteCode != ManagementCode.Succeeded)
            {
                return Result(WriteCode);
            }

            Item = Item with
            {
                Recipients = Item.Recipients.Where(x => x.Id != recipientId).ToArray(),
                Version = NextVersion(),
            };
            return Result(ManagementCode.Succeeded, Item);
        }

        public Task<ManagementResult<SmtpSettingsItem>> QueueTestAsync(
            AdminSession actor, string version, Guid requestId,
            CancellationToken cancellationToken = default)
        {
            QueueTestCalls++;
            LastRequestId = requestId;
            if (WriteCode != ManagementCode.Succeeded)
            {
                return Result(WriteCode);
            }

            _tests.Add(requestId);
            return Result(ManagementCode.Succeeded, Item);
        }

        private static Task<ManagementResult<SmtpSettingsItem>> Result(
            ManagementCode code, SmtpSettingsItem? item = null) =>
            Task.FromResult(new ManagementResult<SmtpSettingsItem>(code, item));

        private string NextVersion() => Convert.ToBase64String(BitConverter.GetBytes(++_serial).Concat(new byte[4]).ToArray());
    }
}
