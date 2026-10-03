using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using DbBackupManager.Application.Identity;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Web.Authentication;
using DbBackupManager.Web.BackupTasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbBackupManager.Web.Tests;

public sealed class AdminAuthenticationWebTests
{
    private const string Username = "Admin-1";
    private const string Password = "synthetic-password";

    [Fact]
    public async Task ApiSetupCookieAndCsrfFollowIdentityLifecycle()
    {
        await using var host = await AuthTestHost.CreateAsync();
        var client = host.Client;

        using var anonymousMe = await client.GetAsync("/api/v1/auth/me");
        var anonymousProblem = await ReadProblemCodeAsync(anonymousMe);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousMe.StatusCode);
        Assert.Null(anonymousMe.Headers.Location);
        Assert.Equal("authentication_required", anonymousProblem);

        var anonymousToken = await GetCsrfTokenAsync(client);
        using var setup = await PostJsonAsync(
            client,
            "/api/v1/auth/setup",
            new AdminSetupRequest(Username, Password),
            anonymousToken);
        var session = await setup.Content.ReadFromJsonAsync<AdminSessionResponse>();

        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.NotNull(session);
        Assert.Equal(Username, session.Username);
        var authCookie = Assert.Single(
            setup.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                $"{AdminAuthenticationDefaults.DevelopmentCookieName}=",
                StringComparison.Ordinal));
        Assert.Contains("secure", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", authCookie, StringComparison.OrdinalIgnoreCase);

        using var staleTokenLogout = await PostJsonAsync<object?>(
            client,
            "/api/v1/auth/logout",
            null,
            anonymousToken);
        Assert.Equal(HttpStatusCode.BadRequest, staleTokenLogout.StatusCode);
        Assert.Equal("invalid_csrf_token", await ReadProblemCodeAsync(staleTokenLogout));
        Assert.True(await host.HasAuditAsync("security.csrf"));

        var authenticatedToken = await GetCsrfTokenAsync(client);
        using var logout = await PostJsonAsync<object?>(
            client,
            "/api/v1/auth/logout",
            null,
            authenticatedToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var signedOutMe = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, signedOutMe.StatusCode);
    }

    [Fact]
    public async Task DevelopmentHttpSupportsCsrfAndAuthenticationCookies()
    {
        await using var host = await AuthTestHost.CreateAsync();
        using var client = host.CreateHttpClient();

        using var csrf = await client.GetAsync("/api/v1/auth/csrf");
        var csrfPayload = await csrf.Content.ReadFromJsonAsync<CsrfTokenResponse>();
        Assert.Equal(HttpStatusCode.OK, csrf.StatusCode);
        Assert.NotNull(csrfPayload);
        Assert.False(string.IsNullOrWhiteSpace(csrfPayload.Token));
        var antiforgeryCookie = Assert.Single(
            csrf.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                $"{AdminAuthenticationDefaults.DevelopmentAntiforgeryCookieName}=",
                StringComparison.Ordinal));
        Assert.DoesNotContain("secure", antiforgeryCookie, StringComparison.OrdinalIgnoreCase);
        Assert.False(antiforgeryCookie.StartsWith("__Host-", StringComparison.Ordinal));
        Assert.False(antiforgeryCookie.StartsWith("__Secure-", StringComparison.Ordinal));
        Assert.Contains("httponly", antiforgeryCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", antiforgeryCookie, StringComparison.OrdinalIgnoreCase);

        using var setup = await PostJsonAsync(
            client,
            "/api/v1/auth/setup",
            new AdminSetupRequest(Username, Password),
            csrfPayload.Token);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var authCookie = Assert.Single(
            setup.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                $"{AdminAuthenticationDefaults.DevelopmentCookieName}=",
                StringComparison.Ordinal));
        Assert.DoesNotContain("secure", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.False(authCookie.StartsWith("__Host-", StringComparison.Ordinal));
        Assert.False(authCookie.StartsWith("__Secure-", StringComparison.Ordinal));
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", authCookie, StringComparison.OrdinalIgnoreCase);

        using var current = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    [Fact]
    public async Task ProductionHttpSupportsCsrfAndAuthenticationWithoutHttpsRedirect()
    {
        await using var host = await AuthTestHost.CreateAsync(environment: "Production");
        using var client = host.CreateHttpClient();

        using var csrf = await client.GetAsync("/api/v1/auth/csrf");
        var csrfPayload = await csrf.Content.ReadFromJsonAsync<CsrfTokenResponse>();

        Assert.Equal(HttpStatusCode.OK, csrf.StatusCode);
        Assert.Null(csrf.Headers.Location);
        Assert.False(csrf.Headers.Contains("Strict-Transport-Security"));
        Assert.NotNull(csrfPayload);
        var antiforgeryCookie = Assert.Single(
            csrf.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                $"{AdminAuthenticationDefaults.AntiforgeryCookieName}=",
                StringComparison.Ordinal));
        Assert.DoesNotContain("secure", antiforgeryCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", antiforgeryCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", antiforgeryCookie, StringComparison.OrdinalIgnoreCase);

        using var setup = await PostJsonAsync(
            client,
            "/api/v1/auth/setup",
            new AdminSetupRequest(Username, Password),
            csrfPayload.Token);

        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var authCookie = Assert.Single(
            setup.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                $"{AdminAuthenticationDefaults.CookieName}=",
                StringComparison.Ordinal));
        Assert.DoesNotContain("secure", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", authCookie, StringComparison.OrdinalIgnoreCase);

        using var current = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    [Fact]
    public async Task DevelopmentHttpLoginFormCarriesCookieAndHiddenAntiforgeryToken()
    {
        await using var host = await AuthTestHost.CreateAsync();
        using var client = host.CreateHttpClient();
        await SetupAsync(client);
        using var logout = await PostJsonAsync<object?>(client, "/api/v1/auth/logout", null, await GetCsrfTokenAsync(client));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var page = await client.GetAsync("/login");
        var html = await page.Content.ReadAsStringAsync();
        var marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += marker.Length;
        var end = html.IndexOf('"', start);
        var token = WebUtility.HtmlDecode(html[start..end]);
        using var missingToken = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = Username,
            ["password"] = Password,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        using var login = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = Username,
            ["password"] = Password,
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location?.OriginalString);
        using var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task GetLogoutDoesNotChangeStateAndExternalReturnUrlIsRejected()
    {
        await using var host = await AuthTestHost.CreateAsync();
        var client = host.Client;
        await SetupAsync(client);

        using var getLogout = await client.GetAsync("/account/logout");
        Assert.Equal(HttpStatusCode.NotFound, getLogout.StatusCode);

        using var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        var logoutToken = await GetCsrfTokenAsync(client);
        using var logout = await PostJsonAsync<object?>(
            client,
            "/api/v1/auth/logout",
            null,
            logoutToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var loginToken = await GetCsrfTokenAsync(client);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = Username,
            ["password"] = Password,
            ["returnUrl"] = "https://example.invalid/redirect",
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/account/login")
        {
            Content = form,
        };
        request.Headers.Add(AdminAuthenticationDefaults.CsrfHeaderName, loginToken);
        using var login = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task FiveFailedLoginsLockAccountWithoutChangingFailureMessage()
    {
        await using var host = await AuthTestHost.CreateAsync();
        var client = host.Client;
        await SetupAsync(client);
        var logoutToken = await GetCsrfTokenAsync(client);
        using var logout = await PostJsonAsync<object?>(
            client,
            "/api/v1/auth/logout",
            null,
            logoutToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var token = await GetCsrfTokenAsync(client);

        for (var attempt = 0; attempt < AdminCredentialPolicy.LoginFailureThreshold; attempt++)
        {
            using var failure = await PostJsonAsync(
                client,
                "/api/v1/auth/login",
                new AdminLoginRequest(Username, "incorrect-password"),
                token);
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
            Assert.Equal("invalid_credentials", await ReadProblemCodeAsync(failure));
        }

        using var locked = await PostJsonAsync(
            client,
            "/api/v1/auth/login",
            new AdminLoginRequest(Username, Password),
            token);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal("invalid_credentials", await ReadProblemCodeAsync(locked));
    }

    [Fact]
    public async Task PasswordChangeInvalidatesSessionAndOldPassword()
    {
        await using var host = await AuthTestHost.CreateAsync();
        var client = host.Client;
        await SetupAsync(client);
        var token = await GetCsrfTokenAsync(client);
        using var change = await PostJsonAsync(
            client,
            "/api/v1/auth/change-password",
            new AdminChangePasswordRequest(Password, "replacement-password"),
            token);
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        using var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);

        var anonymousToken = await GetCsrfTokenAsync(client);
        using var oldPassword = await PostJsonAsync(
            client,
            "/api/v1/auth/login",
            new AdminLoginRequest(Username, Password),
            anonymousToken);
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);

        using var newPassword = await PostJsonAsync(
            client,
            "/api/v1/auth/login",
            new AdminLoginRequest(Username, "replacement-password"),
            anonymousToken);
        Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);
    }

    [Fact]
    public async Task AuthenticationEndpointReturnsStableRateLimitProblem()
    {
        await using var host = await AuthTestHost.CreateAsync();
        var client = host.Client;
        var token = await GetCsrfTokenAsync(client);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var response = await PostJsonAsync(
                client,
                "/api/v1/auth/login",
                new AdminLoginRequest("unknown", "synthetic-password"),
                token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var limited = await PostJsonAsync(
            client,
            "/api/v1/auth/login",
            new AdminLoginRequest("unknown", "synthetic-password"),
            token);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("60", Assert.Single(limited.Headers.GetValues("Retry-After")));
        Assert.Equal("rate_limit_exceeded", await ReadProblemCodeAsync(limited));
    }

    [Fact]
    public async Task PersistedCookieKeyRingSurvivesWebHostRestart()
    {
        await using var host = await AuthTestHost.CreateAsync();
        var token = await GetCsrfTokenAsync(host.Client);
        using var setup = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/setup",
            new AdminSetupRequest(Username, Password),
            token);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var setCookie = Assert.Single(
            setup.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                $"{AdminAuthenticationDefaults.DevelopmentCookieName}=",
                StringComparison.Ordinal));
        var cookie = setCookie.Split(';', 2)[0];

        await host.RestartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Add("Cookie", cookie);
        using var me = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task CircuitRevalidationFailsClosedWhenIdentityStoreFails()
    {
        var provider = new AdminRevalidatingAuthenticationStateProvider(
            NullLoggerFactory.Instance,
            new ThrowingIdentityService(),
            NullLogger<AdminRevalidatingAuthenticationStateProvider>.Instance);
        var principal = AdminPrincipalFactory.CreatePrincipal(new AdminSession(
            Guid.NewGuid(),
            Username,
            "synthetic-security-stamp"));

        var isValid = await provider.ValidatePrincipalAsync(principal);

        Assert.False(isValid);
        ((IDisposable)provider).Dispose();
    }

    [Fact]
    public async Task AnonymousAuthPagesExposeStaticFormsAndSafeErrorMapping()
    {
        await using var host = await AuthTestHost.CreateAsync();
        var client = host.Client;

        using var login = await client.GetAsync("/login?error=invalid_credentials&returnUrl=/change-password");
        var loginHtml = await login.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("data-testid=\"login-form\"", loginHtml, StringComparison.Ordinal);
        Assert.Contains("action=\"/account/login\"", loginHtml, StringComparison.Ordinal);
        Assert.Contains("method=\"post\"", loginHtml, StringComparison.Ordinal);
        Assert.Contains("name=\"__RequestVerificationToken\"", loginHtml, StringComparison.Ordinal);
        Assert.Contains("name=\"returnUrl\"", loginHtml, StringComparison.Ordinal);
        Assert.Contains("/change-password", loginHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-password", loginHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("data-testid=\"drawer-toggle\"", loginHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("data-testid=\"theme-toggle\"", loginHtml, StringComparison.Ordinal);

        using var setup = await client.GetAsync("/setup");
        var setupHtml = await setup.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.Contains("data-testid=\"setup-form\"", setupHtml, StringComparison.Ordinal);
        Assert.Contains("action=\"/account/setup\"", setupHtml, StringComparison.Ordinal);

        using var denied = await client.GetAsync("/access-denied");
        var deniedHtml = await denied.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        Assert.Contains("data-testid=\"access-denied-login\"", deniedHtml, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"access-denied-message\"", deniedHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetupPageHidesFormAfterAdministratorExistsAndHomeShowsUserMenu()
    {
        await using var host = await AuthTestHost.CreateAsync();
        var client = host.Client;
        await SetupAsync(client);

        using var setup = await client.GetAsync("/setup");
        var setupHtml = await setup.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.DoesNotContain("data-testid=\"setup-form\"", setupHtml, StringComparison.Ordinal);
        Assert.Contains("首次设置已完成", setupHtml, StringComparison.Ordinal);

        using var home = await client.GetAsync("/");
        var homeHtml = await home.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        // 退出登录表单位于按需展开的用户菜单内，初始 HTML 只呈现菜单按钮；
        // POST、action 与防伪令牌断言由 MainLayoutTests 的组件测试在展开菜单后覆盖。
        Assert.Contains("data-testid=\"user-menu-button\"", homeHtml, StringComparison.Ordinal);
        Assert.Contains("Admin-1", homeHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaticChangePasswordPageKeepsAccountActionsAndNavigationReachable()
    {
        await using var host = await AuthTestHost.CreateAsync();
        var client = host.Client;
        await SetupAsync(client);
        await SignInAsync(client);

        using var page = await client.GetAsync("/change-password");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        // 修改密码页是 Static SSR：账号操作必须是不依赖交互事件的纯链接与 POST 表单。
        Assert.Contains("data-testid=\"nav-link-change-password\"", html, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"logout-form\"", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/account/logout\"", html, StringComparison.Ordinal);
        Assert.Contains("method=\"post\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"__RequestVerificationToken\"", html, StringComparison.Ordinal);
        Assert.Contains("Admin-1", html, StringComparison.Ordinal);

        // 分组导航在静态页面上全部展开，主要业务链接直接可达。
        Assert.Contains("href=\"/servers\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/backup-tasks\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/sql-credentials\"", html, StringComparison.Ordinal);

        // 静态页面不出现依赖交互事件处理器的控件。
        Assert.DoesNotContain("data-testid=\"user-menu-button\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-testid=\"drawer-toggle\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-testid=\"theme-toggle\"", html, StringComparison.Ordinal);
    }

    private static async Task SetupAsync(HttpClient client)
    {
        var token = await GetCsrfTokenAsync(client);
        using var response = await PostJsonAsync(
            client,
            "/api/v1/auth/setup",
            new AdminSetupRequest(Username, Password),
            token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task SignInAsync(HttpClient client)
    {
        var token = await GetCsrfTokenAsync(client);
        using var response = await PostJsonAsync(
            client,
            "/api/v1/auth/login",
            new AdminLoginRequest(Username, Password),
            token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string> GetCsrfTokenAsync(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<CsrfTokenResponse>("/api/v1/auth/csrf");
        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response.Token));
        return response.Token;
    }

    private static async Task<HttpResponseMessage> PostJsonAsync<T>(
        HttpClient client,
        string path,
        T value,
        string csrfToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(value),
        };
        request.Headers.Add(AdminAuthenticationDefaults.CsrfHeaderName, csrfToken);
        return await client.SendAsync(request);
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private sealed class ThrowingIdentityService : IAdminIdentityService
    {
        public Task<bool> IsSetupRequiredAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AdminIdentityResult> SetupAsync(
            string? username,
            string? password,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AdminIdentityResult> AuthenticateAsync(
            string? username,
            string? password,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AdminIdentityResult> ValidateSessionAsync(
            Guid adminUserId,
            string? securityStamp,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("synthetic failure");

        public Task<AdminIdentityResult> ChangePasswordAsync(
            Guid adminUserId,
            string? securityStamp,
            string? currentPassword,
            string? newPassword,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RecordCsrfFailureAsync(
            Guid? actorAdminUserId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

internal sealed class AuthTestHost : IAsyncDisposable
{
    private const string DatabaseNamePrefix = "DbBackupManagerP4WebTests_";
    private const string KeyRingDirectoryPrefix = "DbBackupManagerP4CookieKeys_";
    private readonly string _databaseName = $"{DatabaseNamePrefix}{Guid.NewGuid():N}";
    private readonly string _keyRingPath = Path.Combine(
        Path.GetTempPath(),
        $"{KeyRingDirectoryPrefix}{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory;

    private readonly Action<IServiceCollection>? _configureServices;
    private readonly string _environment;

    private AuthTestHost(Action<IServiceCollection>? configureServices, string environment)
    {
        _configureServices = configureServices;
        _environment = environment;
        _factory = CreateFactory();
        Client = CreateClient();
    }

    public HttpClient Client { get; private set; }

    public HttpClient CreateHttpClient()
    {
        return _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://localhost"),
            HandleCookies = true,
        });
    }

    public IServiceScope CreateScope()
    {
        return _factory.Services.CreateScope();
    }

    public static async Task<AuthTestHost> CreateAsync(
        Action<IServiceCollection>? configureServices = null,
        string environment = "Development")
    {
        var host = new AuthTestHost(configureServices, environment);
        try
        {
            var contextFactory = host._factory.Services
                .GetRequiredService<IDbContextFactory<PlatformDbContext>>();
            await using var context = await contextFactory.CreateDbContextAsync();
            await context.Database.MigrateAsync();
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public async Task RestartAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        _factory = CreateFactory();
        Client = CreateClient();

        var contextFactory = _factory.Services
            .GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        Assert.True(await context.Database.CanConnectAsync());
    }

    public async Task<bool> HasAuditAsync(string action)
    {
        var contextFactory = _factory.Services
            .GetRequiredService<IDbContextFactory<PlatformDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.AuditRecords.AnyAsync(record => record.Action == action);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        await _factory.DisposeAsync();

        await LocalDbTestDatabase.DropAsync(_databaseName);

        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var resolvedKeyRingPath = Path.GetFullPath(_keyRingPath);
        if (!resolvedKeyRingPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolvedKeyRingPath).StartsWith(
                KeyRingDirectoryPrefix,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("拒绝删除不属于 P4 Web 测试的 Key Ring 目录。");
        }

        if (Directory.Exists(resolvedKeyRingPath))
        {
            Directory.Delete(resolvedKeyRingPath, recursive: true);
        }
    }

    private string CreateConnectionString()
    {
        return $"Server=(localdb)\\MSSQLLocalDB;Database={_databaseName};Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Pooling=false";
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(_environment);
            builder.ConfigureServices(services =>
            {
                // 认证与管理 API 测试不启动事件轮询；宿主注册和循环行为由专项测试覆盖。
                foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)
                    && (item.ImplementationType == typeof(TaskEventRelay)
                        || item.ImplementationType == typeof(TaskEventRetentionService)
                        || item.ImplementationType?.Name == "PlatformDatabaseMonitor")).ToArray())
                {
                    services.Remove(descriptor);
                }

                _configureServices?.Invoke(services);
            });
            builder.UseSetting("ConnectionStrings:PlatformDatabase", CreateConnectionString());
            builder.UseSetting("DataProtection:CookieKeyRingPath", _keyRingPath);
            builder.UseSetting("DataProtection:BusinessCredentialKeyRingPath", Path.Combine(_keyRingPath, "business"));
        });
    }

    private HttpClient CreateClient()
    {
        return _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });
    }
}
