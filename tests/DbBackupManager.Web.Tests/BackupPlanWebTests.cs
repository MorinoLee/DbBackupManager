using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Web.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DbBackupManager.Web.Tests;

public sealed class BackupPlanWebTests
{
    [Fact]
    public async Task CurrentPlanIsUniqueAndDoesNotSuggestEnablingLog()
    {
        await using var host = await AuthTestHost.CreateAsync(services =>
        {
            services.RemoveAll<ITargetSqlReadOnlyProbe>();
            services.AddSingleton<ITargetSqlReadOnlyProbe, FakeProbe>();
        });
        var client = host.Client;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/backup-plans/" + Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/databases/" + Guid.NewGuid() + "/backup-recovery")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/backup-files")).StatusCode);
        var token = (await client.GetFromJsonAsync<CsrfTokenResponse>("/api/v1/auth/csrf"))!.Token;
        using var setup = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/setup", new AdminSetupRequest("admin", "synthetic-password"), token);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        token = (await client.GetFromJsonAsync<CsrfTokenResponse>("/api/v1/auth/csrf"))!.Token;
        var databaseId = await PrepareDatabaseAsync(client, token);

        using var beforePlan = await client.GetAsync($"/api/v1/databases/{databaseId}/backup-recovery");
        Assert.Equal(HttpStatusCode.OK, beforePlan.StatusCode);
        var withoutPlan = (await beforePlan.Content.ReadFromJsonAsync<BackupRecoverySummaryResponse>())!;
        Assert.False(withoutPlan.HasCurrentPlan);
        Assert.Empty(withoutPlan.Locations);

        var full = Plan(databaseId, "合成完整计划", mode: 1, paused: false);
        using var missingCsrf = await client.PostAsJsonAsync("/api/v1/backup-plans", full);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Equal("invalid_csrf_token", await ReadCodeAsync(missingCsrf));
        using var created = await SendAsync(client, HttpMethod.Post, "/api/v1/backup-plans", full, token);
        var createdJson = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        AssertNoLogSuggestion(createdJson);
        var plan = JsonSerializer.Deserialize<BackupPlanResponse>(createdJson, JsonOptions())!;
        Assert.Equal(1, plan.Mode);
        Assert.Equal(1, plan.VersionNumber);
        Assert.True(plan.UseChecksum);
        Assert.Empty(plan.Notices);
        Assert.Null(plan.DifferentialSchedule);

        var current = (await client.GetFromJsonAsync<BackupPlanResponse>($"/api/v1/databases/{databaseId}/backup-plan"))!;
        Assert.Equal(plan.Id, current.Id);
        Assert.Equal(plan.Version, current.Version);

        var missingVersion = Plan(databaseId, "缺少版本", mode: 1, paused: true);
        using var invalidVersion = await SendAsync(client, HttpMethod.Put, $"/api/v1/backup-plans/{plan.Id}", missingVersion, token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidVersion.StatusCode);
        Assert.Equal("backup_plan_invalid", await ReadCodeAsync(invalidVersion));
        Assert.Equal(1, (await client.GetFromJsonAsync<BackupPlanResponse>($"/api/v1/backup-plans/{plan.Id}"))!.VersionNumber);

        var pauseRequest = Plan(databaseId, "暂停后仍占名额", mode: 1, paused: true, version: plan.Version);
        using var putWithoutCsrf = await client.PutAsJsonAsync($"/api/v1/backup-plans/{plan.Id}", pauseRequest);
        Assert.Equal(HttpStatusCode.BadRequest, putWithoutCsrf.StatusCode);
        Assert.Equal("invalid_csrf_token", await ReadCodeAsync(putWithoutCsrf));

        using var paused = await SendAsync(client, HttpMethod.Put, $"/api/v1/backup-plans/{plan.Id}", pauseRequest, token);
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        var pausedPlan = (await paused.Content.ReadFromJsonAsync<BackupPlanResponse>())!;
        Assert.True(pausedPlan.IsPaused);
        Assert.Equal(1, pausedPlan.VersionNumber);
        Assert.NotEqual(plan.Version, pausedPlan.Version);

        var second = Plan(databaseId, "第二个当前计划", mode: 1, paused: true);
        using var rejected = await SendAsync(client, HttpMethod.Post, "/api/v1/backup-plans", second, token);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        using var rejectedJson = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal("backup_plan_exists", rejectedJson.RootElement.GetProperty("code").GetString());
        Assert.Equal(plan.Id, rejectedJson.RootElement.GetProperty("planId").GetGuid());

        var stale = Plan(databaseId, "过期版本", mode: 1, paused: true, version: plan.Version);
        using var conflict = await SendAsync(client, HttpMethod.Put, $"/api/v1/backup-plans/{plan.Id}", stale, token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var conflictJson = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Equal("backup_plan_version_conflict", conflictJson.RootElement.GetProperty("code").GetString());

        var differentialSchedule = new BackupPlanScheduleRequest { ScheduleType = 1, LocalTime = new TimeOnly(2, 30) };
        var revised = Plan(databaseId, "完整加差异", mode: 2, paused: true, version: pausedPlan.Version, differential: differentialSchedule);
        using var updated = await SendAsync(client, HttpMethod.Put, $"/api/v1/backup-plans/{plan.Id}", revised, token);
        var updatedJson = await updated.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        AssertNoLogSuggestion(updatedJson);
        var differential = JsonSerializer.Deserialize<BackupPlanResponse>(updatedJson, JsonOptions())!;
        Assert.Equal(2, differential.Mode);
        Assert.Equal(2, differential.VersionNumber);
        Assert.NotNull(differential.DifferentialSchedule);
        Assert.Empty(differential.Notices);

        var log = Plan(databaseId, "日志计划", mode: 3, paused: true, version: differential.Version, differential: differentialSchedule);
        using var logRejected = await SendAsync(client, HttpMethod.Put, $"/api/v1/backup-plans/{plan.Id}", log, token);
        var logJson = await logRejected.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, logRejected.StatusCode);
        using var logProblem = JsonDocument.Parse(logJson);
        Assert.Equal("backup_plan_log_unsupported", logProblem.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("建议", logJson, StringComparison.Ordinal);
        Assert.Equal(2, (await client.GetFromJsonAsync<BackupPlanResponse>($"/api/v1/backup-plans/{plan.Id}"))!.VersionNumber);

        using var recoveryResponse = await client.GetAsync($"/api/v1/databases/{databaseId}/backup-recovery");
        var recoveryJson = await recoveryResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, recoveryResponse.StatusCode);
        AssertNoLogSuggestion(recoveryJson);
        var recovery = JsonSerializer.Deserialize<BackupRecoverySummaryResponse>(recoveryJson, JsonOptions())!;
        Assert.True(recovery.HasCurrentPlan);
        var local = Assert.Single(recovery.Locations, item => item.Location == "Local");
        Assert.Equal("Unknown", local.Baseline);
        Assert.True(local.Unknown);
        Assert.False(local.RemoteUnrecoverable);
        Assert.Equal("NotRestorable", local.Conclusion);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/manual-backup-policies")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/backup-policies")).StatusCode);
    }

    [Fact]
    public async Task ManualRunRequiresCsrfAndRejectsDifferentialWithoutABaseline()
    {
        await using var host = await AuthTestHost.CreateAsync(services =>
        {
            services.RemoveAll<ITargetSqlReadOnlyProbe>();
            services.AddSingleton<ITargetSqlReadOnlyProbe, FakeProbe>();
        });
        var client = host.Client;
        var token = (await client.GetFromJsonAsync<CsrfTokenResponse>("/api/v1/auth/csrf"))!.Token;
        using var setup = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/setup", new AdminSetupRequest("admin", "synthetic-password"), token);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        token = (await client.GetFromJsonAsync<CsrfTokenResponse>("/api/v1/auth/csrf"))!.Token;
        var databaseId = await PrepareDatabaseAsync(client, token);
        var paused = Plan(databaseId, "暂停后仍可手动完整备份", mode: 1, paused: true);
        using var created = await SendAsync(client, HttpMethod.Post, "/api/v1/backup-plans", paused, token);
        var plan = (await created.Content.ReadFromJsonAsync<BackupPlanResponse>())!;
        var fullRequest = new RunBackupPlanRequest { TaskId = Guid.NewGuid(), MutationId = Guid.NewGuid(), Purpose = "PlanFull" };

        using var missingCsrf = await client.PostAsJsonAsync($"/api/v1/backup-plans/{plan.Id}/runs", fullRequest);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Equal("invalid_csrf_token", await ReadCodeAsync(missingCsrf));

        using var full = await SendAsync(client, HttpMethod.Post, $"/api/v1/backup-plans/{plan.Id}/runs", fullRequest, token);
        var fullBody = (await full.Content.ReadFromJsonAsync<RunBackupPlanResponse>())!;
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("PlanFull", fullBody.Purpose);
        Assert.False(fullBody.UseCopyOnly);
        Assert.Equal(plan.CurrentVersionId, fullBody.PlanVersionId);

        using var replay = await SendAsync(client, HttpMethod.Post, $"/api/v1/backup-plans/{plan.Id}/runs", fullRequest, token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(fullBody.TaskId, (await replay.Content.ReadFromJsonAsync<RunBackupPlanResponse>())!.TaskId);

        var copyRequest = new RunBackupPlanRequest { TaskId = Guid.NewGuid(), MutationId = Guid.NewGuid(), Purpose = "AdHocCopyOnlyFull" };
        using var copy = await SendAsync(client, HttpMethod.Post, $"/api/v1/backup-plans/{plan.Id}/runs", copyRequest, token);
        var copyBody = (await copy.Content.ReadFromJsonAsync<RunBackupPlanResponse>())!;
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);
        Assert.True(copyBody.UseCopyOnly);

        var logRequest = new RunBackupPlanRequest { TaskId = Guid.NewGuid(), MutationId = Guid.NewGuid(), Purpose = "PlanLog" };
        using var log = await SendAsync(client, HttpMethod.Post, $"/api/v1/backup-plans/{plan.Id}/runs", logRequest, token);
        Assert.Equal(HttpStatusCode.BadRequest, log.StatusCode);
        Assert.Equal("backup_plan_stage_not_open", await ReadCodeAsync(log));

        var differential = Plan(databaseId, "完整加差异", mode: 2, paused: false, version: plan.Version, differential: new BackupPlanScheduleRequest { ScheduleType = 1, LocalTime = new TimeOnly(2, 30) });
        using var updated = await SendAsync(client, HttpMethod.Put, $"/api/v1/backup-plans/{plan.Id}", differential, token);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var diffRequest = new RunBackupPlanRequest { TaskId = Guid.NewGuid(), MutationId = Guid.NewGuid(), Purpose = "PlanDifferential" };
        using var rejected = await SendAsync(client, HttpMethod.Post, $"/api/v1/backup-plans/{plan.Id}/runs", diffRequest, token);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("backup_plan_baseline_required", await ReadCodeAsync(rejected));

        using var recovery = await client.GetAsync($"/api/v1/databases/{databaseId}/backup-recovery");
        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
    }

    private static SaveBackupPlanRequest Plan(
        Guid databaseId, string name, int mode, bool paused, string? version = null, BackupPlanScheduleRequest? differential = null) => new()
        {
            Version = version,
            DatabaseId = databaseId,
            Name = name,
            Mode = mode,
            FullSchedule = new BackupPlanScheduleRequest { ScheduleType = 2, LocalTime = new TimeOnly(1, 15), DaysOfWeek = 1 },
            DifferentialSchedule = differential,
            TimeZoneId = "UTC",
            StorageMode = 1,
            LocalRecoveryWindowDays = 7,
            UseCompression = false,
            BackupTimeoutMinutes = 60,
            VerifyTimeoutMinutes = 30,
            TransferTimeoutMinutes = 45,
            IsPaused = paused,
        };

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("code").GetString();
    }

    private static void AssertNoLogSuggestion(string json)
    {
        Assert.DoesNotContain("建议开启", json, StringComparison.Ordinal);
        Assert.DoesNotContain("建议启用", json, StringComparison.Ordinal);
        Assert.DoesNotContain("enable_log", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LogRecoveryModelUnsupported", json, StringComparison.Ordinal);
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web);

    private static async Task<Guid> PrepareDatabaseAsync(HttpClient client, string token)
    {
        using var file = await SendAsync(client, HttpMethod.Post, "/api/v1/file-credentials",
            new CreateFileCredentialRequest { Name = "SMB", Username = "synthetic", Kind = FileCredentialKindValue.SmbPassword, Password = "synthetic-password" }, token);
        var fileItem = (await file.Content.ReadFromJsonAsync<FileCredentialResponse>())!;
        using var sql = await SendAsync(client, HttpMethod.Post, "/api/v1/sql-credentials",
            new CreateSqlCredentialRequest { Name = "SQL", Username = "synthetic", Password = "synthetic-password" }, token);
        var sqlItem = (await sql.Content.ReadFromJsonAsync<SqlCredentialResponse>())!;
        using var createdServer = await SendAsync(client, HttpMethod.Post, "/api/v1/servers", new SaveServerRequest
        {
            Name = "测试服务器",
            LocalBackupRootPath = @"D:\Synthetic",
            Protocol = 1,
            Host = "synthetic-host",
            BasePath = "synthetic-share",
            CredentialId = fileItem.Id,
            IsEnabled = true,
        }, token);
        var server = (await createdServer.Content.ReadFromJsonAsync<ServerResponse>())!;
        using var createdInstance = await SendAsync(client, HttpMethod.Post, "/api/v1/instances", new SaveInstanceRequest
        {
            ServerId = server.Id,
            Name = "默认实例",
            ConnectionAddress = "synthetic-host",
            SqlCredentialId = sqlItem.Id,
            TrustServerCertificate = false,
            ConnectionTimeoutSeconds = 15,
            IsEnabled = true,
        }, token);
        var instance = (await createdInstance.Content.ReadFromJsonAsync<InstanceResponse>())!;
        using var discover = await SendAsync(client, HttpMethod.Post, $"/api/v1/instances/{instance.Id}/discover",
            new InstanceProbeRequest { Version = instance.Version }, token);
        Assert.Equal(HttpStatusCode.OK, discover.StatusCode);
        var inventory = (await client.GetFromJsonAsync<ServerInventoryResponse>("/api/v1/server-inventory"))!;
        var database = Assert.Single(inventory.Databases);
        using var manage = await SendAsync(client, HttpMethod.Post, $"/api/v1/databases/{database.Id}/managed",
            new SetDatabaseManagedRequest { Version = database.Version, IsManaged = true }, token);
        Assert.Equal(HttpStatusCode.OK, manage.StatusCode);
        return database.Id;
    }

    private static Task<HttpResponseMessage> SendAsync<T>(HttpClient client, HttpMethod method, string path, T value, string token)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(value) };
        request.Headers.Add(AdminAuthenticationDefaults.CsrfHeaderName, token);
        return client.SendAsync(request);
    }

    private sealed class FakeProbe : ITargetSqlReadOnlyProbe
    {
        public Task<TargetSqlResult<TargetSqlServerInfo>> ProbeServerAsync(TargetSqlConnectionInput connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(TargetSqlResult.Succeeded(new TargetSqlServerInfo("15.0.synthetic", "synthetic", "synthetic", 3, 15, true)));

        public Task<TargetSqlResult<TargetSqlDatabaseCatalog>> DiscoverDatabasesAsync(TargetSqlConnectionInput connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(TargetSqlResult.Succeeded(new TargetSqlDatabaseCatalog([
                new("synthetic-db", TargetSqlDatabaseState.Online, TargetSqlRecoveryModel.Full, false, false, true)])));

        public Task<TargetSqlResult<TargetSqlBackupVerification>> VerifyBackupAsync(
            TargetSqlConnectionInput connection, TargetSqlBackupVerificationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
