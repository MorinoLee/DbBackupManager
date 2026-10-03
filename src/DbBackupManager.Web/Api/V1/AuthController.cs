using DbBackupManager.Application.Identity;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Web.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DbBackupManager.Web.Api.V1;

[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(
    IAdminIdentityService identityService,
    IAntiforgery antiforgery,
    TimeProvider timeProvider) : ControllerBase
{
    [HttpGet("csrf")]
    [AllowAnonymous]
    [ProducesResponseType<CsrfTokenResponse>(StatusCodes.Status200OK)]
    public ActionResult<CsrfTokenResponse> GetCsrfToken()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new CsrfTokenResponse(
            tokens.RequestToken
                ?? throw new InvalidOperationException("无法生成安全令牌。")));
    }

    [HttpGet("setup-status")]
    [AllowAnonymous]
    [ProducesResponseType<SetupStatusResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SetupStatusResponse>> GetSetupStatus(
        CancellationToken cancellationToken)
    {
        var requiresSetup = await identityService.IsSetupRequiredAsync(cancellationToken);
        return Ok(new SetupStatusResponse(requiresSetup));
    }

    [HttpPost("setup")]
    [AllowAnonymous]
    [EnableRateLimiting(AdminAuthenticationDefaults.AuthRateLimitPolicy)]
    [ProducesResponseType<AdminSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AdminSessionResponse>> Setup(
        AdminSetupRequest request,
        CancellationToken cancellationToken)
    {
        var result = await identityService.SetupAsync(
            request.Username,
            request.Password,
            cancellationToken);

        if (!result.IsSucceeded)
        {
            return MapFailure(result);
        }

        await SignInAsync(result.Session!);
        return Ok(MapSession(result.Session!));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(AdminAuthenticationDefaults.AuthRateLimitPolicy)]
    [ProducesResponseType<AdminSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AdminSessionResponse>> Login(
        AdminLoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await identityService.AuthenticateAsync(
            request.Username,
            request.Password,
            cancellationToken);

        if (!result.IsSucceeded)
        {
            return MapFailure(result);
        }

        await SignInAsync(result.Session!);
        return Ok(MapSession(result.Session!));
    }

    [HttpPost("logout")]
    [Authorize(Policy = AdminAuthenticationDefaults.AuthorizationPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(AdminAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize(Policy = AdminAuthenticationDefaults.AuthorizationPolicy)]
    [ProducesResponseType<AdminSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AdminSessionResponse>> GetCurrentAdmin(
        CancellationToken cancellationToken)
    {
        var result = await ValidateCurrentSessionAsync(cancellationToken);
        return result.IsSucceeded
            ? Ok(MapSession(result.Session!))
            : MapFailure(result);
    }

    [HttpPost("change-password")]
    [Authorize(Policy = AdminAuthenticationDefaults.AuthorizationPolicy)]
    [EnableRateLimiting(AdminAuthenticationDefaults.AuthRateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ChangePassword(
        AdminChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        if (!AdminPrincipalFactory.TryGetSessionClaims(User, out var adminUserId, out var securityStamp))
        {
            return MapFailure(new AdminIdentityResult(AdminIdentityResultCode.AuthenticationRequired));
        }

        var result = await identityService.ChangePasswordAsync(
            adminUserId,
            securityStamp,
            request.CurrentPassword,
            request.NewPassword,
            cancellationToken);

        if (!result.IsSucceeded)
        {
            if (result.Code == AdminIdentityResultCode.AuthenticationRequired)
            {
                await HttpContext.SignOutAsync(AdminAuthenticationDefaults.AuthenticationScheme);
            }

            return MapFailure(result);
        }

        await HttpContext.SignOutAsync(AdminAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    private async Task<AdminIdentityResult> ValidateCurrentSessionAsync(
        CancellationToken cancellationToken)
    {
        if (!AdminPrincipalFactory.TryGetSessionClaims(User, out var adminUserId, out var securityStamp))
        {
            return new AdminIdentityResult(AdminIdentityResultCode.AuthenticationRequired);
        }

        return await identityService.ValidateSessionAsync(
            adminUserId,
            securityStamp,
            cancellationToken);
    }

    private async Task SignInAsync(AdminSession session)
    {
        await HttpContext.SignInAsync(
            AdminAuthenticationDefaults.AuthenticationScheme,
            AdminPrincipalFactory.CreatePrincipal(session),
            AdminPrincipalFactory.CreateProperties(timeProvider));
    }

    private ObjectResult MapFailure(AdminIdentityResult result)
    {
        var (status, code, title) = result.Code switch
        {
            AdminIdentityResultCode.ValidationFailed =>
                (StatusCodes.Status400BadRequest, "validation_failed", "输入内容不符合要求。"),
            AdminIdentityResultCode.InvalidCredentials =>
                (StatusCodes.Status401Unauthorized, "invalid_credentials", "账号或密码无效。"),
            AdminIdentityResultCode.SetupUnavailable =>
                (StatusCodes.Status409Conflict, "setup_unavailable", "系统已经完成首次设置。"),
            AdminIdentityResultCode.ConcurrencyConflict =>
                (StatusCodes.Status409Conflict, "concurrency_conflict", "数据已发生变化，请重试。"),
            _ =>
                (StatusCodes.Status401Unauthorized, "authentication_required", "需要重新登录。"),
        };
        var problem = ApiProblemDetails.Create(HttpContext, status, code, title);

        if (result.ValidationFailures is { Count: > 0 })
        {
            problem.Extensions["errors"] = result.ValidationFailures
                .GroupBy(failure => failure.Field, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(failure => failure.Code).Distinct(StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal);
        }

        return new ObjectResult(problem) { StatusCode = status };
    }

    private static AdminSessionResponse MapSession(AdminSession session)
    {
        return new AdminSessionResponse(session.AdminUserId, session.Username);
    }
}
