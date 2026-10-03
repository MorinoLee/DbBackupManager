using Bunit;
using Bunit.TestDoubles;
using DbBackupManager.Web.Authentication;
using DbBackupManager.Web.BackupTasks;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace DbBackupManager.Web.Tests.Components;

/// <summary>
/// MudBlazor 组件测试的统一上下文。
/// </summary>
/// <remarks>
/// MudBlazor 会注册只实现 <see cref="IAsyncDisposable"/> 的作用域服务，
/// 而 xUnit v2 默认只调用同步 <see cref="IDisposable.Dispose"/>，容器会因此抛出异常。
/// 这里通过 <see cref="IAsyncLifetime"/> 走异步释放路径，并在异步释放完成后跳过同步释放。
/// 迁移到 xUnit v3 后可以去掉这个适配。
/// </remarks>
public abstract class MudBlazorComponentTest : BunitContext, IAsyncLifetime
{
    private bool _disposedAsynchronously;

    protected MudBlazorComponentTest()
    {
        Authorization = this.AddAuthorization();
        Authorization.SetNotAuthorized();
        Services.AddMudServices();
        Services.AddSingleton<CircuitReconnectNotifier>();
        Services.AddSingleton(MonitoringRefreshOptions.Default);
        Services.AddSingleton<AntiforgeryStateProvider, TestAntiforgeryStateProvider>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    protected BunitAuthorizationContext Authorization { get; }

    protected void SignIn(string username)
    {
        Authorization.SetAuthorized(username);
        Authorization.SetPolicies(AdminAuthenticationDefaults.AuthorizationPolicy);
    }

    Task IAsyncLifetime.InitializeAsync()
    {
        return Task.CompletedTask;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await ((IAsyncDisposable)this).DisposeAsync();
        _disposedAsynchronously = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposedAsynchronously)
        {
            return;
        }

        base.Dispose(disposing);
    }
}

internal sealed class TestAntiforgeryStateProvider : AntiforgeryStateProvider
{
    public override AntiforgeryRequestToken GetAntiforgeryToken()
    {
        return new AntiforgeryRequestToken("test-request-token", "__RequestVerificationToken");
    }
}
