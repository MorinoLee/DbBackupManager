using Bunit;
using DbBackupManager.Web.Components.Shared;

namespace DbBackupManager.Web.Tests.Components;

public sealed class PageHeaderTests : MudBlazorComponentTest
{
    [Fact]
    public void PageHeaderRendersSingleH1DescriptionAndActions()
    {
        var component = Render<PageHeader>(parameters => parameters
            .Add(header => header.Title, "任务记录")
            .Add(header => header.Description, "查看备份任务的状态与结果。")
            .Add(header => header.Actions, "<button data-testid=\"header-action\">新建</button>"));

        var title = component.Find("h1");
        Assert.Single(component.FindAll("h1"));
        Assert.Equal("任务记录", title.TextContent);
        Assert.Equal("查看备份任务的状态与结果。", component.Find("[data-testid='page-header-description']").TextContent);
        Assert.Equal("新建", component.Find("[data-testid='header-action']").TextContent);
    }

    [Fact]
    public void PageHeaderOmitsOptionalRegions()
    {
        var component = Render<PageHeader>(parameters => parameters.Add(header => header.Title, "工作台"));

        Assert.Empty(component.FindAll("[data-testid='page-header-description']"));
        Assert.Empty(component.FindAll(".page-header__actions"));
        Assert.Empty(component.FindAll(".page-header__breadcrumbs"));
    }
}

public sealed class TaskStatusChipTests : MudBlazorComponentTest
{
    [Theory]
    [InlineData("Pending", "等待执行")]
    [InlineData("Running", "执行中")]
    [InlineData("NeedsAttention", "待核对")]
    [InlineData("Succeeded", "成功")]
    [InlineData("Failed", "失败")]
    [InlineData("Cancelled", "已取消")]
    public void TaskStatusChipAlwaysShowsChineseTextLabel(string status, string expected)
    {
        var component = Render<TaskStatusChip>(parameters => parameters.Add(chip => chip.Status, status));

        Assert.Equal(expected, component.Find("[data-testid='task-status-chip']").TextContent.Trim());
    }
}

public sealed class FormattersTests
{
    [Fact]
    public void NullValuesRenderAsPlaceholder()
    {
        Assert.Equal("—", Formatters.LocalDateTime(null));
        Assert.Equal("—", Formatters.Bytes(null));
    }

    [Fact]
    public void LocalDateTimeUsesFullTimestamp()
    {
        var value = new DateTimeOffset(2026, 9, 8, 12, 30, 45, TimeSpan.Zero);

        Assert.Equal(value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), Formatters.LocalDateTime(value));
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(2048L, "2 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
    public void BytesUseReadableUnits(long value, string expected)
    {
        Assert.Equal(expected, Formatters.Bytes(value));
    }
}
