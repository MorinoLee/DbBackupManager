using System.Net.Http.Json;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.TargetSql;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DbBackupManager.UiPreview;

internal static class Preview
{
    private static async Task Main(string[] args)
    {
        var name = "DbBackupManagerUiRefactor_" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(root);
        var connection = $@"Server=(localdb)\MSSQLLocalDB;Database={name};Integrated Security=true;Encrypt=true;TrustServerCertificate=true";
        await using var context = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(connection).Options);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseContentRoot(Path.GetFullPath("../../../../../src/DbBackupManager.Web", AppContext.BaseDirectory));
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["urls"] = "http://127.0.0.1:5088",
                    ["ConnectionStrings:PlatformDatabase"] = connection,
                    ["DataProtection:CookieKeyRingPath"] = Path.Combine(root, "cookie"),
                    ["DataProtection:BusinessCredentialKeyRingPath"] = Path.Combine(root, "business"),
                    ["Logging:LogLevel:Default"] = "Warning",
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITargetSqlReadOnlyProbe>();
                services.AddSingleton<ITargetSqlReadOnlyProbe, SyntheticProbe>();
            });
        });
        try
        {
            await context.Database.MigrateAsync();
            factory.UseKestrel(5088);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost:5088") });
            Console.WriteLine("PREVIEW_HTTP " + client.BaseAddress);
            var token = (await client.GetFromJsonAsync<CsrfTokenResponse>("/api/v1/auth/csrf"))!.Token;
            await Post<AdminSessionResponse>(client, "/api/v1/auth/setup", new AdminSetupRequest("preview-admin", "synthetic-password"), token);
            token = (await client.GetFromJsonAsync<CsrfTokenResponse>("/api/v1/auth/csrf"))!.Token;
            Console.WriteLine($"PREVIEW_READY http://localhost:5088 CONTROL={root}");
            var seeded = args.Contains("--seed", StringComparer.Ordinal);
            if (seeded) await Seed(client, token);
            if (args.Contains("--monitoring", StringComparer.Ordinal))
            {
                if (!seeded) { await Seed(client, token); seeded = true; }
                await SeedMonitoring(client, token, factory.Services);
            }
            var deadline = DateTimeOffset.UtcNow.AddHours(2);
            while (!File.Exists(Path.Combine(root, "stop")) && DateTimeOffset.UtcNow < deadline)
            {
                if (!seeded && File.Exists(Path.Combine(root, "seed")))
                {
                    await Seed(client, token);
                    seeded = true;
                    Console.WriteLine("PREVIEW_SEEDED");
                }
                await Task.Delay(500);
            }
        }
        finally
        {
            await factory.DisposeAsync();
            // 本进程仅生成固定前缀的随机 LocalDB 数据库，不接受外部连接配置。
            if (name.StartsWith("DbBackupManagerUiRefactor_", StringComparison.Ordinal))
            {
                await context.Database.EnsureDeletedAsync();
            }
            Console.WriteLine("PREVIEW_DATABASE_REMOVED");
            var resolvedRoot = Path.GetFullPath(root);
            if (resolvedRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolvedRoot) == name && Directory.Exists(resolvedRoot))
            {
                Directory.Delete(resolvedRoot, recursive: true);
            }
        }
    }

    private static async Task<T> Post<T>(HttpClient client, string path, object body, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task Seed(HttpClient client, string token)
    {
        var file = await Post<FileCredentialResponse>(client, "/api/v1/file-credentials",
            new CreateFileCredentialRequest { Name = "演示暂存访问", Username = "synthetic-user", Kind = FileCredentialKindValue.SmbPassword, Password = "synthetic-password" }, token);
        var sql = await Post<SqlCredentialResponse>(client, "/api/v1/sql-credentials",
            new CreateSqlCredentialRequest { Name = "演示 SQL 凭据", Username = "synthetic-sql", Password = "synthetic-password" }, token);
        var server = await Post<ServerResponse>(client, "/api/v1/servers", new SaveServerRequest
        {
            Name = "演示业务服务器",
            LocalBackupRootPath = @"D:\Synthetic\Backups",
            Protocol = 1,
            Host = "synthetic-host",
            BasePath = "synthetic-share",
            CredentialId = file.Id,
            IsEnabled = true,
        }, token);
        var instance = await Post<InstanceResponse>(client, "/api/v1/instances", new SaveInstanceRequest
        {
            ServerId = server.Id,
            Name = "默认实例",
            ConnectionAddress = "synthetic-host",
            SqlCredentialId = sql.Id,
            TrustServerCertificate = false,
            ConnectionTimeoutSeconds = 15,
            IsEnabled = true,
        }, token);
        await Post<object>(client, $"/api/v1/instances/{instance.Id}/discover", new InstanceProbeRequest { Version = instance.Version }, token);
        var inventory = (await client.GetFromJsonAsync<ServerInventoryResponse>("/api/v1/server-inventory"))!;
        foreach (var database in inventory.Databases)
        {
            await Post<DatabaseResponse>(client, $"/api/v1/databases/{database.Id}/managed", new SetDatabaseManagedRequest { Version = database.Version, IsManaged = true }, token);
            var policy = await Post<ManualBackupPolicyResponse>(client, "/api/v1/manual-backup-policies", new SaveManualBackupRequest
            {
                DatabaseId = database.Id,
                Name = database.Name + " 手动备份",
                RetentionDays = 7,
                BackupTimeoutMinutes = 120,
                VerifyTimeoutMinutes = 60,
                IsEnabled = true,
            }, token);
            await Post<object>(client, $"/api/v1/manual-backup-policies/{policy.Id}/run", new BackupTaskCommandRequest { RequestId = Guid.NewGuid() }, token);
        }
    }

    private static async Task SeedMonitoring(HttpClient client, string token, IServiceProvider services)
    {
        var dashboard = (await client.GetFromJsonAsync<BackupDashboardResponse>("/api/v1/backup-dashboard"))!;
        for (var i = 0; i < 22; i++)
            await Post<object>(client, $"/api/v1/manual-backup-policies/{dashboard.Policies[0].Id}/run",
                new BackupTaskCommandRequest { RequestId = Guid.NewGuid() }, token);
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupTaskExecutionStore>();
        // 仅向此夹具随机 LocalDB 写入合成阶段结果，不调用 Worker 或任何外部适配器。
        for (var i = 0; i < 3; i++)
        {
            var now = DateTimeOffset.UtcNow;
            var item = (await store.ClaimNextAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "synthetic-preview", now, now.AddMinutes(5)))).Value!;
            var lease = (await store.MarkBackupInvocationStartedAsync(item.Lease, item.Attempt.RowVersion, DateTimeOffset.UtcNow)).Value!;
            if (i == 0)
            {
                var backedUp = (await store.CommitStageAsync(lease, new(Guid.NewGuid(), BackupStageOutcome.Succeeded, DateTimeOffset.UtcNow))).Value!;
                await store.CommitStageAsync(backedUp.Lease!, new(Guid.NewGuid(), BackupStageOutcome.Succeeded, DateTimeOffset.UtcNow, SourceLengthBytes: 921572864));
            }
            else
            {
                var result = await store.CommitStageAsync(lease, new(Guid.NewGuid(), i == 1 ? BackupStageOutcome.ConfirmedFailed : BackupStageOutcome.Indeterminate,
                    DateTimeOffset.UtcNow, ErrorCode: i == 1 ? "smb_credential_decryption_failed" : "backup_outcome_unknown", ErrorMessage: "合成验收结果。"));
                if (!result.IsSucceeded) throw new InvalidOperationException("合成任务结果未能登记。");
            }
        }
        await scope.ServiceProvider.GetRequiredService<IWorkerHeartbeatStore>().RegisterAsync(Guid.NewGuid(), CancellationToken.None);
    }

    private sealed class SyntheticProbe : ITargetSqlReadOnlyProbe
    {
        public Task<TargetSqlResult<TargetSqlServerInfo>> ProbeServerAsync(TargetSqlConnectionInput connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(TargetSqlResult.Succeeded(new TargetSqlServerInfo("15.0.synthetic", "synthetic", "synthetic", 3, 15, true)));
        public Task<TargetSqlResult<TargetSqlDatabaseCatalog>> DiscoverDatabasesAsync(TargetSqlConnectionInput connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(TargetSqlResult.Succeeded(new TargetSqlDatabaseCatalog([
                new("演示订单库", TargetSqlDatabaseState.Online, TargetSqlRecoveryModel.Full, false, false, true),
                new("Synthetic_Long_Database_Name_For_Responsive_Layout_Validation", TargetSqlDatabaseState.Online, TargetSqlRecoveryModel.Full, false, false, true)])));
        public Task<TargetSqlResult<TargetSqlBackupVerification>> VerifyBackupAsync(TargetSqlConnectionInput connection, TargetSqlBackupVerificationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
