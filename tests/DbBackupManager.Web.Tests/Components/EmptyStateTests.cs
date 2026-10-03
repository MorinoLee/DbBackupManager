using Bunit;
using DbBackupManager.Web.Components.Shared;

namespace DbBackupManager.Web.Tests.Components;

public sealed class EmptyStateTests : MudBlazorComponentTest
{
    [Fact]
    public void EmptyStateOnlyShowsTitleWhenNothingElseIsSupplied()
    {
        var component = Render<EmptyState>();

        Assert.Contains("暂无数据", component.Find("[data-testid='empty-state']").TextContent, StringComparison.Ordinal);
        Assert.Empty(component.FindAll("[data-testid='empty-state-description']"));
        Assert.Empty(component.FindAll(".app-state-actions"));
    }

    [Fact]
    public void EmptyStateShowsDescriptionAndCallerSuppliedAction()
    {
        var component = Render<EmptyState>(parameters => parameters
            .Add(state => state.Title, "还没有备份任务")
            .Add(state => state.Description, "创建第一个备份任务后，这里会显示执行记录。")
            .Add(state => state.Actions, "<button data-testid='empty-state-action'>新建任务</button>"));

        Assert.Contains(
            "还没有备份任务",
            component.Find("[data-testid='empty-state']").TextContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "创建第一个备份任务后",
            component.Find("[data-testid='empty-state-description']").TextContent,
            StringComparison.Ordinal);
        Assert.Equal("新建任务", component.Find("[data-testid='empty-state-action']").TextContent);
    }
}
