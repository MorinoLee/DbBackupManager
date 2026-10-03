using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbBackupManager.Infrastructure.Hosting;

public sealed class PlatformDatabaseReadiness : IPlatformDatabaseReadiness
{
    private readonly object _sync = new();
    private TaskCompletionSource _ready = NewSignal();
    private bool _available;

    public bool IsReady { get { lock (_sync) return _available; } }

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        lock (_sync) return _ready.Task.WaitAsync(cancellationToken);
    }

    internal void SetAvailable(bool available)
    {
        lock (_sync)
        {
            if (available) _ready.TrySetResult();
            else if (_available) _ready = NewSignal();
            _available = available;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public static class PlatformDatabaseReadinessRegistration
{
    public static IServiceCollection AddPlatformDatabaseReadiness(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PlatformDatabaseReadiness>();
        services.AddSingleton<IPlatformDatabaseReadiness>(provider => provider.GetRequiredService<PlatformDatabaseReadiness>());
        services.AddScoped<IPlatformDatabaseProbe, PlatformDatabaseProbe>();
        services.AddHostedService<PlatformDatabaseMonitor>();
        return services;
    }
}

internal interface IPlatformDatabaseProbe
{
    Task CheckAsync(CancellationToken cancellationToken);
}

internal sealed class PlatformDatabaseProbe(IDbContextFactory<PlatformDbContext> factory) : IPlatformDatabaseProbe
{
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        // 仅探测平台连接；绕过 EF 查询/重试日志，不改变业务 Context 的连接与重试策略。
        var connection = context.Database.GetDbConnection();
        connection.ConnectionString = new SqlConnectionStringBuilder(connection.ConnectionString)
        {
            ConnectTimeout = 3,
            ConnectRetryCount = 0,
            Pooling = false,
        }.ConnectionString;
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.CommandTimeout = 3;
        await command.ExecuteScalarAsync(cancellationToken);
    }
}

internal sealed class PlatformDatabaseMonitor(
    IServiceScopeFactory scopes,
    PlatformDatabaseReadiness readiness,
    TimeProvider clock,
    ILogger<PlatformDatabaseMonitor> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> Unavailable = LoggerMessage.Define(
        LogLevel.Warning, new EventId(6001), "平台数据库尚不可用，后台循环等待；请检查 SQL 服务、连接配置与服务身份权限。每 10 秒重新探测，持续故障每 5 分钟提醒。");
    private static readonly Action<ILogger, Exception?> Available = LoggerMessage.Define(
        LogLevel.Information, new EventId(6002), "平台数据库连接探测通过，后台循环恢复；此结果不证明 Migration、备份目标或 Worker 心跳健康。");
    private bool? _previous;
    private DateTimeOffset _lastWarning;

    internal async Task ProbeOnceAsync(CancellationToken cancellationToken)
    {
        var available = false;
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IPlatformDatabaseProbe>().CheckAsync(cancellationToken);
            available = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { /* 不输出原始连接异常或受保护配置。 */ }
        readiness.SetAvailable(available);
        var now = clock.GetUtcNow();
        if (!available && (_previous != false || now - _lastWarning >= TimeSpan.FromMinutes(5)))
        {
            Unavailable(logger, null);
            _lastWarning = now;
        }
        else if (available && _previous != true) Available(logger, null);
        _previous = available;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ProbeOnceAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(10), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { readiness.SetAvailable(false); }
    }
}
