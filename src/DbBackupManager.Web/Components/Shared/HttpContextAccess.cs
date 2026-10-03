namespace DbBackupManager.Web.Components.Shared;

internal static class HttpContextAccess
{
    public static bool TryGetActive(HttpContext? httpContext, out HttpContext active)
    {
        if (httpContext is null)
        {
            active = null!;
            return false;
        }

        try
        {
            // 级联 HttpContext 在请求结束后仍可能非 null，但 FeatureCollection 已被释放。
            _ = httpContext.Request.QueryString;
            active = httpContext;
            return true;
        }
        catch (ObjectDisposedException)
        {
            active = null!;
            return false;
        }
    }
}
