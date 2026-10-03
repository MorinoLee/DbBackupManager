using System.Globalization;
using System.Security.Claims;
using DbBackupManager.Application.Identity;
using Microsoft.AspNetCore.Authentication;

namespace DbBackupManager.Web.Authentication;

internal static class AdminPrincipalFactory
{
    public static ClaimsPrincipal CreatePrincipal(AdminSession session)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, session.AdminUserId.ToString("N")),
                new Claim(ClaimTypes.Name, session.Username),
                new Claim(AdminAuthenticationDefaults.SecurityStampClaimType, session.SecurityStamp),
            ],
            AdminAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        return new ClaimsPrincipal(identity);
    }

    public static AuthenticationProperties CreateProperties(TimeProvider timeProvider)
    {
        var properties = new AuthenticationProperties
        {
            AllowRefresh = true,
            IsPersistent = false,
        };
        properties.Items[AdminAuthenticationDefaults.LastValidatedUtcProperty] =
            timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        return properties;
    }

    public static bool TryGetSessionClaims(
        ClaimsPrincipal principal,
        out Guid adminUserId,
        out string securityStamp)
    {
        var idValue = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        securityStamp = principal.FindFirstValue(
            AdminAuthenticationDefaults.SecurityStampClaimType) ?? string.Empty;

        return Guid.TryParseExact(idValue, "N", out adminUserId)
            && !string.IsNullOrWhiteSpace(securityStamp);
    }
}
