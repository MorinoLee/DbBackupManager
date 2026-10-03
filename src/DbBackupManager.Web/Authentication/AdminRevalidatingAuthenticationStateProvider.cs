using DbBackupManager.Application.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace DbBackupManager.Web.Authentication;

internal sealed class AdminRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IAdminIdentityService identityService,
    ILogger<AdminRevalidatingAuthenticationStateProvider> logger)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    private static readonly EventId RevalidationFailureEvent = new(4102, "AdminCircuitRevalidationFailed");
    private static readonly Action<ILogger, string, Exception?> LogRevalidationFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            RevalidationFailureEvent,
            "管理员 Circuit 重验证失败，已按失败关闭处理。异常类型：{ExceptionType}");

    protected override TimeSpan RevalidationInterval =>
        AdminAuthenticationDefaults.RevalidationInterval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        return await ValidatePrincipalAsync(authenticationState.User, cancellationToken);
    }

    internal async Task<bool> ValidatePrincipalAsync(
        System.Security.Claims.ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        if (!AdminPrincipalFactory.TryGetSessionClaims(
                principal,
                out var adminUserId,
                out var securityStamp))
        {
            return false;
        }

        try
        {
            var result = await identityService.ValidateSessionAsync(
                adminUserId,
                securityStamp,
                cancellationToken);
            return result.IsSucceeded;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRevalidationFailure(logger, exception.GetType().Name, null);
            return false;
        }
    }
}
