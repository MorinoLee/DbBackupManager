using DbBackupManager.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;

namespace DbBackupManager.Web.Tests.Components;

public sealed class AuthQueryTests
{
    [Fact]
    public void GetReadsLiveHttpContextQueryBeforeNavigationUri()
    {
        var navigation = new TestNavigationManager("https://localhost/login?error=from-navigation");
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?error=from-request");

        var value = AuthQuery.Get(navigation, httpContext, "error");

        Assert.Equal("from-request", value);
    }

    [Fact]
    public void GetFallsBackToNavigationUriWhenHttpContextIsNull()
    {
        var navigation = new TestNavigationManager(
            "https://localhost/login?error=invalid_credentials&returnUrl=/change-password");

        Assert.Equal("invalid_credentials", AuthQuery.Get(navigation, null, "error"));
        Assert.Equal("/change-password", AuthQuery.Get(navigation, null, "returnUrl"));
        Assert.Null(AuthQuery.Get(navigation, null, "status"));
    }

    [Fact]
    public void GetFallsBackToNavigationUriWhenHttpContextFeaturesAreDisposed()
    {
        var navigation = new TestNavigationManager("https://localhost/login?error=invalid_credentials");
        var httpContext = DisposedHttpContext.Create();

        var exception = Record.Exception(() => AuthQuery.Get(navigation, httpContext, "error"));

        Assert.Null(exception);
        Assert.Equal("invalid_credentials", AuthQuery.Get(navigation, httpContext, "error"));
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string uri)
        {
            Initialize("https://localhost/", uri);
        }
    }
}
