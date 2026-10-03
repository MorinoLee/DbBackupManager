using System.Security.Claims;
using Bunit;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Application.Servers;
using DbBackupManager.Application.StorageTargets;
using DbBackupManager.Web.Authentication;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Page = DbBackupManager.Web.Components.Pages.ScheduledBackupPolicies;

namespace DbBackupManager.Web.Tests.Components;

public sealed class ScheduledBackupPoliciesTests : MudBlazorComponentTest
{
    private readonly FakeManagement _service = new();
    private readonly FakeTargets _targets = new();

    public ScheduledBackupPoliciesTests()
    {
        Services.AddSingleton<IScheduledBackupPolicyService>(_service);
        Services.AddSingleton<IStorageTargetManagementService>(_targets);
    }

    [Fact]
    public void AnonymousCannotReadScheduledPolicies()
    {
        var page = Render<Page>();
        Assert.Equal(0, _service.ListCalls);
        Assert.Empty(page.FindAll("[data-testid='new-scheduled-policy']"));
    }

    [Fact]
    public void EnabledPolicyShowsNextRunInLocalAndUtc()
    {
        Authenticate();
        var page = Render<Page>();
        page.WaitForAssertion(() => Assert.Contains("2026-09-19 02:00", page.Markup, StringComparison.Ordinal));
        Assert.Contains("2026-09-18 18:00 UTC", page.Markup, StringComparison.Ordinal);
        Assert.Contains("已启用", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledPolicyShowsDisabledLabel()
    {
        Authenticate();
        var page = Render<Page>();
        page.WaitForAssertion(() => Assert.Contains("已停用", page.Markup, StringComparison.Ordinal));
        Assert.Empty(page.FindAll("[data-testid='run-backup']"));
    }

    [Fact]
    public void WeeklyFieldsAppearAndConflictKeepsInput()
    {
        Authenticate();
        var provider = Render<MudDialogProvider>();
        var page = Render<Page>();
        page.Find("[data-testid='new-scheduled-policy']").Click();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("#scheduled-backup-name")));
        var editor = provider.FindComponent<DbBackupManager.Web.Components.Pages.ScheduledBackupPolicyEditDialog>();
        editor.Instance.Model.ScheduleType = 2;
        editor.Instance.Model.Monday = true;
        editor.Render();
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find("[data-testid='weekday-mon']")));
        provider.Find("#scheduled-backup-name").Change("冲突计划");
        _service.SaveCode = BackupManagementCode.Conflict;
        provider.Find("form").Submit();
        provider.WaitForAssertion(() => Assert.Equal("冲突计划", _service.Saved?.Name));
        provider.WaitForAssertion(() => Assert.Contains("已改变", provider.Markup, StringComparison.Ordinal));
        Assert.Equal("冲突计划", provider.Find("#scheduled-backup-name").GetAttribute("value"));
    }

    private void Authenticate()
    {
        SignIn("管理员");
        Authorization.SetClaims(
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("N")),
            new Claim(AdminAuthenticationDefaults.SecurityStampClaimType, "synthetic"));
    }

    private sealed class FakeManagement : IScheduledBackupPolicyService
    {
        public int ListCalls;
        public BackupManagementCode SaveCode = BackupManagementCode.Succeeded;
        public ScheduledBackupPolicyInput? Saved { get; private set; }
        private readonly Guid _databaseId = Guid.NewGuid();
        private readonly ScheduledBackupPolicy _enabled;
        private readonly ScheduledBackupPolicy _disabled;

        public FakeManagement()
        {
            _enabled = new(
                Guid.NewGuid(),
                new(_databaseId, "每晚备份", 1, new TimeOnly(2, 0), 0, "Taipei Standard Time", 1, null, 7, null, 120, 60, 60, false, true),
                "AAAAAAAAAAA=",
                DateTimeOffset.UnixEpoch,
                new DateTimeOffset(2026, 9, 18, 18, 0, 0, TimeSpan.Zero),
                new DateTime(2026, 9, 19, 2, 0, 0),
                "Taipei Standard Time");
            _disabled = new(
                Guid.NewGuid(),
                new(_databaseId, "停用计划", 1, new TimeOnly(3, 0), 0, "Taipei Standard Time", 1, null, 7, null, 120, 60, 60, false, false),
                "AAAAAAAAAAA=",
                null,
                null,
                null,
                null);
        }

        public Task<BackupManagementResult<ScheduledBackupDashboard>> ListAsync(AdminSession actor, CancellationToken token = default)
        {
            ListCalls++;
            return Task.FromResult(new BackupManagementResult<ScheduledBackupDashboard>(
                BackupManagementCode.Succeeded,
                new([new BackupDatabaseChoice(_databaseId, "演示库")], [_enabled, _disabled])));
        }

        public Task<BackupManagementResult<ScheduledBackupPolicy>> GetAsync(AdminSession actor, Guid id, CancellationToken token = default) =>
            throw new NotSupportedException();

        public Task<BackupManagementResult<ScheduledBackupPolicy>> SaveAsync(
            AdminSession actor,
            Guid? id,
            string? version,
            ScheduledBackupPolicyInput input,
            CancellationToken token = default)
        {
            Saved = input;
            return Task.FromResult(new BackupManagementResult<ScheduledBackupPolicy>(SaveCode, _enabled));
        }
    }

    private sealed class FakeTargets : IStorageTargetManagementService
    {
        public Task<ManagementResult<IReadOnlyList<StorageTargetItem>>> ListAsync(
            AdminSession actor, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ManagementResult<IReadOnlyList<StorageTargetItem>>(ManagementCode.Succeeded, []));

        public Task<ManagementResult<StorageTargetItem>> SaveAsync(
            AdminSession actor, Guid? id, string? version, StorageTargetInput input,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ManagementResult<StorageTargetItem>> SetEnabledAsync(
            AdminSession actor, Guid id, string version, bool isEnabled,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ManagementResult<StorageTargetItem>> TestConnectionAsync(
            AdminSession actor, Guid id, string version,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
