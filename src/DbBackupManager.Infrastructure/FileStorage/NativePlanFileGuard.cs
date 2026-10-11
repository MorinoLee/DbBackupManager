using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupTasks;
using Microsoft.Win32.SafeHandles;

namespace DbBackupManager.Infrastructure.FileStorage;

internal interface IProtectedPlanFileSession
{
    NativePlanFileGuard OpenGuarded(string path, bool create, bool rename);
    void VerifyGuardedCapabilities(NativePlanFileGuard file);
    void RenameGuarded(NativePlanFileGuard file, string finalPath);
}

/// <summary>目录名称禁止共享删除/替换，文件禁止共享写入/删除，保护延续至平台提交完成。</summary>
[SupportedOSPlatform("windows")]
internal sealed class NativePlanFileGuard : IAsyncDisposable
{
    private readonly List<SafeFileHandle> _parents;
    private bool _disposed;
    private NativePlanFileGuard(string path, FileStream stream, List<SafeFileHandle> parents)
    { Path = path; Stream = stream; _parents = parents; }
    public string Path { get; private set; }
    public FileStream Stream { get; }
    public bool IsProtected => !_disposed && !Stream.SafeFileHandle.IsClosed && _parents.All(x => !x.IsClosed && !x.IsInvalid);

    internal static NativePlanFileGuard Open(string path, IEnumerable<string> parentPaths, bool create, bool rename)
    {
        var parents = new List<SafeFileHandle>();
        FileStream? stream = null;
        try
        {
            foreach (var parent in parentPaths)
            {
                // LIST_DIRECTORY 参与共享读/写/删除检查；仅 READ_ATTRIBUTES 不足以保护目录名称。
                // 目录项新增/重命名需要共享写；禁止共享 DELETE 保持每级目录名称不被替换。
                var handle = OpenNative(parent, 0x81, 3, 0x02200000, share: 3);
                parents.Add(handle);
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0
                    || !string.Equals(FinalPath(handle), parent, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("受保护目录身份不一致。");
            }
            if (create) stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
            else if (rename)
            {
                var handle = OpenNative(path, 0x80010000, 3, 0x40200000); // GENERIC_READ、DELETE；OVERLAPPED。
                try { stream = new FileStream(handle, FileAccess.Read, 128 * 1024, isAsync: true); }
                catch { handle.Dispose(); throw; }
            }
            else stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
                || !string.Equals(FinalPath(stream.SafeFileHandle), path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("受保护文件身份不一致。");
            return new(path, stream, parents);
        }
        catch
        {
            stream?.Dispose();
            foreach (var parent in parents) parent.Dispose();
            throw;
        }
    }

    public async Task<BackupContentEvidence> HashAsync(CancellationToken token)
    {
        if (!IsProtected) throw new IOException("文件保护已失效。");
        Stream.Position = 0;
        var length = Stream.Length;
        var digest = await SHA256.HashDataAsync(Stream, token);
        if (length <= 0 || Stream.Position != length || Stream.Length != length || !IsProtected)
            throw new IOException("文件内容长度或保护状态发生变化。");
        Stream.Position = 0;
        return new()
        {
            State = BackupContentState.Verified,
            DigestAlgorithm = "SHA256",
            DigestHex = Convert.ToHexStringLower(digest),
            LengthBytes = length,
            StableObjectId = SmbFileOperations.TryGetIdentity(Stream.SafeFileHandle),
            Protection = BackupObjectProtection.GuardedUntilCommit
        };
    }

    internal void VerifyShareDenials()
    {
        if (!IsProtected) throw new IOException("对象保护已失效。");
        RequireSharingViolation(Path, 0x40000000, 0x00200000); // 尝试取得写权限，不写入字节。
        RequireSharingViolation(Path, 0x10000, 0x00200000); // 尝试取得 DELETE，不删除对象。
        foreach (var parent in _parents)
            RequireSharingViolation(FinalPath(parent), 0x10000, 0x02200000);

        static void RequireSharingViolation(string path, uint access, uint flags)
        {
            using var contender = CreateFileW(NativePath(path), access, 7, IntPtr.Zero, 3, flags, IntPtr.Zero);
            var error = Marshal.GetLastWin32Error();
            if (!contender.IsInvalid || error != 32)
                throw new IOException("无法实证当前端点的写入、删除或路径替换共享保护。", new Win32Exception(error));
        }
    }

    public void RenameNoReplace(string destination)
    {
        if (!IsProtected || !string.Equals(System.IO.Path.GetDirectoryName(Path), System.IO.Path.GetDirectoryName(destination), StringComparison.OrdinalIgnoreCase))
            throw new IOException("重命名对象或目录不一致。");
        if (_parents.Count == 0 || !string.Equals(FinalPath(_parents[^1]), System.IO.Path.GetDirectoryName(destination), StringComparison.OrdinalIgnoreCase))
            throw new IOException("无覆盖重命名缺少原目录保护。");
        var name = Encoding.Unicode.GetBytes(NativePath(destination));
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(int);
        var buffer = Marshal.AllocHGlobal(nameOffset + name.Length + sizeof(char));
        try
        {
            Marshal.Copy(new byte[nameOffset + name.Length + sizeof(char)], 0, buffer, nameOffset + name.Length + sizeof(char));
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            // ReplaceIfExists 为 FALSE；原文件和目录名称保护均不释放。
            if (!SetFileInformationByHandle(Stream.SafeFileHandle, 3, buffer, (uint)(nameOffset + name.Length + sizeof(char))))
                throw new IOException("无覆盖重命名失败。", new Win32Exception(Marshal.GetLastWin32Error()));
            // Windows 的原 FILE_OBJECT 名称可能保持旧值；在原锁未释放时核对目标名称。
            using var named = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var originalId = SmbFileOperations.TryGetIdentity(Stream.SafeFileHandle);
            if (originalId is null || originalId != SmbFileOperations.TryGetIdentity(named.SafeFileHandle)
                || !string.Equals(FinalPath(named.SafeFileHandle), destination, StringComparison.OrdinalIgnoreCase))
                throw new IOException("重命名后的路径或对象身份不一致。");
            Path = destination;
            if (!IsProtected) throw new IOException("重命名后的路径保护失效。");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await Stream.DisposeAsync(); }
        finally { foreach (var parent in _parents) parent.Dispose(); }
    }
    internal void DeleteOwnedCapabilityProbe(string expectedObjectId)
    {
        var name = System.IO.Path.GetFileName(Path);
        const string prefix = ".dbbackup-capability-";
        var suffix = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : string.Empty;
        if (suffix.EndsWith(".renamed", StringComparison.Ordinal)) suffix = suffix[..^8];
        if (!Guid.TryParseExact(suffix, "N", out _) || !IsProtected
            || expectedObjectId != SmbFileOperations.TryGetIdentity(Stream.SafeFileHandle))
            throw new IOException("拒绝清理非本次能力探测对象。");
        var disposition = Marshal.AllocHGlobal(sizeof(byte));
        try
        {
            Marshal.WriteByte(disposition, 1);
            if (!SetFileInformationByHandle(Stream.SafeFileHandle, 4, disposition, sizeof(byte)))
                throw new IOException("本次能力探测对象清理失败。", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(disposition); }
    }
    private static string FinalPath(SafeFileHandle handle)
    {
        var path = SmbFileOperations.GetFinalPath(handle);
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
    private static SafeFileHandle OpenNative(string path, uint access, uint disposition, uint flags, uint share = 1)
    {
        var handle = CreateFileW(NativePath(path), access, share, IntPtr.Zero, disposition, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException("无法取得对象保护句柄。", new Win32Exception(error));
    }
    private static string NativePath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal)
        ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr information, uint size);
#pragma warning restore SYSLIB1054
}
