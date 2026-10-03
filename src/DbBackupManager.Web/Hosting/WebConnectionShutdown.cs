using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace DbBackupManager.Web.Hosting;

internal static class WebConnectionShutdown
{
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    public static void Configure(KestrelServerOptions options)
    {
        options.ConfigureEndpointDefaults(endpoint => endpoint.Use(next => async connection =>
        {
            var lifetime = connection.Features.Get<IConnectionLifetimeNotificationFeature>();
            if (lifetime is null)
            {
                await next(connection);
                return;
            }

            // Kestrel 请求关闭后仍给在途请求完成的机会；不让残留连接耗尽主机的退出预算。
            // 正常运行不计时，连接完成后按逆序注销回调，避免定时器在连接释放后触发。
            using var timeout = new CancellationTokenSource();
            using var abort = timeout.Token.Register(() => connection.Abort(
                new ConnectionAbortedException("Web 主机停止，连接排空超时。")));
            using var closing = lifetime.ConnectionClosedRequested.Register(() => timeout.CancelAfter(DrainTimeout));
            await next(connection);
        }));
    }
}
