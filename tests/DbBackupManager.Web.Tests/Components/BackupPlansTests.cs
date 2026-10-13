using System.Security.Claims;
using Bunit;
using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.Servers;
using DbBackupManager.Application.StorageTargets;
using DbBackupManager.Domain.BackupPlans;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Web.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Page = DbBackupManager.Web.Components.Pages.BackupPlans;

namespace DbBackupManager.Web.Tests.Components;

public sealed class BackupPlansTests : MudBlazorComponentTest
{
    private readonly FakeCatalog _catalog = new();
    private readonly FakeServers _servers = new();
    private readonly FakeTargets _targets = new();

    public BackupPlansTests()
    {
        Services.AddSingleton<IBackupPlanCatalogService>(_catalog);
        Services.AddSingleton<IServerManagementService>(_servers);
        Services.AddSingleton<IStorageTargetManagementService>(_targets);
    }

    [Fact]
    public void AnonymousDoesNotReadPlans()
    {
        var page = Render<Page>();
        Assert.Equal(0, _catalog.ListCalls);
        Assert.Equal(0, _servers.ListCalls);
        Assert.Empty(page.FindAll("[data-testid='new-backup-plan']"));
    }

    [Fact]
    public void CreateDialogDefaultsChecksumAndDoesNotOfferLog()
    {
        Authenticate();
        var provider = Render<MudDialogProvider>();
        var page = Render<Page>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='new-backup-plan']")));
        Assert.DoesNotContain("建议开启", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("系统库合成", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("未纳管库", page.Markup, StringComparison.Ordinal);

        page.Find("[data-testid='new-backup-plan']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#backup-plan-name")));
        var editor = provider.FindComponent<DbBackupManager.Web.Components.Pages.BackupPlanEditDialog>();
        Assert.True(editor.Instance.Model.UseChecksum);
        Assert.DoesNotContain("日志", provider.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("建议开启", provider.Markup, StringComparison.Ordinal);
        var checksum = provider.Find("[data-testid='plan-checksum']");
        Assert.Equal("checkbox", checksum.GetAttribute("type"));
        Assert.True(checksum.HasAttribute("checked"));

        provider.Find("#backup-plan-name").Change("合成计划");
        provider.Find("form").Submit();

        page.WaitForAssertion(() => Assert.NotNull(_catalog.Created));
        Assert.Equal("合成计划", _catalog.Created!.Name);
        Assert.True(_catalog.Created.UseChecksum);
        Assert.Equal(1, _catalog.Created.Mode);
        Assert.Null(_catalog.Created.DifferentialSchedule);
        Assert.Null(_catalog.Created.StorageTargetId);
        Assert.Equal(7, _catalog.Created.LocalRecoveryWindowDays);
        Assert.Null(_catalog.Created.RemoteRecoveryWindowDays);
        Assert.Contains("自动调度和定时删除尚未启用", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionConflictKeepsTheDialogAndChineseMessage()
    {
        Authenticate();
        _catalog.Plans =
        [
            new(FakeCatalog.PlanId, FakeCatalog.DatabaseId, "原计划", false, FakeCatalog.Version, 2, 1, 1, true),
        ];
        var provider = Render<MudDialogProvider>();
        var page = Render<Page>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='edit-plan']")));

        page.Find("[data-testid='edit-plan']").Click();
        provider.WaitForAssertion(() => Assert.Equal("原计划", provider.Find("#backup-plan-name").GetAttribute("value")));
        provider.Find("#backup-plan-name").Change("冲突计划");
        _catalog.UpdateCode = BackupPlanCatalogCode.VersionConflict;
        provider.Find("form").Submit();
        provider.WaitForAssertion(() => Assert.Contains("备份计划已被其他人修改，请刷新后重试。", provider.Markup, StringComparison.Ordinal));
        Assert.Equal("冲突计划", provider.Find("#backup-plan-name").GetAttribute("value"));
        Assert.Equal(FakeCatalog.Version, _catalog.UpdatedVersion);
    }

    [Fact]
    public void ExistingPlanConflictKeepsTheDialogAndChineseMessage()
    {
        Authenticate();
        _catalog.CreateCode = BackupPlanCatalogCode.AlreadyExists;
        var provider = Render<MudDialogProvider>();
        var page = Render<Page>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='new-backup-plan']")));
        page.Find("[data-testid='new-backup-plan']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#backup-plan-name")));
        provider.Find("#backup-plan-name").Change("重复计划");
        provider.Find("form").Submit();
        provider.WaitForAssertion(() => Assert.Contains("该数据库已有当前备份计划（含已暂停）。请改用现有计划。", provider.Markup, StringComparison.Ordinal));
        Assert.Equal("重复计划", provider.Find("#backup-plan-name").GetAttribute("value"));
    }

    [Fact]
    public void MissingPlanShowsEmptyRecoveryInsteadOfAHealthyWindow()
    {
        Authenticate();
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/backup-plans?database={FakeCatalog.DatabaseId}");
        var page = Render<Page>();

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid='plan-empty']")));
        Assert.Contains("还没有当前备份计划", page.Find("[data-testid='plan-empty']").TextContent, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-testid='recovery-health']"));
        Assert.DoesNotContain("可恢复", page.Find("[data-testid='recovery-panel']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownLocationIsUnhealthyEvenWhenTheWindowLooksSatisfied()
    {
        Authenticate();
        _catalog.Recovery = new BackupRecoverySummary(FakeCatalog.DatabaseId, true,
        [
            new BackupLocationRecovery(
                BackupFileLocation.Local, null, 7, LocationRecoveryConclusion.RestorableWindowSatisfied,
                "Known", Guid.NewGuid(), true, true, ["remote_base_unconfirmed"], ["window_unknown"]),
        ]);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/backup-plans?database={FakeCatalog.DatabaseId}");
        var page = Render<Page>();

        page.WaitForAssertion(() => Assert.Contains("不健康", page.Find("[data-testid='recovery-health']").TextContent, StringComparison.Ordinal));
        var panel = page.Find("[data-testid='recovery-panel']");
        Assert.DoesNotContain("可恢复", panel.TextContent, StringComparison.Ordinal);
        Assert.Contains("远端暂时无法恢复", page.Find("[data-testid='recovery-remote-gap']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteGapStaysVisibleWhenTheLocationIsOtherwiseKnown()
    {
        Authenticate();
        _catalog.Recovery = new BackupRecoverySummary(FakeCatalog.DatabaseId, true,
        [
            new BackupLocationRecovery(
                BackupFileLocation.Remote, Guid.NewGuid(), 30, LocationRecoveryConclusion.NotRestorable,
                "None", null, false, true, ["remote_base_unconfirmed"], []),
        ]);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/backup-plans?database={FakeCatalog.DatabaseId}");
        var page = Render<Page>();

        page.WaitForAssertion(() => Assert.Contains("远端暂时无法恢复", page.Find("[data-testid='recovery-remote-gap']").TextContent, StringComparison.Ordinal));
        Assert.Contains("不可恢复", page.Find("[data-testid='recovery-health']").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("不健康", page.Find("[data-testid='recovery-health']").TextContent, StringComparison.Ordinal);
    }

    private void Authenticate()
    {
        SignIn("管理员");
        Authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("N")),
            new Claim(AdminAuthenticationDefaults.SecurityStampClaimType, "synthetic"));
    }

    private sealed class FakeCatalog : IBackupPlanCatalogService
    {
        public const string Version = "AAAAAAAAAAA=";
        public static readonly Guid DatabaseId = Guid.Parse("10000000-0000-4000-8000-000000000001");
        public static readonly Guid PlanId = Guid.Parse("20000000-0000-4000-8000-000000000002");
        public int ListCalls;
        public int RecoveryCalls;
        public BackupPlanCatalogCode CreateCode = BackupPlanCatalogCode.Succeeded;
        public BackupPlanCatalogCode UpdateCode = BackupPlanCatalogCode.Succeeded;
        public BackupPlanInput? Created { get; private set; }
        public string? UpdatedVersion { get; private set; }
        public List<BackupPlanListItem> Plans { get; set; } = [];
        public BackupRecoverySummary Recovery { get; set; } = new(DatabaseId, false, []);

        public Task<BackupPlanCatalogResult<IReadOnlyList<BackupPlanListItem>>> ListAsync(AdminSession actor, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(new BackupPlanCatalogResult<IReadOnlyList<BackupPlanListItem>>(BackupPlanCatalogCode.Succeeded, Plans));
        }

        public Task<BackupPlanCatalogResult<BackupPlanView>> GetByDatabaseAsync(AdminSession actor, Guid databaseId, CancellationToken cancellationToken = default)
        {
            var plan = Plans.Single(item => item.DatabaseId == databaseId);
            return Task.FromResult(new BackupPlanCatalogResult<BackupPlanView>(BackupPlanCatalogCode.Succeeded, View(plan)));
        }

        public Task<BackupPlanCatalogResult<BackupPlanView>> GetAsync(AdminSession actor, Guid planId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BackupPlanCatalogResult<BackupPlanView>> CreateAsync(AdminSession actor, BackupPlanInput input, CancellationToken cancellationToken = default)
        {
            Created = input;
            return Task.FromResult(new BackupPlanCatalogResult<BackupPlanView>(CreateCode, CreateCode == BackupPlanCatalogCode.Succeeded ? View(Plans.FirstOrDefault() ?? new(PlanId, input.DatabaseId, input.Name, input.IsPaused, Version, 1, input.Mode, input.StorageMode, input.UseChecksum ?? true)) : null));
        }

        public Task<BackupPlanCatalogResult<BackupPlanView>> UpdateAsync(AdminSession actor, Guid planId, string? version, BackupPlanInput input, CancellationToken cancellationToken = default)
        {
            UpdatedVersion = version;
            Created = input;
            var current = Plans.Single(item => item.Id == planId);
            return Task.FromResult(new BackupPlanCatalogResult<BackupPlanView>(UpdateCode, UpdateCode == BackupPlanCatalogCode.Succeeded ? View(current) : null));
        }

        public Task<BackupPlanCatalogResult<BackupRecoverySummary>> RecoveryAsync(AdminSession actor, Guid databaseId, CancellationToken cancellationToken = default)
        {
            RecoveryCalls++;
            return Task.FromResult(new BackupPlanCatalogResult<BackupRecoverySummary>(BackupPlanCatalogCode.Succeeded, Recovery with { DatabaseId = databaseId }));
        }

        private static BackupPlanView View(BackupPlanListItem plan) => new(
            plan.Id, plan.DatabaseId, plan.Name, plan.IsPaused, plan.Version, Guid.NewGuid(), plan.VersionNumber,
            (BackupPlanMode)plan.Mode,
            new RecurringBackupSchedule(BackupScheduleType.Daily, new TimeOnly(2, 0), BackupWeekdays.None),
            plan.Mode == 2 ? new RecurringBackupSchedule(BackupScheduleType.Daily, new TimeOnly(3, 0), BackupWeekdays.None) : null,
            "UTC", (BackupStorageMode)plan.StorageMode, null,
            plan.StorageMode == 3 ? null : 7, plan.StorageMode == 1 ? null : 30,
            plan.UseChecksum, false, 120, 60, 60, DateTimeOffset.UtcNow, []);
    }

    private sealed class FakeServers : IServerManagementService
    {
        private static readonly Guid ServerId = Guid.NewGuid();
        private static readonly Guid InstanceId = Guid.NewGuid();
        public int ListCalls { get; private set; }

        public Task<ManagementResult<ServerInventory>> ListAsync(AdminSession actor, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            var server = new ServerItem(ServerId, new("synthetic-server", @"D:\Synthetic", 1, "synthetic-host", null, "synthetic-share", Guid.NewGuid(), null, null, true), "v");
            var instance = new InstanceItem(InstanceId, new(ServerId, "synthetic-instance", "synthetic-host", Guid.NewGuid(), false, null, 15, true), "v", "Connected", null, null, null, null);
            DatabaseItem Database(Guid id, string name, bool system, bool managed) =>
                new(id, InstanceId, name, system, true, managed, "Online", "Full", DateTimeOffset.UtcNow, "v");
            var inventory = new ServerInventory([server], [instance],
            [
                Database(FakeCatalog.DatabaseId, "合成库", false, true),
                Database(Guid.NewGuid(), "系统库合成", true, true),
                Database(Guid.NewGuid(), "未纳管库", false, false),
            ]);
            return Task.FromResult(new ManagementResult<ServerInventory>(ManagementCode.Succeeded, inventory));
        }

        public Task<ManagementResult<ServerItem>> SaveServerAsync(AdminSession actor, Guid? id, string? version, ServerInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagementResult<InstanceItem>> SaveInstanceAsync(AdminSession actor, Guid? id, string? version, InstanceInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagementResult<InstanceItem>> ProbeAsync(AdminSession actor, Guid id, string version, bool discover, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagementResult<DatabaseItem>> SetManagedAsync(AdminSession actor, Guid id, string version, bool managed, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeTargets : IStorageTargetManagementService
    {
        public Task<ManagementResult<IReadOnlyList<StorageTargetItem>>> ListAsync(AdminSession actor, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ManagementResult<IReadOnlyList<StorageTargetItem>>(ManagementCode.Succeeded, []));

        public Task<ManagementResult<StorageTargetItem>> SaveAsync(AdminSession actor, Guid? id, string? version, StorageTargetInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagementResult<StorageTargetItem>> SetEnabledAsync(AdminSession actor, Guid id, string version, bool isEnabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagementResult<StorageTargetItem>> TestConnectionAsync(AdminSession actor, Guid id, string version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
