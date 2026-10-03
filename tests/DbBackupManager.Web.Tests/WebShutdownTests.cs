using System.Net;
using System.Net.WebSockets;
using System.Text;
using DbBackupManager.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbBackupManager.Web.Tests;

public sealed class WebShutdownTests
{
    [Fact]
    public async Task StalledRequestDoesNotExhaustHostShutdownBudget()
    {
        await using var factory = new ShutdownFactory();
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        var response = client.GetAsync("/shutdown-test");
        await factory.Request.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await factory.Host!.StopAsync(deadline.Token);

        Assert.False(deadline.IsCancellationRequested);
        Assert.True(factory.Request.Aborted);
        Assert.IsAssignableFrom<HttpRequestException>(await Record.ExceptionAsync(async () => await response));
    }

    [Fact]
    public async Task InFlightRequestCanFinishGracefullyDuringShutdown()
    {
        await using var factory = new ShutdownFactory();
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        var response = client.GetAsync("/shutdown-test");
        await factory.Request.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var stop = factory.Host!.StopAsync(deadline.Token);
        await stopping.Task.WaitAsync(deadline.Token);
        factory.Request.Release.TrySetResult();
        using var result = await response;
        await stop;

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("completed", await result.Content.ReadAsStringAsync());
        Assert.False(factory.Request.Aborted);
        Assert.False(deadline.IsCancellationRequested);
    }

    [Fact]
    public async Task DrainTimeoutDoesNotLimitRequestsWhileHostIsRunning()
    {
        await using var factory = new ShutdownFactory();
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        var response = client.GetAsync("/shutdown-test");
        await factory.Request.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // 真实连接保持超过排空时限，证明正常运行期间没有启动退出定时器。
        await Task.Delay(WebConnectionShutdown.DrainTimeout + TimeSpan.FromSeconds(1));
        Assert.False(response.IsCompleted);
        Assert.False(factory.Request.Aborted);
        factory.Request.Release.TrySetResult();
        using var result = await response;
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task OpenBlazorWebSocketDoesNotBlockShutdown()
    {
        await using var factory = new ShutdownFactory();
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        using var socket = new ClientWebSocket();
        var uri = new UriBuilder(client.BaseAddress!) { Scheme = "ws", Path = "/_blazor" }.Uri;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(uri, deadline.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"blazorpack\",\"version\":1}\u001e"),
            WebSocketMessageType.Text, true, deadline.Token);
        var buffer = new byte[1024];
        var handshake = await socket.ReceiveAsync(buffer, deadline.Token);
        Assert.Equal("{}\u001e", Encoding.UTF8.GetString(buffer, 0, handshake.Count));

        await factory.Host!.StopAsync(deadline.Token);
        Assert.False(deadline.IsCancellationRequested);
    }

    private sealed class ShutdownFactory : WebApplicationFactory<Program>
    {
        public IHost? Host { get; private set; }
        public RequestGate Request { get; } = new();

        protected override IHost CreateHost(IHostBuilder builder)
        {
            Host = base.CreateHost(builder);
            return Host;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // Cookie 事件构造会解析 DbContextFactory；握手不查库，但仍需独立的占位配置。
            // 禁止偶然依赖开发机的 User Secrets 或环境连接串。
            builder.UseSetting("ConnectionStrings:PlatformDatabase",
                "Server=127.0.0.1,1;Database=ShutdownTransportOnly;Integrated Security=true;Encrypt=true;Connect Timeout=1");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
            {
                // 只验证真实传输退出；不启动数据库轮询，不读取任何业务记录或持久化密钥。
                foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)
                    && (item.ImplementationType?.Namespace == "DbBackupManager.Web.BackupTasks"
                        || item.ImplementationType?.Name == "PlatformDatabaseMonitor")).ToArray())
                {
                    services.Remove(descriptor);
                }
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.AddSingleton<IStartupFilter>(Request);
                services.AddSingleton<IHostLifetime, CancellationCheckingLifetime>();
            });
        }
    }

    private sealed class RequestGate : IStartupFilter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Aborted { get; private set; }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextRequest) =>
            {
                if (context.Request.Path != "/shutdown-test")
                {
                    await nextRequest(context);
                    return;
                }

                Started.TrySetResult();
                try
                {
                    await Release.Task.WaitAsync(context.RequestAborted);
                    await context.Response.WriteAsync("completed");
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    Aborted = true;
                }
            });
            next(app);
        };
    }

    private sealed class CancellationCheckingLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // 对齐 WindowsServiceLifetime 的取消检查，无需真实安装或停止 Windows 服务。
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
