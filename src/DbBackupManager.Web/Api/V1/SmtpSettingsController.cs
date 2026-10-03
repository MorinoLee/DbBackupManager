using DbBackupManager.Application.Notifications;
using DbBackupManager.Application.Servers;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Web.Authentication;
using DbBackupManager.Web.Notifications;
using DbBackupManager.Web.SqlCredentials;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbBackupManager.Web.Api.V1;

[ApiController]
[Route("api/v1/notifications/smtp")]
[Authorize(Policy = AdminAuthenticationDefaults.AuthorizationPolicy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(32768)]
[ProducesResponseType<ProblemDetails>(400, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(401, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(403, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(409, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(503, "application/problem+json")]
public sealed class SmtpSettingsController(ISmtpSettingsService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<SmtpSettingsResponse>(200, "application/json")]
    public Task<IActionResult> Get(CancellationToken token) => Execute(async actor =>
    {
        var result = await service.GetAsync(actor, token);
        return result.Code == ManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
    });

    [HttpPut]
    [ProducesResponseType<SmtpSettingsResponse>(200, "application/json")]
    public Task<IActionResult> Save(SaveSmtpSettingsRequest request, CancellationToken token) =>
        Execute(async actor =>
        {
            var result = await service.SaveAsync(
                actor,
                request.Version,
                new SmtpSettingsInput(
                    request.Host,
                    request.Port,
                    (int)request.SecurityMode,
                    request.FromAddress,
                    request.TimeoutSeconds,
                    request.IsEnabled),
                token);
            return result.Code == ManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
        });

    [HttpPost("enabled")]
    [ProducesResponseType<SmtpSettingsResponse>(200, "application/json")]
    public Task<IActionResult> SetEnabled(SetSmtpEnabledRequest request, CancellationToken token) =>
        Execute(async actor =>
        {
            var result = await service.SetEnabledAsync(actor, request.Version, request.IsEnabled, token);
            return result.Code == ManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
        });

    [HttpPost("password")]
    [RequestSizeLimit(16_384)]
    [ProducesResponseType<SmtpSettingsResponse>(200, "application/json")]
    public Task<IActionResult> RotatePassword(RotateSmtpPasswordRequest request, CancellationToken token) =>
        Execute(async actor =>
        {
            var result = await service.RotatePasswordAsync(
                actor, request.Version, request.Username, request.Password, token);
            return result.Code == ManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
        });

    [HttpPost("password/clear")]
    [ProducesResponseType<SmtpSettingsResponse>(200, "application/json")]
    public Task<IActionResult> ClearPassword(SmtpVersionRequest request, CancellationToken token) =>
        Execute(async actor =>
        {
            var result = await service.ClearPasswordAsync(actor, request.Version, token);
            return result.Code == ManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
        });

    [HttpPost("recipients")]
    [ProducesResponseType<SmtpSettingsResponse>(200, "application/json")]
    public Task<IActionResult> AddRecipient(AddSmtpRecipientRequest request, CancellationToken token) =>
        Execute(async actor =>
        {
            var result = await service.AddRecipientAsync(actor, request.Version, request.Address, token);
            return result.Code == ManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
        });

    [HttpPost("recipients/remove")]
    [ProducesResponseType<SmtpSettingsResponse>(200, "application/json")]
    public Task<IActionResult> RemoveRecipient(RemoveSmtpRecipientRequest request, CancellationToken token) =>
        Execute(async actor =>
        {
            var result = await service.RemoveRecipientAsync(actor, request.Version, request.RecipientId, token);
            return result.Code == ManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
        });

    [HttpPost("test")]
    [ProducesResponseType<SmtpSettingsResponse>(200, "application/json")]
    public Task<IActionResult> QueueTest(SendSmtpTestRequest request, CancellationToken token) =>
        Execute(async actor =>
        {
            var result = await service.QueueTestAsync(actor, request.Version, request.RequestId, token);
            return result.Code == ManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
        });

    private Task<IActionResult> Execute(
        Func<DbBackupManager.Application.Identity.AdminSession, Task<IActionResult>> action)
    {
        var actor = SqlCredentialPresentation.GetActor(User);
        return actor is null
            ? Task.FromResult<IActionResult>(Failure(ManagementCode.AuthenticationRequired))
            : action(actor);
    }

    private ObjectResult Failure(ManagementCode code)
    {
        var error = SmtpSettingsPresentation.Error(code);
        return StatusCode(error.Status, ApiProblemDetails.Create(HttpContext, error.Status, error.Code, error.Message));
    }

    private static SmtpSettingsResponse Map(SmtpSettingsItem item) => new(
        item.Id,
        item.Host,
        item.Port,
        (SmtpSecurityModeValue)item.SecurityMode,
        item.FromAddress,
        item.TimeoutSeconds,
        item.IsEnabled,
        item.HasCredential,
        item.Recipients.Select(recipient => new SmtpRecipientResponse(recipient.Id, recipient.Address)).ToArray(),
        item.Version);
}
