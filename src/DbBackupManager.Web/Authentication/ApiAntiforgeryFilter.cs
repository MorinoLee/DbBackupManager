using System.Security.Claims;
using DbBackupManager.Application.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DbBackupManager.Web.Authentication;

internal sealed class ApiAntiforgeryFilter(
    IAntiforgery antiforgery,
    IAdminIdentityService identityService,
    ILogger<ApiAntiforgeryFilter> logger) : IAsyncAuthorizationFilter
{
    private static readonly EventId AuditFailureEvent = new(4103, "CsrfAuditFailed");
    private static readonly Action<ILogger, string, Exception?> LogAuditFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            AuditFailureEvent,
            "CSRF 拒绝审计写入失败，请求仍保持拒绝。异常类型：{ExceptionType}");

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var request = context.HttpContext.Request;
        if (!request.Path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase)
            || HttpMethods.IsGet(request.Method)
            || HttpMethods.IsHead(request.Method)
            || HttpMethods.IsOptions(request.Method)
            || HttpMethods.IsTrace(request.Method))
        {
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            await TryRecordAuditAsync(context.HttpContext);
            context.Result = new ObjectResult(ApiProblemDetails.Create(
                context.HttpContext,
                StatusCodes.Status400BadRequest,
                "invalid_csrf_token",
                "安全令牌无效或已过期。"))
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }
    }

    private async Task TryRecordAuditAsync(HttpContext httpContext)
    {
        Guid? actorAdminUserId = null;
        var idValue = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParseExact(idValue, "N", out var parsedId))
        {
            actorAdminUserId = parsedId;
        }

        try
        {
            await identityService.RecordCsrfFailureAsync(
                actorAdminUserId,
                httpContext.RequestAborted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogAuditFailure(logger, exception.GetType().Name, null);
        }
    }
}
