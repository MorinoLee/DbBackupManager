using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace DbBackupManager.Web.Tests.Components;

/// <summary>
/// 验证 bUnit 中独立渲染的 MudDialogProvider 能展示同作用域 DialogService 打开的弹窗；
/// 后续页面弹窗测试都依赖这一模式。
/// </summary>
public sealed class DialogProbeTests : MudBlazorComponentTest
{
    [Fact]
    public async Task DialogShownViaServiceRendersInSeparatelyRenderedProvider()
    {
        var provider = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();

        await dialogs.ShowAsync<ProbeDialog>("探头");

        provider.WaitForAssertion(() =>
            Assert.Contains("探头内容", provider.Markup, StringComparison.Ordinal));
    }

    private sealed class ProbeDialog : ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.AddContent(0, "探头内容");
        }
    }
}
