using System.Globalization;
using DbBackupManager.Application.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.WebUtilities;

namespace DbBackupManager.Web.Authentication;

internal sealed class AdminCookieAuthenticationEvents(
    IAdminIdentityService identityService,
    TimeProvider timeProvider,
    ILogger<AdminCookieAuthenticationEvents> logger) : CookieAuthenticationEvents
{
    private static readonly EventId RevalidationFailureEvent = new(4101, "AdminCookieRevalidationFailed");
    private static readonly Action<ILogger, string, Exception?> LogRevalidationFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            RevalidationFailureEvent,
            "管理员 Cookie 重验证失败，已按失败关闭处理。异常类型：{ExceptionType}");

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (!RequiresRevalidation(context.Properties))
        {
            return;
        }

        if (context.Principal is null
            || !AdminPrincipalFactory.TryGetSessionClaims(
                context.Principal,
                out var adminUserId,
                out var securityStamp))
        {
            await RejectPrincipalAsync(context);
            return;
        }

        try
        {
            var result = await identityService.ValidateSessionAsync(
                adminUserId,
                securityStamp,
                context.HttpContext.RequestAborted);

            if (!result.IsSucceeded)
            {
                await RejectPrincipalAsync(context);
                return;
            }

            context.Properties.Items[AdminAuthenticationDefaults.LastValidatedUtcProperty] =
                timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            context.ShouldRenew = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRevalidationFailure(logger, exception.GetType().Name, null);
            await RejectPrincipalAsync(context);
        }
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        return WriteAuthorizationFailureAsync(
            context,
            StatusCodes.Status401Unauthorized,
            "authentication_required",
            "需要登录后才能继续。",
            context.Options.LoginPath);
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        return WriteAuthorizationFailureAsync(
            context,
            StatusCodes.Status403Forbidden,
            "access_denied",
            "当前账号无权执行此操作。",
            context.Options.AccessDeniedPath);
    }

    private bool RequiresRevalidation(AuthenticationProperties properties)
    {
        if (!properties.Items.TryGetValue(
                AdminAuthenticationDefaults.LastValidatedUtcProperty,
                out var rawValue)
            || !DateTimeOffset.TryParseExact(
                rawValue,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var lastValidatedUtc))
        {
            return true;
        }

        return timeProvider.GetUtcNow() - lastValidatedUtc
            >= AdminAuthenticationDefaults.RevalidationInterval;
    }

    private static async Task RejectPrincipalAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(AdminAuthenticationDefaults.AuthenticationScheme);
    }

    private static Task WriteAuthorizationFailureAsync(
        RedirectContext<CookieAuthenticationOptions> context,
        int statusCode,
        string code,
        string title,
        PathString pagePath)
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/problem+json";
            var problem = ApiProblemDetails.Create(context.HttpContext, statusCode, code, title);
            return context.Response.WriteAsJsonAsync(problem);
        }

        var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
        context.Response.Redirect(QueryHelpers.AddQueryString(
            pagePath.Value ?? "/login",
            "returnUrl",
            returnUrl));
        return Task.CompletedTask;
    }
}
