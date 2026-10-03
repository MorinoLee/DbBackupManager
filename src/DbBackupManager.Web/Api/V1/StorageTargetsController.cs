using DbBackupManager.Application.Servers;
using DbBackupManager.Application.StorageTargets;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Web.Authentication;
using DbBackupManager.Web.SqlCredentials;
using DbBackupManager.Web.StorageTargets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbBackupManager.Web.Api.V1;

[ApiController]
[Route("api/v1/storage-targets")]
[Authorize(Policy = AdminAuthenticationDefaults.AuthorizationPolicy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(32768)]
[ProducesResponseType<ProblemDetails>(400, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(401, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(403, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(409, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(422, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(503, "application/problem+json")]
public sealed class StorageTargetsController(IStorageTargetManagementService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<StorageTargetResponse[]>(200, "application/json")]
    public Task<IActionResult> List(CancellationToken token) => Execute(async actor =>
    {
        var result = await service.ListAsync(actor, token);
        return result.Code == ManagementCode.Succeeded
            ? Ok(result.Value!.Select(Map).ToArray())
            : Failure(result.Code);
    });

    [HttpPost]
    [ProducesResponseType<StorageTargetResponse>(200, "application/json")]
    public Task<IActionResult> Create(SaveStorageTargetRequest request, CancellationToken token) =>
        Save(null, request, token);

    [HttpPut("{id:guid}")]
    [ProducesResponseType<StorageTargetResponse>(200, "application/json")]
    public Task<IActionResult> Update(Guid id, SaveStorageTargetRequest request, CancellationToken token) =>
        Save(id, request, token);

    [HttpPost("{id:guid}/enabled")]
    [ProducesResponseType<StorageTargetResponse>(200, "application/json")]
    public Task<IActionResult> SetEnabled(Guid id, SetStorageTargetEnabledRequest request, CancellationToken token) =>
        Execute(async actor =>
        {
            var result = await service.SetEnabledAsync(actor, id, request.Version, request.IsEnabled, token);
            return result.Code == ManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
        });

    [HttpPost("{id:guid}/probe")]
    [ProducesResponseType<StorageTargetResponse>(200, "application/json")]
    public Task<IActionResult> Probe(Guid id, StorageTargetVersionRequest request, CancellationToken token) =>
        Execute(async actor =>
        {
            var result = await service.TestConnectionAsync(actor, id, request.Version, token);
            return result.Code == ManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
        });

    private Task<IActionResult> Save(Guid? id, SaveStorageTargetRequest request, CancellationToken token) =>
        Execute(async actor =>
        {
            var result = await service.SaveAsync(actor, id, request.Version,
                new(request.Name, (int)request.Protocol, request.Host, request.Port, request.BasePath,
                    request.CredentialId, request.Fingerprint, request.IsEnabled), token);
            return result.Code == ManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
        });

    private Task<IActionResult> Execute(Func<DbBackupManager.Application.Identity.AdminSession, Task<IActionResult>> action)
    {
        var actor = SqlCredentialPresentation.GetActor(User);
        return actor is null
            ? Task.FromResult<IActionResult>(Failure(ManagementCode.AuthenticationRequired))
            : action(actor);
    }

    private ObjectResult Failure(ManagementCode code)
    {
        var error = StorageTargetPresentation.Error(code);
        return StatusCode(error.Status, ApiProblemDetails.Create(HttpContext, error.Status, error.Code, error.Message));
    }

    private static StorageTargetResponse Map(StorageTargetItem item) => new(
        item.Id,
        new(item.Settings.Name, (StorageProtocolValue)item.Settings.Protocol, item.Settings.Host, item.Settings.Port,
            item.Settings.BasePath, item.Settings.CredentialId, item.Settings.Fingerprint, item.Settings.IsEnabled),
        item.Version);
}
