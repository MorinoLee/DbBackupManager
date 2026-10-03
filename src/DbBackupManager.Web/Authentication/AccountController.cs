using DbBackupManager.Application.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;

namespace DbBackupManager.Web.Authentication;

[Route("account")]
public sealed class AccountController(
    IAdminIdentityService identityService,
    TimeProvider timeProvider) : Controller
{
    [HttpPost("setup")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(AdminAuthenticationDefaults.AuthRateLimitPolicy)]
    public async Task<IActionResult> Setup(
        [FromForm] string? username,
        [FromForm] string? password,
        [FromForm] string? returnUrl,
        CancellationToken cancellationToken)
    {
        var result = await identityService.SetupAsync(username, password, cancellationToken);
        if (!result.IsSucceeded)
        {
            return RedirectWithError("/setup", result.Code, returnUrl);
        }

        await SignInAsync(result.Session!);
        return LocalRedirect(GetLocalReturnUrl(returnUrl));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(AdminAuthenticationDefaults.AuthRateLimitPolicy)]
    public async Task<IActionResult> Login(
        [FromForm] string? username,
        [FromForm] string? password,
        [FromForm] string? returnUrl,
        CancellationToken cancellationToken)
    {
        var result = await identityService.AuthenticateAsync(username, password, cancellationToken);
        if (!result.IsSucceeded)
        {
            return RedirectWithError("/login", result.Code, returnUrl);
        }

        await SignInAsync(result.Session!);
        return LocalRedirect(GetLocalReturnUrl(returnUrl));
    }

    [HttpPost("logout")]
    [Authorize(Policy = AdminAuthenticationDefaults.AuthorizationPolicy)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout([FromForm] string? returnUrl)
    {
        await HttpContext.SignOutAsync(AdminAuthenticationDefaults.AuthenticationScheme);
        return LocalRedirect(GetLocalReturnUrl(returnUrl, "/login"));
    }

    [HttpPost("change-password")]
    [Authorize(Policy = AdminAuthenticationDefaults.AuthorizationPolicy)]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(AdminAuthenticationDefaults.AuthRateLimitPolicy)]
    public async Task<IActionResult> ChangePassword(
        [FromForm] string? currentPassword,
        [FromForm] string? newPassword,
        CancellationToken cancellationToken)
    {
        if (!AdminPrincipalFactory.TryGetSessionClaims(User, out var adminUserId, out var securityStamp))
        {
            await HttpContext.SignOutAsync(AdminAuthenticationDefaults.AuthenticationScheme);
            return RedirectWithError(
                "/login",
                AdminIdentityResultCode.AuthenticationRequired,
                null);
        }

        var result = await identityService.ChangePasswordAsync(
            adminUserId,
            securityStamp,
            currentPassword,
            newPassword,
            cancellationToken);

        if (!result.IsSucceeded)
        {
            if (result.Code == AdminIdentityResultCode.AuthenticationRequired)
            {
                await HttpContext.SignOutAsync(AdminAuthenticationDefaults.AuthenticationScheme);
                return RedirectWithError("/login", result.Code, null);
            }

            return RedirectWithError("/change-password", result.Code, null);
        }

        await HttpContext.SignOutAsync(AdminAuthenticationDefaults.AuthenticationScheme);
        return RedirectWithError("/login", null, null, "password_changed");
    }

    private async Task SignInAsync(AdminSession session)
    {
        await HttpContext.SignInAsync(
            AdminAuthenticationDefaults.AuthenticationScheme,
            AdminPrincipalFactory.CreatePrincipal(session),
            AdminPrincipalFactory.CreateProperties(timeProvider));
    }

    private RedirectResult RedirectWithError(
        string path,
        AdminIdentityResultCode? resultCode,
        string? returnUrl,
        string? status = null)
    {
        var query = new Dictionary<string, string?>();
        if (resultCode is not null)
        {
            query["error"] = MapErrorCode(resultCode.Value);
        }

        if (status is not null)
        {
            query["status"] = status;
        }

        if (Url.IsLocalUrl(returnUrl))
        {
            query["returnUrl"] = returnUrl;
        }

        return Redirect(QueryHelpers.AddQueryString(path, query));
    }

    private string GetLocalReturnUrl(string? returnUrl, string fallback = "/")
    {
        return Url.IsLocalUrl(returnUrl) ? returnUrl! : fallback;
    }

    private static string MapErrorCode(AdminIdentityResultCode resultCode)
    {
        return resultCode switch
        {
            AdminIdentityResultCode.ValidationFailed => "validation_failed",
            AdminIdentityResultCode.InvalidCredentials => "invalid_credentials",
            AdminIdentityResultCode.SetupUnavailable => "setup_unavailable",
            AdminIdentityResultCode.ConcurrencyConflict => "concurrency_conflict",
            _ => "authentication_required",
        };
    }
}
