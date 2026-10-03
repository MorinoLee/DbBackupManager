using DbBackupManager.Application.BackupTasks;
using DbBackupManager.Application.Identity;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Web.Authentication;
using DbBackupManager.Web.BackupTasks;
using DbBackupManager.Web.SqlCredentials;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbBackupManager.Web.Api.V1;

[ApiController, Route("api/v1/scheduled-backup-policies"), Authorize(Policy = AdminAuthenticationDefaults.AuthorizationPolicy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(16384)]
[ProducesResponseType<ProblemDetails>(400, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(401, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(403, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(409, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(503, "application/problem+json")]
public sealed class ScheduledBackupPolicyController(IScheduledBackupPolicyService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ScheduledBackupDashboardResponse>(200, "application/json")]
    public Task<IActionResult> List(CancellationToken token) => Execute(async actor =>
    {
        var result = await service.ListAsync(actor, token);
        return result.Code == BackupManagementCode.Succeeded
            ? Ok(new ScheduledBackupDashboardResponse(
                result.Value!.Databases.Select(x => new BackupDatabaseChoiceResponse(x.Id, x.Label)).ToArray(),
                result.Value.Policies.Select(Map).ToArray()))
            : Failure(result.Code);
    });

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ScheduledBackupPolicyResponse>(200, "application/json")]
    public Task<IActionResult> Get(Guid id, CancellationToken token) => Execute(async actor =>
    {
        var result = await service.GetAsync(actor, id, token);
        return result.Code == BackupManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
    });

    [HttpPost]
    [ProducesResponseType<ScheduledBackupPolicyResponse>(200, "application/json")]
    public Task<IActionResult> Create(SaveScheduledBackupRequest request, CancellationToken token) =>
        Save(null, request, token);

    [HttpPut("{id:guid}")]
    [ProducesResponseType<ScheduledBackupPolicyResponse>(200, "application/json")]
    public Task<IActionResult> Update(Guid id, SaveScheduledBackupRequest request, CancellationToken token) =>
        Save(id, request, token);

    private Task<IActionResult> Save(Guid? id, SaveScheduledBackupRequest request, CancellationToken token) =>
        Execute(async actor =>
        {
            var result = await service.SaveAsync(actor, id, request.Version, new ScheduledBackupPolicyInput(
                request.DatabaseId,
                request.Name,
                request.ScheduleType,
                request.LocalTime,
                request.DaysOfWeek,
                request.TimeZoneId,
                request.StorageMode,
                request.StorageTargetId,
                request.LocalRetentionDays,
                request.RemoteRetentionDays,
                request.BackupTimeoutMinutes,
                request.VerifyTimeoutMinutes,
                request.TransferTimeoutMinutes,
                request.UseCompression,
                request.IsEnabled), token);
            return result.Code == BackupManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
        });

    private Task<IActionResult> Execute(Func<AdminSession, Task<IActionResult>> action)
    {
        var actor = SqlCredentialPresentation.GetActor(User);
        return actor is null
            ? Task.FromResult<IActionResult>(Failure(BackupManagementCode.AuthenticationRequired))
            : action(actor);
    }

    private ObjectResult Failure(BackupManagementCode code)
    {
        var error = BackupPresentation.Error(code);
        return StatusCode(error.Status, ApiProblemDetails.Create(HttpContext, error.Status, error.Code, error.Message));
    }

    private static ScheduledBackupPolicyResponse Map(ScheduledBackupPolicy policy) =>
        new(
            policy.Id,
            new(
                policy.Settings.DatabaseId,
                policy.Settings.Name,
                policy.Settings.ScheduleType,
                policy.Settings.LocalTime,
                policy.Settings.DaysOfWeek,
                policy.Settings.TimeZoneId,
                policy.Settings.StorageMode,
                policy.Settings.StorageTargetId,
                policy.Settings.LocalRetentionDays,
                policy.Settings.RemoteRetentionDays,
                policy.Settings.BackupTimeoutMinutes,
                policy.Settings.VerifyTimeoutMinutes,
                policy.Settings.TransferTimeoutMinutes,
                policy.Settings.UseCompression,
                policy.Settings.IsEnabled),
            policy.Version,
            policy.ScheduleEffectiveFromUtc,
            policy.NextSlotUtc,
            policy.NextSlotLocal,
            policy.NextSlotTimeZoneId);
}
