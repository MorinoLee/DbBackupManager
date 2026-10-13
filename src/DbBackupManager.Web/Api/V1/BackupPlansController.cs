using DbBackupManager.Application.BackupPlans;
using DbBackupManager.Application.Identity;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Web.Authentication;
using DbBackupManager.Web.BackupPlans;
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
public sealed class BackupPlansController(IBackupPlanCatalogService service) : ControllerBase
{
    [HttpGet("databases/{databaseId:guid}/backup-plan")]
    [ProducesResponseType<BackupPlanResponse>(200, "application/json")]
    public Task<IActionResult> GetByDatabase(Guid databaseId, CancellationToken token) => Execute(async actor =>
    {
        var result = await service.GetByDatabaseAsync(actor, databaseId, token);
        return result.Code == BackupPlanCatalogCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result);
    });

    [HttpGet("backup-plans/{id:guid}")]
    [ProducesResponseType<BackupPlanResponse>(200, "application/json")]
    public Task<IActionResult> Get(Guid id, CancellationToken token) => Execute(async actor =>
    {
        var result = await service.GetAsync(actor, id, token);
        return result.Code == BackupPlanCatalogCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result);
    });

    [HttpPost("backup-plans")]
    [ProducesResponseType<BackupPlanResponse>(200, "application/json")]
    public Task<IActionResult> Create(SaveBackupPlanRequest request, CancellationToken token) =>
        Save(null, request, token);

    [HttpPut("backup-plans/{id:guid}")]
    [ProducesResponseType<BackupPlanResponse>(200, "application/json")]
    public Task<IActionResult> Update(Guid id, SaveBackupPlanRequest request, CancellationToken token) =>
        Save(id, request, token);

    [HttpGet("databases/{databaseId:guid}/backup-recovery")]
    [ProducesResponseType<BackupRecoverySummaryResponse>(200, "application/json")]
    public Task<IActionResult> Recovery(Guid databaseId, CancellationToken token) => Execute(async actor =>
    {
        var result = await service.RecoveryAsync(actor, databaseId, token);
        if (result.Code != BackupPlanCatalogCode.Succeeded)
        {
            return Failure(result);
        }

        var value = result.Value!;
        return Ok(new BackupRecoverySummaryResponse(
            value.DatabaseId,
            value.HasCurrentPlan,
            value.Locations.Select(item => new BackupLocationRecoveryResponse(
                item.Location.ToString(),
                item.StorageTargetId,
                item.WindowDays,
                item.Conclusion.ToString(),
                item.Baseline,
                item.ManagedBaselineBackupSetId,
                item.Unknown,
                item.RemoteUnrecoverable,
                item.GapReasonCodes,
                item.UnknownReasonCodes)).ToArray()));
    });

    private Task<IActionResult> Save(Guid? id, SaveBackupPlanRequest request, CancellationToken token) => Execute(async actor =>
    {
        var input = new BackupPlanInput(
            request.DatabaseId,
            request.Name,
            request.Mode,
            new(request.FullSchedule.ScheduleType, request.FullSchedule.LocalTime, request.FullSchedule.DaysOfWeek),
            request.DifferentialSchedule is null
                ? null
                : new(request.DifferentialSchedule.ScheduleType, request.DifferentialSchedule.LocalTime, request.DifferentialSchedule.DaysOfWeek),
            request.TimeZoneId,
            request.StorageMode,
            request.StorageTargetId,
            request.LocalRecoveryWindowDays,
            request.RemoteRecoveryWindowDays,
            request.UseChecksum,
            request.UseCompression,
            request.BackupTimeoutMinutes,
            request.VerifyTimeoutMinutes,
            request.TransferTimeoutMinutes,
            request.IsPaused);
        var result = id is null
            ? await service.CreateAsync(actor, input, token)
            : await service.UpdateAsync(actor, id.Value, request.Version, input, token);
        return result.Code == BackupPlanCatalogCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result);
    });

    private Task<IActionResult> Execute(Func<AdminSession, Task<IActionResult>> action)
    {
        var actor = SqlCredentialPresentation.GetActor(User);
        return actor is null
            ? Task.FromResult<IActionResult>(Failure(new BackupPlanCatalogResult<object>(BackupPlanCatalogCode.AuthenticationRequired)))
            : action(actor);
    }

    private ObjectResult Failure<T>(BackupPlanCatalogResult<T> result)
    {
        var error = BackupPlanPresentation.Error(result.Code);
        var problem = ApiProblemDetails.Create(HttpContext, error.Status, error.Code, error.Message);
        if (result.ExistingPlanId is Guid planId)
        {
            problem.Extensions["planId"] = planId;
        }

        return StatusCode(error.Status, problem);
    }

    private static BackupPlanResponse Map(BackupPlanView plan) => new(
        plan.Id,
        plan.DatabaseId,
        plan.Name,
        plan.IsPaused,
        plan.Version,
        plan.CurrentVersionId,
        plan.VersionNumber,
        (int)plan.Mode,
        new((int)plan.FullSchedule.ScheduleType, plan.FullSchedule.LocalTime, (int)plan.FullSchedule.DaysOfWeek),
        plan.DifferentialSchedule is { } differential
            ? new((int)differential.ScheduleType, differential.LocalTime, (int)differential.DaysOfWeek)
            : null,
        plan.TimeZoneId,
        (int)plan.StorageMode,
        plan.StorageTargetId,
        plan.LocalRecoveryWindowDays,
        plan.RemoteRecoveryWindowDays,
        plan.UseChecksum,
        plan.UseCompression,
        plan.BackupTimeoutMinutes,
        plan.VerifyTimeoutMinutes,
        plan.TransferTimeoutMinutes,
        plan.EffectiveFromUtc,
        plan.Notices);
}
