using DbBackupManager.Application.SqlCredentials;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Web.Authentication;
using DbBackupManager.Web.SqlCredentials;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbBackupManager.Web.Api.V1;

[ApiController]
[Route("api/v1/sql-credentials")]
[Authorize(Policy = AdminAuthenticationDefaults.AuthorizationPolicy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType<ProblemDetails>(400, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(401, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(403, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(409, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(503, "application/problem+json")]
public sealed class SqlCredentialsController(ISqlCredentialService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<SqlCredentialResponse[]>(200, "application/json")]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var actor = SqlCredentialPresentation.GetActor(User);
        if (actor is null) { return Failure(SqlCredentialResultCode.AuthenticationRequired); }
        var result = await service.ListAsync(actor, cancellationToken);
        return result.Code == SqlCredentialResultCode.Succeeded
            ? Ok(result.Value!.Select(Map).ToArray()) : Failure(result.Code);
    }

    [HttpPost]
    [RequestSizeLimit(16_384)]
    [ProducesResponseType<SqlCredentialResponse>(200, "application/json")]
    public async Task<IActionResult> Create(CreateSqlCredentialRequest request, CancellationToken cancellationToken)
    {
        var actor = SqlCredentialPresentation.GetActor(User);
        if (actor is null) { return Failure(SqlCredentialResultCode.AuthenticationRequired); }
        var result = await service.CreateAsync(actor, request.Name, request.Username, request.Password, cancellationToken);
        return Reply(result);
    }

    [HttpPost("{id:guid}/password")]
    [RequestSizeLimit(16_384)]
    [ProducesResponseType<SqlCredentialResponse>(200, "application/json")]
    public async Task<IActionResult> Rotate(Guid id, RotateSqlCredentialPasswordRequest request, CancellationToken cancellationToken)
    {
        var actor = SqlCredentialPresentation.GetActor(User);
        if (actor is null) { return Failure(SqlCredentialResultCode.AuthenticationRequired); }
        return Reply(await service.RotatePasswordAsync(actor, id, request.Version, request.Password, cancellationToken));
    }

    [HttpPost("{id:guid}/enabled")]
    [ProducesResponseType<SqlCredentialResponse>(200, "application/json")]
    public async Task<IActionResult> SetEnabled(Guid id, SetSqlCredentialEnabledRequest request, CancellationToken cancellationToken)
    {
        var actor = SqlCredentialPresentation.GetActor(User);
        if (actor is null) { return Failure(SqlCredentialResultCode.AuthenticationRequired); }
        return Reply(await service.SetEnabledAsync(actor, id, request.Version, request.IsEnabled, cancellationToken));
    }

    private IActionResult Reply(SqlCredentialResult<SqlCredentialItem> result) =>
        result.Code == SqlCredentialResultCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);

    private ObjectResult Failure(SqlCredentialResultCode code)
    {
        var error = SqlCredentialPresentation.Error(code);
        return StatusCode(error.Status, ApiProblemDetails.Create(HttpContext, error.Status, error.Code, error.Message));
    }

    private static SqlCredentialResponse Map(SqlCredentialItem item) =>
        new(item.Id, item.Name, item.Username, item.IsEnabled, item.Version);
}
