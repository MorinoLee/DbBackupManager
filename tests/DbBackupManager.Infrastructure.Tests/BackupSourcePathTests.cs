using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;
using DbBackupManager.Infrastructure.BackupExecution;
namespace DbBackupManager.Infrastructure.Tests;

public sealed class BackupSourcePathTests
{
    [Theory]
    [InlineData("share\\..\\outside")]
    [InlineData("share:stream")]
    [InlineData("\\share")]
    [InlineData("share\\nested. ")]
    public void EscapedRootsAreRejectedBeforeAnyIo(string root)
    {
        var id = Guid.NewGuid(); var name = $"synthetic_{id:N}.bak";
        var attempt = new BackupAttemptModel(id, 1, BackupInvocationStatus.Prepared, $@"D:\Synthetic\{name}", $@"\\synthetic-host\{root}\{name}", null, null, null, null, null, null, null, new byte[8]);
        Assert.Throws<ArgumentException>(() => SmbBackupSourceProbe.ValidateLegacyPath(new(FileTransferProtocol.Smb, "synthetic-host", null, root, Guid.NewGuid(), null), attempt));
    }
    [Fact]
    public void ForeignAttemptFilenameIsRejected()
    {
        var attempt = new BackupAttemptModel(Guid.NewGuid(), 1, BackupInvocationStatus.Prepared, @"D:\Synthetic\foreign.bak", @"\\synthetic-host\share\foreign.bak", null, null, null, null, null, null, null, new byte[8]);
        Assert.Throws<ArgumentException>(() => SmbBackupSourceProbe.ValidateLegacyPath(new(FileTransferProtocol.Smb, "synthetic-host", null, "share", Guid.NewGuid(), null), attempt));
    }
}
