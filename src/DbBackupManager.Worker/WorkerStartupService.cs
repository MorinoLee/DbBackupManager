using DbBackupManager.Application.BackupTasks;

namespace DbBackupManager.Worker;

internal sealed class WorkerStartupService(IWorkerStartupDiagnostics diagnostics,
    IHostEnvironment environment, ILogger<WorkerStartupService> logger) : IHostedService
{
    private static readonly Action<ILogger, bool, Exception?> EnvironmentInfo = LoggerMessage.Define<bool>(
        LogLevel.Information, new EventId(5702), "Worker 配置检查：Development={Development}。User Secrets 仅在 Development 自动加载；Web 与 Worker 必须共享业务密钥目录。此检查不证明平台连接或既有凭据可解密。");
    private static readonly Action<ILogger, string, Exception?> Rejected = LoggerMessage.Define<string>(
        LogLevel.Error, new EventId(5703), "Worker 启动被配置检查阻止：{Code}。请检查平台连接配置、业务密钥目录与服务身份读取权限；修正后重新启动。");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnvironmentInfo(logger, environment.IsDevelopment(), null);
        var code = diagnostics.CheckConfiguration();
        if (code is not null)
        {
            Rejected(logger, code, null);
            throw new InvalidOperationException($"Worker 配置检查失败：{code}。");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
