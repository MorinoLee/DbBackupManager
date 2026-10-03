using System.Net;
using System.Net.Http.Json;
using DbBackupManager.Contracts.Api.V1;

namespace DbBackupManager.Web.Tests;

public sealed class BackupMonitoringWebTests
{
    [Fact]
    public async Task MonitoringRequiresAuthenticationAndValidatesQueriesWithoutCaching()
    {
        await using var host = await AuthTestHost.CreateAsync();
        foreach (var path in new[] { "backup-overview", "backup-tasks", "backup-files" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/v1/" + path)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await host.Client.GetAsync("/backup-files")).StatusCode);
        var token = (await host.Client.GetFromJsonAsync<CsrfTokenResponse>("/api/v1/auth/csrf"))!.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/setup")
        { Content = JsonContent.Create(new AdminSetupRequest("admin", "synthetic-password")) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(request)).StatusCode);
        using var overview = await host.Client.GetAsync("/api/v1/backup-overview");
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        Assert.True(overview.Headers.CacheControl?.NoStore);
        Assert.Equal("Unknown", (await overview.Content.ReadFromJsonAsync<BackupOverviewResponse>())!.Worker.Status);
        var tasks = await host.Client.GetFromJsonAsync<BackupTaskPageResponse>("/api/v1/backup-tasks");
        Assert.Equal(0, tasks!.TotalCount); Assert.Equal(20, tasks.PageSize);
        Assert.Empty((await host.Client.GetFromJsonAsync<BackupFilePageResponse>("/api/v1/backup-files"))!.Items);
        foreach (var path in new[] { "backup-tasks?page=-1", "backup-tasks?status=Unknown", "backup-files?status=Failed", "backup-files?fromUtc=2026-09-12T00:00:00Z&untilUtc=2026-09-11T00:00:00Z" })
            Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/api/v1/" + path)).StatusCode);
    }
}
