using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace DbBackupManager.Web.BackupTasks;

/// <summary>公开任务状态字符串的取值与语义，包含新的“结果未知已结束”终态。</summary>
internal sealed class BackupStatusOpenApiTransformer : IOpenApiDocumentTransformer
{
    internal const string StatusDescription =
        "任务运行状态。取值：Pending、Running、NeedsAttention、Succeeded、Failed、Cancelled、ClosedUnknown。"
        + "ClosedUnknown 表示原调用已独立证明终止，但成功证据不足、缺失或矛盾：执行已结束、备份结果未知，"
        + "不是明确失败，也不是未调用；系统保留原 Attempt、证据与文件，并继续保守保留恢复与 Retention 资格。";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (document.Components?.Schemas is not null
            && document.Components.Schemas.TryGetValue("BackupTaskSummaryResponse", out var schema)
            && schema is OpenApiSchema summary
            && summary.Properties is not null
            && summary.Properties.TryGetValue("status", out var property)
            && property is OpenApiSchema status)
        {
            status.Description = StatusDescription;
        }

        return Task.CompletedTask;
    }
}
