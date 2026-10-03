using System.Security.Claims;
using Bunit;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.Servers;
using DbBackupManager.Web.Authentication;
using Microsoft.Extensions.DependencyInjection;
using DatabasesPage = DbBackupManager.Web.Components.Pages.Databases;

namespace DbBackupManager.Web.Tests.Components;

public sealed class DatabasesTests : MudBlazorComponentTest
{
    private readonly FakeManagement _service = new();

    public DatabasesTests()
    {
        Services.AddSingleton<IServerManagementService>(_service);
    }

    [Fact]
    public void AnonymousPageDoesNotReadConfiguration()
    {
        var page = Render<DatabasesPage>();
        Assert.Equal(0, _service.ListCalls);
        Assert.Contains("请先登录", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyInventoryGuidesToServers()
    {
        Authenticate();
        _service.Inventory = new ServerInventory([], [], []);
        var page = Render<DatabasesPage>();

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='empty-go-servers']")));
        Assert.Equal("/servers", page.Find("[data-testid='empty-go-servers']").GetAttribute("href"));
    }

    [Fact]
    public void InstanceQueryParameterPreselectsFilter()
    {
        Authenticate();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"/databases?instance={FakeManagement.InstanceId}");

        var page = Render<DatabasesPage>();

        page.WaitForAssertion(() => Assert.Contains("synthetic-db", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("other-db", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void UnavailableDatabaseCannotBeNewlyManaged()
    {
        Authenticate();
        var page = Render<DatabasesPage>();

        page.WaitForAssertion(() => Assert.Contains("synthetic-db", page.Markup, StringComparison.Ordinal));
        var row = page.FindAll("[data-testid='toggle-managed']")
            .Single(button => button.TextContent.Contains("纳管", StringComparison.Ordinal)
                && button.Closest("tr")!.TextContent.Contains("other-db", StringComparison.Ordinal));
        Assert.True(row.HasAttribute("disabled"));
    }

    [Fact]
    public void ExplicitManagementToggleCallsUseCaseAndConfirms()
    {
        Authenticate();
        var page = Render<DatabasesPage>();

        page.WaitForAssertion(() => Assert.Contains("synthetic-db", page.Markup, StringComparison.Ordinal));
        var row = page.FindAll("[data-testid='toggle-managed']")
            .Single(button => button.Closest("tr")!.TextContent.Contains("synthetic-db", StringComparison.Ordinal));
        Assert.Equal("纳管", row.TextContent.Trim());
        row.Click();

        page.WaitForAssertion(() => Assert.Equal(1, _service.ManageCalls));
        page.WaitForAssertion(() => Assert.Contains("纳管设置已保存", page.Markup, StringComparison.Ordinal));
    }

    private void Authenticate()
    {
        SignIn("管理员");
        Authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("N")),
            new Claim(AdminAuthenticationDefaults.SecurityStampClaimType, "synthetic-stamp"));
    }

    private sealed class FakeManagement : IServerManagementService
    {
        public const string Version = "AAAAAAAAAAA=";
        public static readonly Guid ServerId = Guid.NewGuid();
        public static readonly Guid InstanceId = Guid.NewGuid();
        public static readonly Guid OtherInstanceId = Guid.NewGuid();
        private static readonly ServerItem Server = new(ServerId, new("synthetic-server", @"D:\Synthetic", 1, "synthetic-host", null, "synthetic-share", Guid.NewGuid(), null, null, true), Version);
        private static readonly InstanceItem Instance = new(InstanceId, new(ServerId, "synthetic-instance", "synthetic-host", Guid.NewGuid(), false, null, 15, true), Version, "Connected", null, null, null, null);
        private static readonly InstanceItem OtherInstance = new(OtherInstanceId, new(ServerId, "other-instance", "synthetic-host", Guid.NewGuid(), false, null, 15, true), Version, "Connected", null, null, null, null);
        private static readonly DatabaseItem Available = new(Guid.NewGuid(), InstanceId, "synthetic-db", false, true, false, "Online", "Full", DateTimeOffset.UtcNow, Version);
        private static readonly DatabaseItem Unavailable = new(Guid.NewGuid(), OtherInstanceId, "other-db", false, false, false, null, null, DateTimeOffset.UtcNow, Version);

        public ServerInventory Inventory { get; set; } = new([Server], [Instance, OtherInstance], [Available, Unavailable]);
        public int ListCalls { get; private set; }
        public int ManageCalls { get; private set; }

        public Task<ManagementResult<ServerInventory>> ListAsync(AdminSession actor, CancellationToken cancellationToken = default)
        { ListCalls++; return Task.FromResult(new ManagementResult<ServerInventory>(ManagementCode.Succeeded, Inventory)); }
        public Task<ManagementResult<ServerItem>> SaveServerAsync(AdminSession actor, Guid? id, string? version, ServerInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagementResult<InstanceItem>> SaveInstanceAsync(AdminSession actor, Guid? id, string? version, InstanceInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagementResult<InstanceItem>> ProbeAsync(AdminSession actor, Guid id, string version, bool discover, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagementResult<DatabaseItem>> SetManagedAsync(AdminSession actor, Guid id, string version, bool managed, CancellationToken cancellationToken = default)
        {
            ManageCalls++;
            Inventory = new ServerInventory(Inventory.Servers, Inventory.Instances,
                Inventory.Databases.Select(database => database.Id == id ? database with { IsManaged = managed } : database).ToArray());
            return Task.FromResult(new ManagementResult<DatabaseItem>(ManagementCode.Succeeded, Inventory.Databases.Single(database => database.Id == id)));
        }
    }
}
