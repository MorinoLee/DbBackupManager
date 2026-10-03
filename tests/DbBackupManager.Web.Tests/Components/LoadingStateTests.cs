using Bunit;
using DbBackupManager.Web.Components.Shared;

namespace DbBackupManager.Web.Tests.Components;

public sealed class LoadingStateTests : MudBlazorComponentTest
{
    [Fact]
    public void LoadingStateIsAnnouncedPolitelyWithDefaultMessage()
    {
        var component = Render<LoadingState>();

        var root = component.Find("[data-testid='loading-state']");

        Assert.Equal("status", root.GetAttribute("role"));
        Assert.Equal("polite", root.GetAttribute("aria-live"));
        Assert.Contains("正在加载数据", root.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadingStateShowsCallerSuppliedMessage()
    {
        var component = Render<LoadingState>(parameters => parameters.Add(
            state => state.Message,
            "正在读取备份任务…"));

        Assert.Contains(
            "正在读取备份任务…",
            component.Find("[data-testid='loading-state']").TextContent,
            StringComparison.Ordinal);
    }
}
