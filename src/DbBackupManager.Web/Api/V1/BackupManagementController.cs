using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Web.Authentication;
using DbBackupManager.Web.BackupTasks;
using DbBackupManager.Web.SqlCredentials;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace DbBackupManager.Web.Api.V1;

[ApiController, Route("api/v1"), Authorize(Policy = AdminAuthenticationDefaults.AuthorizationPolicy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(16384)]
[ProducesResponseType<ProblemDetails>(400, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(401, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(403, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(409, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(503, "application/problem+json")]
public sealed class BackupManagementController(IBackupManagementService service) : ControllerBase
{
    [HttpGet("backup-dashboard")]
    [ProducesResponseType<BackupDashboardResponse>(200, "application/json")]
    public Task<IActionResult> List(int page, CancellationToken token) => Execute(async actor =>
    {
        var r = await service.ListAsync(actor, page, token);
        return r.Code == BackupManagementCode.Succeeded ? Ok(new BackupDashboardResponse(
            r.Value!.Databases.Select(x => new BackupDatabaseChoiceResponse(x.Id, x.Label)).ToArray(),
            r.Value.Policies.Select(Map).ToArray(), r.Value.Tasks.Select(Map).ToArray(), r.Value.HasMore)) : Failure(r.Code);
    });
    [HttpPost("manual-backup-policies")]
    [ProducesResponseType<ManualBackupPolicyResponse>(200, "application/json")]
    public Task<IActionResult> Create(SaveManualBackupRequest request, CancellationToken token) => Save(null, request, token);
    [HttpPut("manual-backup-policies/{id:guid}")]
    [ProducesResponseType<ManualBackupPolicyResponse>(200, "application/json")]
    public Task<IActionResult> Update(Guid id, SaveManualBackupRequest request, CancellationToken token) => Save(id, request, token);
    private Task<IActionResult> Save(Guid? id, SaveManualBackupRequest r, CancellationToken token) => Execute(async actor =>
    {
        var result = await service.SavePolicyAsync(actor, id, r.Version, new(r.DatabaseId, r.Name, r.RetentionDays,
            r.BackupTimeoutMinutes, r.VerifyTimeoutMinutes, r.UseCompression, r.IsEnabled, r.StorageMode,
            r.StorageTargetId, r.RemoteRetentionDays, r.TransferTimeoutMinutes), token);
        return result.Code == BackupManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
    });
    [HttpPost("manual-backup-policies/{id:guid}/run")]
    [ProducesResponseType<BackupTaskCommandResponse>(200, "application/json")]
    public Task<IActionResult> Run(Guid id, BackupTaskCommandRequest request, CancellationToken token) => Execute(async actor => Reply(await service.StartAsync(actor, id, request.RequestId, token)));
    [HttpPost("backup-tasks/{id:guid}/cancel")]
    [ProducesResponseType<BackupTaskCommandResponse>(200, "application/json")]
    public Task<IActionResult> Cancel(Guid id, BackupTaskCommandRequest request, CancellationToken token) => Execute(async actor => Reply(await service.CancelAsync(actor, id, request.RequestId, token)));
    [HttpPost("backup-tasks/{id:guid}/retry")]
    [ProducesResponseType<BackupTaskCommandResponse>(200, "application/json")]
    public Task<IActionResult> Retry(Guid id, BackupTaskCommandRequest request, CancellationToken token) => Execute(async actor => Reply(await service.RetryAsync(actor, id, request.RequestId, token)));
    [HttpPost("backup-tasks/{id:guid}/reconcile")]
    [ProducesResponseType<BackupTaskCommandResponse>(200, "application/json")]
    public Task<IActionResult> Reconcile(Guid id, BackupTaskCommandRequest request, CancellationToken token) => Execute(async actor => Reply(await service.RequestReconciliationAsync(actor, id, request.RequestId, token)));
    [HttpPost("backup-tasks/{id:guid}/confirm-failed")]
    [ProducesResponseType<BackupTaskCommandResponse>(200, "application/json")]
    public Task<IActionResult> ConfirmFailed(Guid id, ConfirmBackupTaskFailedRequest request, CancellationToken token) => Execute(async actor => Reply(await service.ConfirmFailedAsync(actor, id, request.RequestId, request.EvidenceReviewed, token)));
    [HttpGet("backup-tasks/{id:guid}")]
    [ProducesResponseType<BackupTaskDetailResponse>(200, "application/json")]
    public Task<IActionResult> Detail(Guid id, CancellationToken token) => Execute(async actor =>
    {
        var r = await service.DetailAsync(actor, id, token);
        return r.Code == BackupManagementCode.Succeeded ? Ok(new BackupTaskDetailResponse(Map(r.Value!.Task),
            r.Value.History.Select(x => new BackupTaskHistoryResponse(x.AtUtc, x.Status, x.Stage, x.Reason)).ToArray(),
            r.Value.Attempts.Select(x => new BackupAttemptSummaryResponse(x.Number, x.InvocationStatus, x.Length, x.VerifiedAtUtc)).ToArray(),
            r.Value.VerifiedSqlPath,
            new BackupReconciliationResponse(r.Value.Reconciliation.AttemptCount, r.Value.Reconciliation.NextAtUtc,
                r.Value.Reconciliation.ReasonCode, r.Value.Reconciliation.CanRequest),
            r.Value.Files.Select(MapFile).ToArray())) : Failure(r.Code);
    });
    private IActionResult Reply(BackupManagementResult<Guid> r) => r.Code == BackupManagementCode.Succeeded ? Ok(new BackupTaskCommandResponse(r.Value)) : Failure(r.Code);
    private Task<IActionResult> Execute(Func<AdminSession, Task<IActionResult>> action)
    { var actor = SqlCredentialPresentation.GetActor(User); return actor is null ? Task.FromResult<IActionResult>(Failure(BackupManagementCode.AuthenticationRequired)) : action(actor); }
    private ObjectResult Failure(BackupManagementCode code)
    { var e = BackupPresentation.Error(code); return StatusCode(e.Status, ApiProblemDetails.Create(HttpContext, e.Status, e.Code, e.Message)); }
    private static ManualBackupPolicyResponse Map(ManualBackupPolicy x) => new(x.Id, new(x.Settings.DatabaseId, x.Settings.Name,
        x.Settings.RetentionDays, x.Settings.BackupTimeoutMinutes, x.Settings.VerifyTimeoutMinutes, x.Settings.UseCompression,
        x.Settings.IsEnabled, x.Settings.StorageMode, x.Settings.StorageTargetId, x.Settings.RemoteRetentionDays,
        x.Settings.TransferTimeoutMinutes), x.Version);
    private static BackupTaskSummaryResponse Map(BackupTaskSummary x) => new(x.Id, x.PolicyName, x.DatabaseName, x.Status, x.Stage,
        x.CreatedAtUtc, x.CompletedAtUtc, x.ErrorCode, x.CancellationRequested, x.FileLength);
    private static BackupFileSummaryResponse MapFile(BackupFileSummary x) => new(x.TaskId, x.DatabaseId, x.DatabaseName,
        x.InstanceName, x.PolicyName, x.SqlPath, x.LengthBytes, x.VerifiedAtUtc, x.RetentionDays, x.FileId, x.Location,
        x.Protocol, x.TargetDisplayName, x.Status, x.DeletedAtUtc, x.ErrorCode, x.Path);
}
