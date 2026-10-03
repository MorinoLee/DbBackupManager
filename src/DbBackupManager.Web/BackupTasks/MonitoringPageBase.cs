using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Web.SqlCredentials;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace DbBackupManager.Web.BackupTasks;

public abstract class MonitoringPageBase : ComponentBase, IDisposable
{
    [Inject] protected IBackupMonitoringService Monitoring { get; set; } = default!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = default!;
    [Inject] private TaskRefreshNotifier Notifications { get; set; } = default!;
    [Inject] private CircuitReconnectNotifier Reconnect { get; set; } = default!;
    [Inject] private MonitoringRefreshOptions RefreshOptions { get; set; } = default!;
    private readonly CancellationTokenSource _lifetime = new();
    private IDisposable? _notifications;
    private IDisposable? _reconnect;
    private long _version;
    private bool _disposed;
    private bool _noticePending;
    protected bool IsDisposed => _disposed;
    protected CancellationToken LifetimeToken => _lifetime.Token;
    protected bool Busy { get; private set; }
    protected bool RequiresLogin { get; private set; }
    protected string? Error { get; private set; }

    protected override void OnInitialized()
    {
        _notifications = Notifications.Subscribe(() => { _ = NoticeAsync(); });
        _reconnect = Reconnect.Subscribe(() => { _ = NoticeAsync(); });
        _ = CalibrateAsync();
    }
    protected override Task OnParametersSetAsync() { ClearData(); return RefreshAsync(); }
    protected abstract Task<Action> FetchAsync(AdminSession actor, CancellationToken token);
    protected abstract void ClearData();
    protected async Task RefreshAsync()
    {
        if (_disposed) return;
        var version = ++_version;
        Busy = true;
        Error = null;
        try
        {
            var actor = SqlCredentialPresentation.GetActor((await Authentication.GetAuthenticationStateAsync()).User);
            if (actor is null) throw new QueryFailure(BackupManagementCode.AuthenticationRequired);
            var apply = await FetchAsync(actor, _lifetime.Token);
            if (_disposed || version != _version) return;
            apply();
            RequiresLogin = false;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (_disposed || version != _version) return;
            ClearData();
            var code = e is QueryFailure failure ? failure.Code : BackupManagementCode.Unavailable;
            RequiresLogin = code == BackupManagementCode.AuthenticationRequired;
            Error = code == BackupManagementCode.Invalid ? "查询条件无效，请检查页码、状态和时间范围。" : BackupPresentation.Error(code).Message;
        }
        finally
        {
            if (!_disposed && version == _version)
            {
                Busy = false;
                if (_noticePending) { _noticePending = false; _ = NoticeAsync(); }
            }
        }
    }
    protected static T Require<T>(BackupManagementResult<T> result) => result.Code == BackupManagementCode.Succeeded
        ? result.Value! : throw new QueryFailure(result.Code);
    private async Task NoticeAsync()
    {
        try
        {
            if (!_disposed) await InvokeAsync(async () =>
            {
                if (Busy) { _noticePending = true; return; }
                await RefreshAsync(); StateHasChanged();
            });
        }
        catch (ObjectDisposedException) { }
    }
    private async Task CalibrateAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(RefreshOptions.CalibrationInterval, _lifetime.Token);
                await NoticeAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifications?.Dispose();
        _reconnect?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
    private sealed class QueryFailure(BackupManagementCode code) : Exception
    {
        public BackupManagementCode Code { get; } = code;
    }
}
