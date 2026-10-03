using System.Security.Claims;
using Bunit;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Web.Authentication;
using DbBackupManager.Web.BackupTasks;
using DbBackupManager.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DbBackupManager.Web.Tests.Components;

public sealed class BackupFilesTests : MudBlazorComponentTest
{
    private readonly FakeMonitoring _service = new();
    private readonly TaskRefreshNotifier _notifier = new();
    public BackupFilesTests()
    {
        Services.AddSingleton<IBackupMonitoringService>(_service);
        Services.AddSingleton(_notifier);
    }
    [Fact]
    public void AnonymousDoesNotQueryFiles()
    {
        var page = Render<BackupFiles>();
        Assert.Equal(0, _service.Calls);
        Assert.Empty(page.FindAll("[data-testid='file-list']"));
    }
    [Fact]
    public void QueryAndPagingRetainFiltersAndFileEvidenceBoundary()
    {
        Authenticate();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/backup-files?q=synthetic&from=2026-09-01&until=2026-09-10&oldest=true");
        var page = Render<BackupFiles>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='file-task-detail']")));
        Assert.Equal("synthetic", _service.LastSearch!.Search);
        Assert.True(_service.LastSearch.OldestFirst);
        Assert.Contains("不会重新检查文件是否存在", page.Markup, StringComparison.Ordinal);
        Assert.Contains("本地", page.Markup, StringComparison.Ordinal);
        Assert.Contains("远程", page.Markup, StringComparison.Ordinal);
        Assert.Contains("SFTP", page.Markup, StringComparison.Ordinal);
        Assert.Contains("删除失败", page.Markup, StringComparison.Ordinal);
        Assert.Contains("主机密钥指纹不匹配", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("HostKeyMismatch", page.Markup, StringComparison.Ordinal);
        Assert.Contains("本页不提供文件删除操作", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-testid='delete-file']"));
        page.Find("[data-testid='next-page']").Click();
        Assert.Contains("page=1", navigation.Uri, StringComparison.Ordinal);
        Assert.Contains("q=synthetic", navigation.Uri, StringComparison.Ordinal);
    }
    [Fact]
    public void TypingAndApplyingSearchUpdatesAddressOnFirstClick()
    {
        Authenticate();
        var page = Render<BackupFiles>();
        page.WaitForAssertion(() => Assert.Contains("backup.bak", page.Markup, StringComparison.Ordinal));
        page.Find("input[data-testid='search-text']").Input("needle");
        page.Find("[data-testid='apply-search']").Click();
        Assert.Contains("q=needle", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void RevokedIdentityClearsFilePaths()
    {
        Authenticate();
        var page = Render<BackupFiles>();
        page.WaitForAssertion(() => Assert.Contains("backup.bak", page.Markup, StringComparison.Ordinal));
        _service.Code = BackupManagementCode.AuthenticationRequired;
        _notifier.Publish();
        page.WaitForAssertion(() => Assert.Contains("重新登录", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("backup.bak", page.Markup, StringComparison.Ordinal);
    }
    [Fact]
    public async Task OldQueryCannotOverwriteNewFilterFailure()
    {
        Authenticate();
        var page = Render<BackupFiles>();
        page.WaitForAssertion(() => Assert.Contains("backup.bak", page.Markup, StringComparison.Ordinal));
        var pending = new TaskCompletionSource<BackupManagementResult<BackupPage<BackupFileSummary>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.PendingFiles = pending;
        var refresh = page.Find("[data-testid='refresh-files']").ClickAsync(new());
        page.WaitForAssertion(() => Assert.Null(_service.PendingFiles));
        _service.Code = BackupManagementCode.Unavailable;
        await page.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo("/backup-files?q=new-query"));
        pending.SetResult(new(BackupManagementCode.Succeeded, _service.Files));
        await refresh;
        page.WaitForAssertion(() => Assert.Contains("暂不可用", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("backup.bak", page.Markup, StringComparison.Ordinal);
    }
    private void Authenticate()
    {
        SignIn("管理员");
        Authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("N")),
            new Claim(AdminAuthenticationDefaults.SecurityStampClaimType, "synthetic"));
    }
}
