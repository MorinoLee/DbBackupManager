using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.FileStorage;
using DbBackupManager.Domain.BackupTasks;
using DbBackupManager.Domain.Configuration;

namespace DbBackupManager.Application.Tests;

public sealed class BackupExecutionBoundaryTests
{
    private static readonly BackupFileEndpointInput Endpoint = new(FileTransferProtocol.Smb, "synthetic-host", null,
        "synthetic-share", Guid.NewGuid(), null);
    private static readonly BackupContentEvidence Content = new()
    {
        State = BackupContentState.Verified,
        DigestAlgorithm = "SHA256",
        DigestHex = new string('a', 64),
        LengthBytes = 4096,
        Protection = BackupObjectProtection.GuardedUntilCommit
    };

    [Fact]
    public async Task ClosedPlanRunnerReturnsPhaseResultAndObservesCancellation()
    {
        var runner = new BackupPlanTaskRunner();
        Assert.Equal(BackupExecutionContractCode.StageNotOpen, await runner.RunTaskOnceAsync(new(Guid.NewGuid(), Guid.NewGuid())));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunTaskOnceAsync(new(Guid.NewGuid(), Guid.NewGuid()), cancellation.Token));
    }

    [Fact]
    public void TransferReceiptRequiresFullContentMatchAtEveryBoundary()
    {
        Assert.True(new VerifiedBackupTransferReceipt(Content, Content, Content).MatchesVerifiedSource(Content));
        var replaced = Content with { DigestHex = new string('b', 64) };
        Assert.False(new VerifiedBackupTransferReceipt(Content, Content, replaced).MatchesVerifiedSource(Content));
        Assert.False(new VerifiedBackupTransferReceipt(Content, replaced, Content).MatchesVerifiedSource(Content));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameLengthReplacementAndLostRenameResponseNeedFreshProtectedEvidence(bool stableId)
    {
        var source = Content with { StableObjectId = stableId ? "synthetic-id" : null };
        await using var handle = new Handle(source);
        var receipt = new VerifiedBackupRenameReceipt(source, source, false, true, handle);
        Assert.True(receipt.CanRegisterAvailable(source, Endpoint, handle.Path));
        handle.Content = source with { DigestHex = new string('b', 64) };
        Assert.False(receipt.CanRegisterAvailable(source, Endpoint, handle.Path));
        handle.Content = source;
        await handle.DisposeAsync();
        Assert.False(receipt.CanRegisterAvailable(source, Endpoint, handle.Path));
        // 响应丢失后旧回执不足；恢复观察须获得新的路径/对象保护。
        await using var recovered = new Handle(source);
        Assert.True(new VerifiedBackupRenameReceipt(source, recovered.Content, false, true, recovered)
            .CanRegisterAvailable(source, Endpoint, recovered.Path));
        Assert.False(receipt.CanRegisterAvailable(source, Endpoint, @"\\synthetic-host\synthetic-share\other.bak"));
    }

    [Fact]
    public async Task UnsupportedProtectionCannotRegisterAvailableEvenWithMatchingDigest()
    {
        await using var handle = new Handle(Content with { Protection = BackupObjectProtection.Unsupported });
        Assert.False(new VerifiedBackupRenameReceipt(Content, Content, false, true, handle)
            .CanRegisterAvailable(Content, Endpoint, handle.Path));
        Assert.False(new VerifiedBackupRenameReceipt(Content, Content, true, true, handle)
            .CanRegisterAvailable(Content, Endpoint, handle.Path));
    }

    private sealed class Handle(BackupContentEvidence content) : IVerifiedBackupFileHandle
    {
        public BackupFileEndpointInput Endpoint => BackupExecutionBoundaryTests.Endpoint;
        public string Path => @"\\synthetic-host\synthetic-share\final.bak";
        public BackupContentEvidence Content { get; set; } = content;
        public bool GuardsExactPathUntilCommit { get; private set; } = true;
        public ValueTask DisposeAsync() { GuardsExactPathUntilCommit = false; return ValueTask.CompletedTask; }
    }
}
