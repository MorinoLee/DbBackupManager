using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Web.BackupPlans;

namespace DbBackupManager.Web.Tests.Components;

public sealed class BackupPlanPresentationTests
{
    [Theory]
    [InlineData("outside_window_unneeded", "已超出恢复窗口，且不再被恢复依赖需要")]
    [InlineData("remote_base_unconfirmed", "远端还没有确认基准完整备份，暂时不能恢复")]
    [InlineData("remote_dependent_unconfirmed", "依赖这份备份的差异备份尚未在远端确认")]
    [InlineData("remote_staging_deferred", "仅远程任务的本地暂存仍需保留")]
    [InlineData("remote_staging_releasable", "远端恢复依赖已确认，本地暂存可以释放")]
    [InlineData("dependency_unproven", "恢复依赖尚未证明，不能删除")]
    [InlineData("artifact.owner_unknown", "副本归属尚未证明，不能删除")]
    [InlineData("window_unknown", "恢复窗口无法确定，文件继续受保护")]
    [InlineData("baseline_unresolved", "活动基线尚未确定，相关完整备份继续保留")]
    [InlineData("active_differential_baseline", "这是当前差异备份的活动基线，继续保留")]
    public void RetentionReasonsStayInChinese(string reason, string expected) =>
        Assert.Equal(expected, BackupPlanPresentation.RetentionReason(reason));

    [Fact]
    public void UnknownFactsAndUnmappedReasonsDoNotEchoRawCodes()
    {
        Assert.Equal("未知", BackupPlanPresentation.Fact("Unknown"));
        Assert.Equal("未知", BackupPlanPresentation.Fact("not-a-status"));
        Assert.Equal("保留原因尚未分类，文件继续受保护", BackupPlanPresentation.RetentionReason("synthetic_reason"));
        Assert.DoesNotContain("synthetic_reason", BackupPlanPresentation.RetentionReason("synthetic_reason"), StringComparison.Ordinal);
        Assert.Equal("备份计划已被其他人修改，请刷新后重试。", BackupPlanPresentation.Error(BackupPlanCatalogCode.VersionConflict).Message);
        Assert.Equal("该数据库已有当前备份计划（含已暂停）。请改用现有计划。", BackupPlanPresentation.Error(BackupPlanCatalogCode.AlreadyExists).Message);
        Assert.Equal("首版不接受包含日志备份的计划，也不会修改数据库恢复模式。", BackupPlanPresentation.Error(BackupPlanCatalogCode.LogUnsupported).Message);
    }
}
