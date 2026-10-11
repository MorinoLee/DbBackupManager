using System.Reflection;
using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;

namespace DbBackupManager.Infrastructure.Tests;

/// <summary>真实数据库提交成功后丢失响应；不替换 SQL、文件或持久化实现。</summary>
internal sealed class PlanExecutionCommitFault(string method, string? kind = null)
{
    public bool Fired { get; private set; }
    public bool Armed { get; set; } = true;
    public Action? BeforeThrow { get; set; }
    public void Observe(string called, object?[] arguments)
    {
        if (!WillFire(called, arguments)) return;
        Fired = true;
        BeforeThrow?.Invoke();
        throw new LostPlanCommitResponseException();
    }
    public bool WillFire(string called, object?[] arguments)
    {
        var identity = arguments.OfType<ReserveBackupExecutionOperation>().FirstOrDefault()
            ?? arguments.OfType<FreezeBackupExecutionOperation>().FirstOrDefault()?.Identity;
        return Armed && !Fired && called == method && (kind is null || identity?.Kind.ToString() == kind);
    }
    public T Wrap<T>(T inner) where T : class
    {
        var proxy = DispatchProxy.Create<T, CommitResponseProxy>();
        ((CommitResponseProxy)(object)proxy).Initialize(inner, this);
        return proxy;
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1852", Justification = "DispatchProxy 必须动态派生代理类型。")]
    public class CommitResponseProxy : DispatchProxy
    {
        private object _inner = null!;
        private PlanExecutionCommitFault _fault = null!;
        public void Initialize(object inner, PlanExecutionCommitFault fault) { _inner = inner; _fault = fault; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var methodInfo = targetMethod!;
            var arguments = args!;
            var returned = methodInfo.Invoke(_inner, arguments)!;
            return typeof(CommitResponseProxy).GetMethod(nameof(WaitAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(methodInfo.ReturnType.GenericTypeArguments[0]).Invoke(this, [returned, methodInfo.Name, arguments]);
        }
        private async Task<T> WaitAsync<T>(Task<T> task, string called, object?[] arguments)
        {
            var result = await task;
            if (_fault.WillFire(called, arguments) && result is BackupFileStorageResult<VerifiedBackupRenameReceipt> { Value: { } receipt })
                await receipt.FinalHandle.DisposeAsync();
            _fault.Observe(called, arguments);
            return result;
        }
    }
}
internal sealed class LostPlanCommitResponseException : Exception;
