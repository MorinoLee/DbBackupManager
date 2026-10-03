using Bunit;
using DbBackupManager.Web.Components.Shared;
using Microsoft.AspNetCore.Components.Web;

namespace DbBackupManager.Web.Tests.Components;

public sealed class ErrorStateTests : MudBlazorComponentTest
{
    private const string RetrySelector = "[data-testid='error-state-retry']";

    [Fact]
    public void ErrorStateShowsChineseGuidanceAndHidesRetryWithoutHandler()
    {
        var component = Render<ErrorState>();

        var root = component.Find("[data-testid='error-state']");

        Assert.Contains("加载失败", root.TextContent, StringComparison.Ordinal);
        Assert.Contains("请稍后重试", root.TextContent, StringComparison.Ordinal);
        Assert.Empty(component.FindAll(RetrySelector));
    }

    [Fact]
    public void ErrorStateShowsCallerSuppliedDescription()
    {
        var component = Render<ErrorState>(parameters => parameters
            .Add(state => state.Title, "无法读取备份任务")
            .Add(state => state.Description, "服务暂时不可用，请稍后再试。"));

        Assert.Equal(
            "服务暂时不可用，请稍后再试。",
            component.Find("[data-testid='error-state-description']").TextContent);
    }

    [Fact]
    public async Task RetryStaysDisabledUntilTheCallbackCompletesAndRunsOnlyOnce()
    {
        var retryStarted = new TaskCompletionSource();
        var releaseRetry = new TaskCompletionSource();
        var invocations = 0;

        var component = Render<ErrorState>(parameters => parameters.Add(
            state => state.OnRetry,
            async () =>
            {
                Interlocked.Increment(ref invocations);
                retryStarted.TrySetResult();
                await releaseRetry.Task;
            }));

        var firstClick = component.Find(RetrySelector).ClickAsync(new MouseEventArgs());
        await retryStarted.Task;

        component.WaitForAssertion(() => Assert.True(component.Find(RetrySelector).HasAttribute("disabled")));

        var repeatedClick = component.Find(RetrySelector).ClickAsync(new MouseEventArgs());
        releaseRetry.SetResult();
        await Task.WhenAll(firstClick, repeatedClick);

        component.WaitForAssertion(() => Assert.False(component.Find(RetrySelector).HasAttribute("disabled")));
        Assert.Equal(1, invocations);
    }
}
