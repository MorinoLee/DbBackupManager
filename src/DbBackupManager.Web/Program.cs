using System.Threading.RateLimiting;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Infrastructure.BackupManagement;
using DbBackupManager.Infrastructure.FileCredentials;
using DbBackupManager.Infrastructure.FileStorage;
using DbBackupManager.Infrastructure.Hosting;
using DbBackupManager.Infrastructure.Notifications;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Infrastructure.Servers;
using DbBackupManager.Infrastructure.SqlCredentials;
using DbBackupManager.Infrastructure.StorageTargets;
using DbBackupManager.Infrastructure.TargetSql;
using DbBackupManager.Web.Authentication;
using DbBackupManager.Web.BackupTasks;
using DbBackupManager.Web.Components;
using DbBackupManager.Web.Hosting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);
ExternalJsonConfiguration.AddIfConfigured(builder.Configuration, AppContext.BaseDirectory);
var cookieSecurePolicy = CookieSecurePolicy.SameAsRequest;

builder.Services.AddWindowsService(options =>
    options.ServiceName = DbBackupManagerWebHostDefaults.WindowsServiceName);
builder.WebHost.ConfigureKestrel(WebConnectionShutdown.Configure);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMudServices();
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AuthorizeFilter(AdminAuthenticationDefaults.AuthorizationPolicy));
    options.Filters.Add<ApiAntiforgeryFilter>();
});
builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        if (context.ProblemDetails.Status >= StatusCodes.Status500InternalServerError)
        {
            context.ProblemDetails.Title = "服务器暂时无法完成请求。";
            context.ProblemDetails.Detail = null;
            context.ProblemDetails.Extensions["code"] = "unexpected_error";
        }
    };
});
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var problem = ApiProblemDetails.Create(
            context.HttpContext,
            StatusCodes.Status400BadRequest,
            "validation_failed",
            "请求格式或输入内容不符合要求。");
        problem.Extensions["errors"] = context.ModelState
            .Where(item => item.Value is { Errors.Count: > 0 })
            .ToDictionary(
                item => item.Key,
                _ => ApiProblemDetails.InvalidModelValue,
                StringComparer.Ordinal);
        return new BadRequestObjectResult(problem);
    };
});
builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer<AuthenticationOpenApiTransformer>();
    options.AddOperationTransformer<AuthenticationOpenApiTransformer>();
});
builder.Services.AddPlatformDatabase(builder.Configuration);
builder.Services.AddPlatformDatabaseReadiness();
builder.Services.AddTargetSqlAdapter(builder.Configuration);
builder.Services.AddSqlCredentialManagement();
builder.Services.AddFileCredentialManagement();
builder.Services.AddServerManagement();
builder.Services.AddStorageTargetManagement();
builder.Services.AddSmtpNotificationManagement();
builder.Services.AddBackupFileStorage();
builder.Services.AddBackupManagement();
builder.Services.AddSingleton(TaskEventRelayOptions.Default);
builder.Services.AddSingleton<TaskRefreshNotifier>();
builder.Services.AddSingleton<ITaskRefreshPublisher>(static services => services.GetRequiredService<TaskRefreshNotifier>());
builder.Services.AddHostedService<TaskEventRelay>();
builder.Services.AddSingleton(TaskEventRetentionOptions.Default);
builder.Services.AddHostedService<TaskEventRetentionService>();
builder.Services.AddScoped<CircuitReconnectNotifier>();
builder.Services.AddSingleton(MonitoringRefreshOptions.Default);
builder.Services.AddScoped<CircuitHandler, MonitoringCircuitHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<PasswordHasherOptions>(options =>
    options.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3);
builder.Services.AddSingleton<IAdminPasswordService, AspNetAdminPasswordService>();
builder.Services.AddScoped<IAdminIdentityService, AdminIdentityService>();
builder.Services.AddScoped<AdminCookieAuthenticationEvents>();
builder.Services.AddScoped<AuthenticationStateProvider, AdminRevalidatingAuthenticationStateProvider>();

var dataProtection = builder.Services
    .AddDataProtection()
    .SetApplicationName("DbBackupManager.Web.Cookie.v1");
var cookieKeyRingPath = builder.Configuration["DataProtection:CookieKeyRingPath"];
if (!string.IsNullOrWhiteSpace(cookieKeyRingPath))
{
    if (!Path.IsPathFullyQualified(cookieKeyRingPath))
    {
        throw new InvalidOperationException("DataProtection:CookieKeyRingPath 必须是绝对路径。");
    }

    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(cookieKeyRingPath));
}

builder.Services
    .AddAuthentication(AdminAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(AdminAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = builder.Environment.IsDevelopment()
            ? AdminAuthenticationDefaults.DevelopmentCookieName
            : AdminAuthenticationDefaults.CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = cookieSecurePolicy;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = AdminAuthenticationDefaults.CookieLifetime;
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
        options.EventsType = typeof(AdminCookieAuthenticationEvents);
    });
builder.Services.AddAuthorization(options =>
    options.AddPolicy(
        AdminAuthenticationDefaults.AuthorizationPolicy,
        policy => policy.RequireAuthenticatedUser()));
builder.Services.AddAntiforgery(options =>
    {
        options.HeaderName = AdminAuthenticationDefaults.CsrfHeaderName;
        options.Cookie.Name = builder.Environment.IsDevelopment()
            ? AdminAuthenticationDefaults.DevelopmentAntiforgeryCookieName
            : AdminAuthenticationDefaults.AntiforgeryCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = cookieSecurePolicy;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
    });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(
        AdminAuthenticationDefaults.AuthRateLimitPolicy,
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 10,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1),
            }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        var requestPath = context.HttpContext.Request.Path;
        if (requestPath.StartsWithSegments("/account", StringComparison.OrdinalIgnoreCase))
        {
            var redirectPath = requestPath.Value switch
            {
                "/account/setup" => "/setup",
                "/account/change-password" => "/change-password",
                _ => "/login",
            };
            context.HttpContext.Response.Redirect(QueryHelpers.AddQueryString(
                redirectPath,
                "error",
                "rate_limit_exceeded"));
            return;
        }

        var problem = ApiProblemDetails.Create(
            context.HttpContext,
            StatusCodes.Status429TooManyRequests,
            "rate_limit_exceeded",
            "请求过于频繁，请稍后重试。");
        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            problem,
            cancellationToken);
    };
});

var app = builder.Build();

app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
});
app.MapGet("/health/ready", (HttpContext context, IPlatformDatabaseReadiness readiness) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        var ready = readiness.IsReady;
        return Results.Text(ready ? "Ready" : "NotReady", statusCode:
            ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    })
    .AllowAnonymous()
    .ExcludeFromDescription();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/{documentName}.json");
}

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
