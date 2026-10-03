using DbBackupManager.Web.Components.Shared;

namespace DbBackupManager.Web.Tests.Components;

public sealed class BackupFilePresentationTests
{
    [Theory]
    [InlineData("Local", "本地")]
    [InlineData("Remote", "远程")]
    [InlineData("Available", "可用")]
    [InlineData("DeletePending", "待删除")]
    [InlineData("DeleteFailed", "删除失败")]
    [InlineData("Deleted", "已删除")]
    [InlineData("Missing", "缺失")]
    [InlineData("Smb", "SMB")]
    [InlineData("Sftp", "SFTP")]
    public void MapsKnownValues(string value, string expected)
    {
        Assert.Contains(expected, new[]
        {
            BackupFilePresentation.Location(value),
            BackupFilePresentation.Status(value),
            BackupFilePresentation.Protocol(value),
        }, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("HostKeyMismatch", "主机密钥指纹不匹配")]
    [InlineData("CredentialUnavailable", "访问凭据不可用")]
    [InlineData("FileNotFound", "指定文件不存在")]
    [InlineData("ConnectionFailed", "用户名和密码")]
    [InlineData("AuthenticationFailed", "身份验证失败")]
    public void FailureMapsKnownCodes(string code, string expected) =>
        Assert.Contains(expected, BackupFilePresentation.Failure(code), StringComparison.Ordinal);

    [Fact]
    public void UnknownFailureDoesNotEchoItsContents() =>
        Assert.DoesNotContain("synthetic_unknown", BackupFilePresentation.Failure("synthetic_unknown"), StringComparison.Ordinal);

    [Fact]
    public void MissingStatusNoteDoesNotUseGenericOperationFailure()
    {
        var note = BackupFilePresentation.StatusNote("Missing", null);
        Assert.Equal("登记副本在目标位置已找不到。", note);
        Assert.DoesNotContain("文件操作失败", note, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteFailedStatusNoteUsesFailureClassification() =>
        Assert.Contains("主机密钥指纹不匹配", BackupFilePresentation.StatusNote("DeleteFailed", "HostKeyMismatch"), StringComparison.Ordinal);
}
