using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Web.Authentication;
using DbBackupManager.Web.BackupTasks;
using DbBackupManager.Web.SqlCredentials;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbBackupManager.Web.Api.V1;

[ApiController, Route("api/v1"), Authorize(Policy = AdminAuthenticationDefaults.AuthorizationPolicy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType<ProblemDetails>(400, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(401, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(403, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(503, "application/problem+json")]
public sealed class BackupMonitoringController(IBackupMonitoringService service) : ControllerBase
{
    [HttpGet("backup-overview")]
    [ProducesResponseType<BackupOverviewResponse>(200, "application/json")]
    public async Task<IActionResult> Overview(CancellationToken token)
    {
        var actor = SqlCredentialPresentation.GetActor(User);
        if (actor is null) return Failure(BackupManagementCode.AuthenticationRequired);
        var result = await service.OverviewAsync(actor, token);
        if (result.Code != BackupManagementCode.Succeeded) return Failure(result.Code);
        var x = result.Value!;
        return Ok(new BackupOverviewResponse(x.GeneratedAtUtc, x.RecentFromUtc, x.ManagedDatabases, x.WithoutEnabledPolicy,
            x.NeverVerified, x.WithVerifiedFile, x.PendingTasks, x.RunningTasks, x.NeedsAttentionTasks, x.RecentFailedTasks,
            new(x.Worker.Status, x.Worker.LastSeenAtUtc, x.Worker.OfflineAfterSeconds),
            x.RecentTasks.Select(Map).ToArray(), x.RecentFailures.Select(Map).ToArray(), x.RecentFiles.Select(Map).ToArray()));
    }

    [HttpGet("backup-tasks")]
    [ProducesResponseType<BackupTaskPageResponse>(200, "application/json")]
    public async Task<IActionResult> Tasks([FromQuery] BackupSearchRequest request, CancellationToken token)
    {
        var actor = SqlCredentialPresentation.GetActor(User);
        if (actor is null) return Failure(BackupManagementCode.AuthenticationRequired);
        var result = await service.TasksAsync(actor, Map(request), token);
        if (result.Code != BackupManagementCode.Succeeded) return Failure(result.Code);
        var x = result.Value!;
        return Ok(new BackupTaskPageResponse(x.Items.Select(Map).ToArray(), x.Page, x.TotalCount, x.PageSize, x.HasMore));
    }

    [HttpGet("backup-files")]
    [ProducesResponseType<BackupFilePageResponse>(200, "application/json")]
    public async Task<IActionResult> Files([FromQuery] BackupSearchRequest request, CancellationToken token)
    {
        var actor = SqlCredentialPresentation.GetActor(User);
        if (actor is null) return Failure(BackupManagementCode.AuthenticationRequired);
        var result = await service.FilesAsync(actor, Map(request), token);
        if (result.Code != BackupManagementCode.Succeeded) return Failure(result.Code);
        var x = result.Value!;
        return Ok(new BackupFilePageResponse(x.Items.Select(Map).ToArray(), x.Page, x.TotalCount, x.PageSize, x.HasMore));
    }

    private ObjectResult Failure(BackupManagementCode code)
    {
        var e = BackupPresentation.Error(code);
        var message = code == BackupManagementCode.Invalid ? "查询条件无效，请检查页码、状态和时间范围。" : e.Message;
        return StatusCode(e.Status, ApiProblemDetails.Create(HttpContext, e.Status, e.Code, message));
    }
    private static BackupSearch Map(BackupSearchRequest x) => new(x.Page, x.Search, x.Status, x.FromUtc, x.UntilUtc, x.OldestFirst);
    private static BackupTaskSummaryResponse Map(BackupTaskSummary x) => new(x.Id, x.PolicyName, x.DatabaseName, x.Status, x.Stage,
        x.CreatedAtUtc, x.CompletedAtUtc, x.ErrorCode, x.CancellationRequested, x.FileLength);
    private static BackupFileSummaryResponse Map(BackupFileSummary x) => new(x.TaskId, x.DatabaseId, x.DatabaseName, x.InstanceName,
        x.PolicyName, x.SqlPath, x.LengthBytes, x.VerifiedAtUtc, x.RetentionDays, x.FileId, x.Location, x.Protocol,
        x.TargetDisplayName, x.Status, x.DeletedAtUtc, x.ErrorCode, x.Path);
}
