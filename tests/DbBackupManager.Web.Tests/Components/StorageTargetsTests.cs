using System.Security.Claims;
using Bunit;
using DbBackupManager.Application.FileCredentials;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.Servers;
using DbBackupManager.Application.StorageTargets;
using DbBackupManager.Web.Authentication;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using StorageTargetsPage = DbBackupManager.Web.Components.Pages.StorageTargets;

namespace DbBackupManager.Web.Tests.Components;

public sealed class StorageTargetsTests : MudBlazorComponentTest
{
    private readonly FakeTargets _targets = new();
    private readonly FakeCredentials _credentials = new();

    public StorageTargetsTests()
    {
        Services.AddSingleton<IStorageTargetManagementService>(_targets);
        Services.AddSingleton<IFileCredentialService>(_credentials);
    }

    [Fact]
    public void AnonymousPageDoesNotReadProtectedTargets()
    {
        var page = Render<StorageTargetsPage>();
        Assert.Equal(0, _targets.ListCalls);
        Assert.Contains("请先登录", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyListHasCreateActionAndSavesSmbWithoutFingerprint()
    {
        Authenticate();
        _credentials.Items = [FakeCredentials.Smb];
        var provider = Render<MudDialogProvider>();
        var page = Render<StorageTargetsPage>();
        page.WaitForAssertion(() => Assert.Contains("暂无存储目标", page.Markup, StringComparison.Ordinal));
        page.Find("[data-testid='new-target']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#target-name")));
        var editor = provider.FindComponent<DbBackupManager.Web.Components.Pages.StorageTargetEditDialog>();
        editor.Instance.Model.Name = "演示目标";
        editor.Instance.Model.Host = "synthetic-host";
        editor.Instance.Model.BasePath = "synthetic-share";
        editor.Instance.Model.CredentialId = FakeCredentials.Smb.Id;
        provider.Find("#target-name").Change("演示目标");
        provider.Find("#target-host").Change("synthetic-host");
        provider.Find("#target-base-path").Change("synthetic-share");
        provider.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Equal(1, _targets.SaveCalls));
        Assert.Equal(1, _targets.Saved?.Protocol);
        Assert.Null(_targets.Saved?.Port);
        Assert.Null(_targets.Saved?.Fingerprint);
        page.WaitForAssertion(() => Assert.Contains("存储目标已保存", page.Markup, StringComparison.Ordinal));
        Assert.Contains("不会自动信任未知主机", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-testid='delete-target']"));
    }

    [Fact]
    public void SftpEditorRequiresFingerprintAndKeepsInputOnConflict()
    {
        Authenticate();
        _credentials.Items = [FakeCredentials.SftpKey];
        _targets.Items = [FakeTargets.Sftp];
        var provider = Render<MudDialogProvider>();
        var page = Render<StorageTargetsPage>();
        page.WaitForAssertion(() => Assert.Contains("SHA256:synthetic-fingerprint", page.Markup, StringComparison.Ordinal));
        page.Find("[data-testid='edit-target']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#target-fingerprint")));
        Assert.Contains("系统不会自动信任未知主机", provider.Markup, StringComparison.Ordinal);
        provider.Find("#target-name").Change("改名目标");
        _targets.SaveCode = ManagementCode.Conflict;
        provider.Find("form").Submit();
        provider.WaitForAssertion(() => Assert.Contains("已被其他操作更新", provider.Markup, StringComparison.Ordinal));
        Assert.Equal("改名目标", provider.Find("#target-name").GetAttribute("value"));
        Assert.Equal("SHA256:synthetic-fingerprint", provider.Find("#target-fingerprint").GetAttribute("value"));
    }

    [Fact]
    public void ProbeFailureMapsHostKeyMismatchWithoutEchoingEndpoint()
    {
        Authenticate();
        _credentials.Items = [FakeCredentials.SftpKey];
        _targets.Items = [FakeTargets.Sftp];
        _targets.ProbeCode = ManagementCode.TargetFailed;
        _targets.ProbeError = "HostKeyMismatch";
        var page = Render<StorageTargetsPage>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='probe-target']")));
        page.Find("[data-testid='probe-target']").Click();
        page.WaitForAssertion(() => Assert.Contains("主机密钥指纹不匹配", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("HostKeyMismatch", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-exception", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledTargetCannotProbe()
    {
        Authenticate();
        _credentials.Items = [FakeCredentials.Smb];
        _targets.Items = [FakeTargets.Smb with
        {
            Settings = FakeTargets.Smb.Settings with { IsEnabled = false },
        }];
        var page = Render<StorageTargetsPage>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='probe-target']")));
        Assert.True(page.Find("[data-testid='probe-target']").HasAttribute("disabled"));
    }

    [Fact]
    public void RevokedIdentityDuringSaveClearsListAndClosesEditor()
    {
        Authenticate();
        _credentials.Items = [FakeCredentials.Smb];
        _targets.Items = [FakeTargets.Smb];
        var provider = Render<MudDialogProvider>();
        var page = Render<StorageTargetsPage>();
        page.WaitForAssertion(() => Assert.Contains("演示 SMB", page.Markup, StringComparison.Ordinal));
        _targets.SaveCode = ManagementCode.AuthenticationRequired;
        page.Find("[data-testid='edit-target']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#target-name")));
        provider.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("重新登录", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("演示 SMB", page.Markup, StringComparison.Ordinal);
        provider.WaitForAssertion(() => Assert.Empty(provider.FindAll("#target-name")));
    }

    [Fact]
    public void ToggleDisableUsesCurrentVersion()
    {
        Authenticate();
        _credentials.Items = [FakeCredentials.Smb];
        _targets.Items = [FakeTargets.Smb];
        var page = Render<StorageTargetsPage>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='toggle-target']")));
        page.Find("[data-testid='toggle-target']").Click();
        page.WaitForAssertion(() => Assert.False(_targets.LastEnabled));
        Assert.Equal(FakeTargets.Smb.Version, _targets.LastVersion);
        Assert.Contains("无法使用此目标", page.Markup, StringComparison.Ordinal);
    }

    private void Authenticate()
    {
        SignIn("管理员");
        Authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("N")),
            new Claim(AdminAuthenticationDefaults.SecurityStampClaimType, "synthetic-stamp"));
    }

    private sealed class FakeTargets : IStorageTargetManagementService
    {
        public static readonly StorageTargetItem Smb = new(
            Guid.NewGuid(),
            new("演示 SMB", 1, "synthetic-host", null, "synthetic-share", FakeCredentials.Smb.Id, null, true),
            "AAAAAAAAAAA=");
        public static readonly StorageTargetItem Sftp = new(
            Guid.NewGuid(),
            new("演示 SFTP", 2, "synthetic-sftp", 22, "/synthetic", FakeCredentials.SftpKey.Id,
                "SHA256:synthetic-fingerprint", true),
            "AAAAAAAAAAA=");

        public int ListCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public ManagementCode ListCode { get; set; } = ManagementCode.Succeeded;
        public ManagementCode SaveCode { get; set; } = ManagementCode.Succeeded;
        public ManagementCode ProbeCode { get; set; } = ManagementCode.Succeeded;
        public string? ProbeError { get; set; }
        public IReadOnlyList<StorageTargetItem> Items { get; set; } = [];
        public StorageTargetInput? Saved { get; private set; }
        public bool LastEnabled { get; private set; } = true;
        public string? LastVersion { get; private set; }

        public Task<ManagementResult<IReadOnlyList<StorageTargetItem>>> ListAsync(
            AdminSession actor, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(new ManagementResult<IReadOnlyList<StorageTargetItem>>(ListCode, Items));
        }

        public Task<ManagementResult<StorageTargetItem>> SaveAsync(
            AdminSession actor, Guid? id, string? version, StorageTargetInput input,
            CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            Saved = input;
            LastVersion = version;
            var item = new StorageTargetItem(id ?? Guid.NewGuid(), input, version ?? "AAAAAAAAAAA=");
            return Task.FromResult(new ManagementResult<StorageTargetItem>(SaveCode, SaveCode == ManagementCode.Succeeded ? item : null));
        }

        public Task<ManagementResult<StorageTargetItem>> SetEnabledAsync(
            AdminSession actor, Guid id, string version, bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            LastEnabled = isEnabled;
            LastVersion = version;
            var current = Items.Single(x => x.Id == id);
            var updated = current with { Settings = current.Settings with { IsEnabled = isEnabled } };
            Items = [updated];
            return Task.FromResult(new ManagementResult<StorageTargetItem>(ManagementCode.Succeeded, updated));
        }

        public Task<ManagementResult<StorageTargetItem>> TestConnectionAsync(
            AdminSession actor, Guid id, string version,
            CancellationToken cancellationToken = default)
        {
            LastVersion = version;
            var current = Items.Single(x => x.Id == id);
            return Task.FromResult(new ManagementResult<StorageTargetItem>(ProbeCode, current, ProbeError));
        }
    }

    private sealed class FakeCredentials : IFileCredentialService
    {
        public static readonly FileCredentialItem Smb = new(
            Guid.NewGuid(), "SMB 凭据", "share-user", FileCredentialKind.SmbPassword, true, "AAAAAAAAAAA=");
        public static readonly FileCredentialItem SftpKey = new(
            Guid.NewGuid(), "SFTP 私钥", "sftp-user", FileCredentialKind.SftpPrivateKey, true, "AAAAAAAAAAA=", true);

        public IReadOnlyList<FileCredentialItem> Items { get; set; } = [];

        public Task<FileCredentialResult<IReadOnlyList<FileCredentialItem>>> ListAsync(
            AdminSession actor, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FileCredentialResult<IReadOnlyList<FileCredentialItem>>(
                FileCredentialResultCode.Succeeded, Items));

        public Task<FileCredentialResult<FileCredentialItem>> CreateAsync(
            AdminSession actor, string name, string username, FileCredentialKind kind, string password,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FileCredentialResult<FileCredentialItem>> RotatePasswordAsync(
            AdminSession actor, Guid id, string version, string password,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FileCredentialResult<FileCredentialItem>> SetEnabledAsync(
            AdminSession actor, Guid id, string version, bool isEnabled,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FileCredentialResult<FileCredentialItem>> CreatePrivateKeyAsync(
            AdminSession actor, string name, string username, string privateKey, string? passphrase,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FileCredentialResult<FileCredentialItem>> RotatePrivateKeyAsync(
            AdminSession actor, Guid id, string version, string privateKey, string? passphrase,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
