using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DbBackupManager.Infrastructure.FileStorage;
using Microsoft.Win32.SafeHandles;

namespace DbBackupManager.Infrastructure.Tests.FileStorage;

public sealed class NativePlanFileGuardTests
{
    [Fact]
    public async Task RealHandlesProtectContentObjectAndAncestorsThroughCommitWindow()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"DbBackupManagerPlanGuard_{Guid.NewGuid():N}");
        var parent = Path.Combine(root, "synthetic");
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, "source.bak");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        try
        {
            await using (var guarded = NativePlanFileGuard.Open(path, [root, parent], false, false))
            {
                var content = await guarded.HashAsync(CancellationToken.None);
                Assert.NotNull(content.StableObjectId);
                Assert.True(guarded.IsProtected);
                guarded.VerifyShareDenials();
                Assert.Throws<IOException>(() => File.WriteAllBytes(path, [5, 6, 7, 8]));
                Assert.Throws<IOException>(() => File.Delete(path));
                Assert.Throws<IOException>(() => Directory.Move(parent, parent + "-replaced"));
                Assert.Equal(content, await guarded.HashAsync(CancellationToken.None));
            }
            File.WriteAllBytes(path, [5, 6, 7, 8]);
        }
        finally { DeleteTestDirectory(root, "DbBackupManagerPlanGuard_"); }
    }

    [Fact]
    public async Task RealRenameKeepsOriginalObjectProtectedAndNeverReplacesDestination()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"DbBackupManagerPlanRename_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var part = Path.Combine(root, "synthetic.bak.part");
        var final = Path.Combine(root, "synthetic.bak");
        await File.WriteAllBytesAsync(part, [1, 2, 3, 4]);
        try
        {
            await using var guarded = NativePlanFileGuard.Open(part, [root], false, true);
            var before = await guarded.HashAsync(CancellationToken.None);
            File.WriteAllBytes(final, [9, 9, 9, 9]);
            Assert.Throws<IOException>(() => { if (OperatingSystem.IsWindows()) guarded.RenameNoReplace(final); });
            Assert.Equal(new byte[] { 9, 9, 9, 9 }, await File.ReadAllBytesAsync(final));
            File.Delete(final);
            try { guarded.RenameNoReplace(final); }
            catch (IOException exception) { Assert.Fail($"{exception.Message}; native={exception.InnerException?.Message}; paths={string.Join(',', Directory.GetFiles(root).Select(Path.GetFileName))}"); }
            Assert.False(File.Exists(part));
            Assert.True(File.Exists(final));
            Assert.Equal(before, await guarded.HashAsync(CancellationToken.None));
            Assert.Throws<IOException>(() => File.WriteAllBytes(final, [5, 6, 7, 8]));
            Assert.Throws<IOException>(() => File.Delete(final));
        }
        finally { DeleteTestDirectory(root, "DbBackupManagerPlanRename_"); }
    }

    [Fact]
    public async Task CapabilityCleanupDeletesOnlyItsGuardedRandomObject()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"DbBackupManagerPlanGuard_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var probe = Path.Combine(root, $".dbbackup-capability-{Guid.NewGuid():N}.renamed");
        var backup = Path.Combine(root, "synthetic_FULL.bak");
        try
        {
            await File.WriteAllBytesAsync(probe, [1]);
            await File.WriteAllBytesAsync(backup, [1]);
            await using (var guarded = NativePlanFileGuard.Open(backup, [root], false, true))
            {
                var content = await guarded.HashAsync(CancellationToken.None);
                Assert.Throws<IOException>(() =>
                {
                    if (OperatingSystem.IsWindows()) guarded.DeleteOwnedCapabilityProbe(content.StableObjectId!);
                });
            }
            await using (var guarded = NativePlanFileGuard.Open(probe, [root], false, true))
            {
                var content = await guarded.HashAsync(CancellationToken.None);
                Assert.Throws<IOException>(() =>
                {
                    if (OperatingSystem.IsWindows()) guarded.DeleteOwnedCapabilityProbe("different-object");
                });
                guarded.DeleteOwnedCapabilityProbe(content.StableObjectId!);
            }
            Assert.False(File.Exists(probe));
            Assert.True(File.Exists(backup));
        }
        finally { DeleteTestDirectory(root, "DbBackupManagerPlanGuard_"); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectedNonemptyAncestorsCannotBeRedirectedByReparsePoint(bool smb)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"DbBackupManagerPlanGuard_{Guid.NewGuid():N}");
        var parent = Path.Combine(root, "protected");
        var control = Path.Combine(root, "empty-control");
        var target = Path.Combine(root, "synthetic-target");
        string Address(string path) => smb ? $"\\\\localhost\\{path[0]}$\\{path[3..]}" : path;
        try
        {
            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(target);
            var file = Path.Combine(parent, "source.bak");
            await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
            await using var guarded = NativePlanFileGuard.Open(Address(file), [Address(root), Address(parent)], false, false);
            var before = await guarded.HashAsync(CancellationToken.None);
            // 空目录对照证明载荷、权限与实际 FSCTL 可用，不能把无效请求当成保护。
            Assert.Equal(0, ReparseAttack.SetJunction(Address(control), target));
            Assert.True((File.GetAttributes(control) & FileAttributes.ReparsePoint) != 0);
            Directory.Delete(control);
            Assert.Equal(145, ReparseAttack.SetJunction(Address(parent), target)); // ERROR_DIR_NOT_EMPTY。
            Assert.False((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(before, await guarded.HashAsync(CancellationToken.None));
            guarded.VerifyShareDenials();
        }
        finally { DeleteTestDirectory(root, "DbBackupManagerPlanGuard_"); }
    }

    [SupportedOSPlatform("windows")]
    private static class ReparseAttack
    {
        internal static int SetJunction(string directory, string target)
        {
            using var handle = CreateFileW(directory, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid) return Marshal.GetLastWin32Error();
            var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
            var print = Encoding.Unicode.GetBytes(target);
            var buffer = new byte[16 + substitute.Length + sizeof(char) + print.Length + sizeof(char)];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003); // IO_REPARSE_TAG_MOUNT_POINT。
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + sizeof(char))));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
            substitute.CopyTo(buffer, 16);
            print.CopyTo(buffer, 16 + substitute.Length + sizeof(char));
            return DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)
                ? 0 : Marshal.GetLastWin32Error();
        }
#pragma warning disable SYSLIB1054
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength,
            IntPtr output, int outputLength, out uint returned, IntPtr overlapped);
#pragma warning restore SYSLIB1054
    }
    private static void DeleteTestDirectory(string root, string prefix)
    {
        var absolute = Path.GetFullPath(root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), absolute, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(prefix, Path.GetFileName(absolute), StringComparison.Ordinal);
        Directory.Delete(absolute, recursive: true);
    }
}
