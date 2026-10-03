namespace DbBackupManager.Web.Authentication;

public static class AdminAuthenticationDefaults
{
    public const string AuthenticationScheme = "DbBackupManager.Admin";
    public const string AuthorizationPolicy = "DbBackupManager.Admin";
    public const string SecurityStampClaimType = "dbbackupmanager:security_stamp";
    public const string LastValidatedUtcProperty = "dbbackupmanager:last_validated_utc";
    public const string CsrfHeaderName = "X-CSRF-TOKEN";
    public const string AuthRateLimitPolicy = "authentication";
    public const string CookieName = "DbBackupManager.Admin";
    public const string AntiforgeryCookieName = "DbBackupManager.Antiforgery";

    public const string DevelopmentCookieName = "DbBackupManager.Development.Admin";
    public const string DevelopmentAntiforgeryCookieName = "DbBackupManager.Development.Antiforgery";

    public static readonly TimeSpan CookieLifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan RevalidationInterval = TimeSpan.FromMinutes(5);
}
