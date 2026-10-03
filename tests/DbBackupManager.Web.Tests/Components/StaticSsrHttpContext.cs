using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;

namespace DbBackupManager.Web.Tests.Components;

/// <summary>
/// 模拟带 <see cref="ExcludeFromInteractiveRoutingAttribute" /> 端点元数据的请求上下文，
/// 使 AcceptsInteractiveRouting 返回 false，用于覆盖 Static SSR 渲染分支。
/// </summary>
internal static class StaticSsrHttpContext
{
    public static HttpContext Create()
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(
            requestDelegate: null,
            metadata: new EndpointMetadataCollection(new ExcludeFromInteractiveRoutingAttribute()),
            displayName: "静态页面"));
        return context;
    }
}
