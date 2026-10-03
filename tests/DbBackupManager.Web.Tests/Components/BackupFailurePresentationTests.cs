using DbBackupManager.Web.BackupTasks;

namespace DbBackupManager.Web.Tests.Components;

public sealed class BackupFailurePresentationTests
{
    [Theory]
    [InlineData("smb_credential_unavailable", "尚未进行 SMB 登录")]
    [InlineData("smb_credential_decryption_failed", "尚未进行 SMB 登录")]
    [InlineData("smb_key_ring_unavailable", "启动环境")]
    [InlineData("backup_path_invalid", "创建新任务")]
    [InlineData("smb_authentication_failed", "账号所属计算机或域")]
    public void FailureDistinguishesCredentialLoadingFromWindowsLogon(string code, string expected)
        => Assert.Contains(expected, BackupPresentation.FailureReason(code), StringComparison.Ordinal);

    [Fact]
    public void UnknownErrorDoesNotEchoItsContents()
        => Assert.DoesNotContain("synthetic-private", BackupPresentation.FailureReason("synthetic-private"), StringComparison.Ordinal);

    [Theory]
    [InlineData("task.created", "任务已提交，等待 Worker 领取")]
    [InlineData("execution.preparation_failed", "备份路径无效，任务未开始执行")]
    [InlineData("admin_reconciliation_requested", "管理员已请求重新核对")]
    [InlineData("reconciliation.inconclusive", "核对证据不足，继续等待")]
    public void HistoryReasonMapsKnownCodes(string code, string expected)
        => Assert.Equal(expected, BackupPresentation.HistoryReason(code));

    [Fact]
    public void UnknownHistoryReasonDoesNotEchoItsContents()
        => Assert.DoesNotContain("synthetic_unknown", BackupPresentation.HistoryReason("synthetic_unknown"), StringComparison.Ordinal);

    [Theory]
    [InlineData(null, "尚无核对结论")]
    [InlineData("reconciliation.file_missing", "只读核对未找到任务专属备份文件")]
    [InlineData("reconciliation_evidence_insufficient", "核对证据不足，任务继续等待核对")]
    [InlineData("reconciliation.inconclusive", "核对证据不足，任务继续等待核对")]
    [InlineData("admin_reconciliation_requested", "管理员已请求重新核对")]
    public void ReconciliationReasonMapsKnownCodes(string? code, string expected)
        => Assert.Equal(expected, BackupPresentation.ReconciliationReason(code));

    [Fact]
    public void UnknownReconciliationReasonDoesNotEchoItsContents()
        => Assert.DoesNotContain("synthetic_unknown", BackupPresentation.ReconciliationReason("synthetic_unknown"), StringComparison.Ordinal);
}
