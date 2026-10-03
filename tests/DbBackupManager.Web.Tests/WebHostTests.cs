using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Infrastructure.Persistence;
using DbBackupManager.Web.Authentication;
using DbBackupManager.Web.BackupTasks;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DbBackupManager.Web.Tests;

public sealed class WebHostTests : IClassFixture<DbBackupManagerWebApplicationFactory>
{
    private readonly DbBackupManagerWebApplicationFactory _factory;

    public WebHostTests(DbBackupManagerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AnonymousAccessDeniedPageContainsBlazorAndMudBlazorAssets()
    {
        using var client = _factory.CreateHttpsClient();

        var html = await client.GetStringAsync("/access-denied");

        Assert.Contains("<html lang=\"zh-CN\">", html, StringComparison.Ordinal);
        Assert.Contains("auth-shell", html, StringComparison.Ordinal);
        Assert.Contains("SQL Server 备份管理平台", html, StringComparison.Ordinal);
        Assert.DoesNotContain("drawer-toggle", html, StringComparison.Ordinal);
        Assert.Contains("_framework/blazor.web.js", html, StringComparison.Ordinal);
        Assert.Matches("href=\"DbBackupManager\\.Web\\.[^\"]+\\.styles\\.css\"", html);
        Assert.Contains("_content/MudBlazor/MudBlazor.min.css", html, StringComparison.Ordinal);
        Assert.Contains("_content/MudBlazor/MudBlazor.min.js", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnonymousRootRedirectsToLoginWithLocalReturnUrl()
    {
        using var client = _factory.CreateHttpsClient();

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login?returnUrl=%2F", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task InteractiveServerNegotiationEndpointIsAvailable()
    {
        using var client = _factory.CreateHttpsClient();

        using var response = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/_content/MudBlazor/MudBlazor.min.css")]
    [InlineData("/_content/MudBlazor/MudBlazor.min.js")]
    public async Task MudBlazorStaticAssetIsAvailable(string path)
    {
        using var client = _factory.CreateHttpsClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Content.Headers.ContentLength > 0);
    }

    [Fact]
    public async Task VersionedControllerReturnsTechnicalVersionInformation()
    {
        using var client = _factory.CreateHttpsClient();

        using var response = await client.GetAsync("/api/v1/system/version");
        var content = await response.Content.ReadFromJsonAsync<SystemVersionResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(content);
        Assert.Equal("DbBackupManager", content.Product);
        Assert.Equal("v1", content.ApiVersion);
        Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+$", content.ApplicationVersion);
    }

    [Fact]
    public async Task UnknownOriginDoesNotReceiveCorsPermission()
    {
        using var client = _factory.CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/version");
        request.Headers.Add("Origin", "https://client.example.invalid");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task LivenessEndpointDoesNotClaimExternalDependencies()
    {
        using var client = _factory.CreateHttpsClient();

        using var response = await client.GetAsync("/health/live");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", content);
    }

    [Fact]
    public async Task ReadinessReturnsSafeUncached503UntilPlatformIsAvailableWhileLivenessStays200()
    {
        var readiness = new PlatformReadinessStub(false);
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)
                && (item.ImplementationType?.Name == "PlatformDatabaseMonitor"
                    || item.ImplementationType?.Namespace == "DbBackupManager.Web.BackupTasks")).ToArray())
                services.Remove(descriptor);
            services.AddSingleton<IPlatformDatabaseReadiness>(readiness);
        }));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        using var unavailable = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("NotReady", await unavailable.Content.ReadAsStringAsync());
        Assert.True(unavailable.Headers.CacheControl?.NoStore);
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        readiness.IsReady = true;
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Ready", await ready.Content.ReadAsStringAsync());
        readiness.IsReady = false;
        using var lost = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, lost.StatusCode);
    }

    [Fact]
    public async Task DevelopmentOpenApiMatchesCommittedContract()
    {
        using var client = _factory.CreateHttpsClient();

        var actualJson = await client.GetStringAsync("/openapi/v1.json");
        var committedPath = Path.Combine(_factory.ContentRootPath, "OpenApi", "v1.json");
        var committedJson = await File.ReadAllTextAsync(committedPath);

        var actual = JsonNode.Parse(actualJson);
        var committed = JsonNode.Parse(committedJson);

        Assert.NotNull(actual);
        Assert.NotNull(committed);
        var paths = actual["paths"]?.AsObject();
        Assert.NotNull(paths);
        Assert.NotEmpty(paths);
        Assert.All(
            paths,
            path => Assert.StartsWith("/api/v1/", path.Key, StringComparison.Ordinal));
        actual.AsObject().Remove("servers");
        Assert.True(
            JsonNode.DeepEquals(committed, actual),
            "忽略部署地址后，运行时 OpenAPI 与已提交契约不同，请重新生成并审查 OpenApi/v1.json。");
    }

    [Fact]
    public async Task BuildTimeOpenApiMatchesCommittedContract()
    {
        var generatedPath = Path.Combine(_factory.ContentRootPath, "obj", "openapi", "v1.json");
        var committedPath = Path.Combine(_factory.ContentRootPath, "OpenApi", "v1.json");

        Assert.True(File.Exists(generatedPath), "缺少构建时 OpenAPI，请先构建 Web 项目。");

        var generated = JsonNode.Parse(await File.ReadAllTextAsync(generatedPath));
        var committed = JsonNode.Parse(await File.ReadAllTextAsync(committedPath));

        Assert.NotNull(generated);
        Assert.NotNull(committed);
        Assert.True(
            JsonNode.DeepEquals(committed, generated),
            "构建时 OpenAPI 与已提交契约不同，请重新生成并审查 OpenApi/v1.json。");
    }

    [Fact]
    public async Task ProductionDoesNotExposeRuntimeOpenApiEndpoint()
    {
        using var productionFactory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting(
                    "ConnectionStrings:PlatformDatabase",
                    "Server=localhost;Database=DbBackupManagerProductionHostOnly;Integrated Security=true;Encrypt=true");
            });
        using var client = CreateHttpsClient(productionFactory);

        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void ProductionHttpUsesDedicatedSameAsRequestCookies()
    {
        using var productionFactory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting(
                    "ConnectionStrings:PlatformDatabase",
                    "Server=localhost;Database=DbBackupManagerProductionHostOnly;Integrated Security=true;Encrypt=true");
            });

        var authentication = productionFactory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AdminAuthenticationDefaults.AuthenticationScheme);
        var antiforgery = productionFactory.Services
            .GetRequiredService<IOptions<AntiforgeryOptions>>()
            .Value;

        Assert.Equal(AdminAuthenticationDefaults.CookieName, authentication.Cookie.Name);
        Assert.Equal(AdminAuthenticationDefaults.AntiforgeryCookieName, antiforgery.Cookie.Name);
        Assert.Equal("/", authentication.Cookie.Path);
        Assert.Equal("/", antiforgery.Cookie.Path);
        Assert.Null(authentication.Cookie.Domain);
        Assert.Null(antiforgery.Cookie.Domain);
        Assert.NotNull(authentication.Cookie.Name);
        Assert.NotNull(antiforgery.Cookie.Name);
        Assert.False(authentication.Cookie.Name.StartsWith("__Host-", StringComparison.Ordinal));
        Assert.False(antiforgery.Cookie.Name.StartsWith("__Host-", StringComparison.Ordinal));
        Assert.Equal(CookieSecurePolicy.SameAsRequest, authentication.Cookie.SecurePolicy);
        Assert.Equal(CookieSecurePolicy.SameAsRequest, antiforgery.Cookie.SecurePolicy);
    }

    [Fact]
    public async Task WebHostRegistersPlatformDbContextFactoryWithoutOpeningDatabase()
    {
        var factory = _factory.Services.GetRequiredService<IDbContextFactory<PlatformDbContext>>();

        await using var context = await factory.CreateDbContextAsync();

        Assert.NotNull(context);
        Assert.False(context.Database.GetDbConnection().State == System.Data.ConnectionState.Open);
    }

    [Fact]
    public void WebHostRegistersTaskEventRelayOptionsAndPublisherAlias()
    {
        var notifier = _factory.Services.GetRequiredService<TaskRefreshNotifier>();
        var publisher = _factory.Services.GetRequiredService<ITaskRefreshPublisher>();
        var options = _factory.Services.GetRequiredService<TaskEventRelayOptions>();

        Assert.Same(notifier, publisher);
        Assert.Equal(TaskEventRelayOptions.Default, options);
        options.Validate();
        Assert.Contains(
            _factory.Services.GetServices<IHostedService>(),
            service => service is TaskEventRelay);
    }

    [Fact]
    public async Task WebHostScopesCircuitReconnectNotifierPerCircuit()
    {
        var options = _factory.Services.GetRequiredService<MonitoringRefreshOptions>();
        Assert.Same(MonitoringRefreshOptions.Default, options);
        Assert.Equal(TimeSpan.FromSeconds(30), options.CalibrationInterval);

        using var firstScope = _factory.Services.CreateScope();
        using var secondScope = _factory.Services.CreateScope();

        var firstNotifier = firstScope.ServiceProvider.GetRequiredService<CircuitReconnectNotifier>();
        var secondNotifier = secondScope.ServiceProvider.GetRequiredService<CircuitReconnectNotifier>();

        Assert.Same(
            firstNotifier,
            firstScope.ServiceProvider.GetRequiredService<CircuitReconnectNotifier>());
        Assert.NotSame(firstNotifier, secondNotifier);

        var firstHandler = Assert.Single(
            firstScope.ServiceProvider.GetServices<CircuitHandler>()
                .OfType<MonitoringCircuitHandler>());

        var firstCalls = 0;
        var secondCalls = 0;
        using var firstSubscription = firstNotifier.Subscribe(() => firstCalls++);
        using var secondSubscription = secondNotifier.Subscribe(() => secondCalls++);

        await firstHandler.OnConnectionDownAsync(null!, CancellationToken.None);
        await firstHandler.OnConnectionUpAsync(null!, CancellationToken.None);

        Assert.Equal(1, firstCalls);
        Assert.Equal(0, secondCalls);
    }

    private static HttpClient CreateHttpsClient(WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
    }
}

public sealed class DbBackupManagerWebApplicationFactory : WebApplicationFactory<Program>
{
    public string ContentRootPath => Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;

    public HttpClient CreateHttpsClient()
    {
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting(
            "ConnectionStrings:PlatformDatabase",
            "Server=localhost;Database=DbBackupManagerWebHostOnly;Integrated Security=true;Encrypt=true");
    }
}
