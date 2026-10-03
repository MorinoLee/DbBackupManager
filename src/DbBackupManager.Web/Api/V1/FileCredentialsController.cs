using DbBackupManager.Application.FileCredentials;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Web.Authentication;
using DbBackupManager.Web.FileCredentials;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbBackupManager.Web.Api.V1;

[ApiController]
[Route("api/v1/file-credentials")]
[Authorize(Policy = AdminAuthenticationDefaults.AuthorizationPolicy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType<ProblemDetails>(400, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(401, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(403, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(409, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(503, "application/problem+json")]
public sealed class FileCredentialsController(IFileCredentialService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<FileCredentialResponse[]>(200, "application/json")]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var actor = FileCredentialPresentation.GetActor(User);
        if (actor is null) { return Failure(FileCredentialResultCode.AuthenticationRequired); }
        var result = await service.ListAsync(actor, cancellationToken);
        return result.Code == FileCredentialResultCode.Succeeded
            ? Ok(result.Value!.Select(Map).ToArray()) : Failure(result.Code);
    }

    [HttpPost]
    [RequestSizeLimit(16_384)]
    [ProducesResponseType<FileCredentialResponse>(200, "application/json")]
    public async Task<IActionResult> Create(CreateFileCredentialRequest request, CancellationToken cancellationToken)
    {
        var actor = FileCredentialPresentation.GetActor(User);
        if (actor is null) { return Failure(FileCredentialResultCode.AuthenticationRequired); }
        var result = await service.CreateAsync(actor, request.Name, request.Username, (FileCredentialKind)request.Kind, request.Password, cancellationToken);
        return Reply(result);
    }

    [HttpPost("{id:guid}/password")]
    [RequestSizeLimit(16_384)]
    [ProducesResponseType<FileCredentialResponse>(200, "application/json")]
    public async Task<IActionResult> Rotate(Guid id, RotateFileCredentialPasswordRequest request, CancellationToken cancellationToken)
    {
        var actor = FileCredentialPresentation.GetActor(User);
        if (actor is null) { return Failure(FileCredentialResultCode.AuthenticationRequired); }
        return Reply(await service.RotatePasswordAsync(actor, id, request.Version, request.Password, cancellationToken));
    }

    [HttpPost("{id:guid}/enabled")]
    [ProducesResponseType<FileCredentialResponse>(200, "application/json")]
    public async Task<IActionResult> SetEnabled(Guid id, SetFileCredentialEnabledRequest request, CancellationToken cancellationToken)
    {
        var actor = FileCredentialPresentation.GetActor(User);
        if (actor is null) { return Failure(FileCredentialResultCode.AuthenticationRequired); }
        return Reply(await service.SetEnabledAsync(actor, id, request.Version, request.IsEnabled, cancellationToken));
    }

    [HttpPost("private-key")]
    [RequestSizeLimit(65_536)]
    [ProducesResponseType<FileCredentialResponse>(200, "application/json")]
    public async Task<IActionResult> CreatePrivateKey(CreateSftpPrivateKeyRequest request, CancellationToken cancellationToken)
    {
        var actor = FileCredentialPresentation.GetActor(User);
        if (actor is null) { return Failure(FileCredentialResultCode.AuthenticationRequired); }
        return Reply(await service.CreatePrivateKeyAsync(
            actor, request.Name, request.Username, request.PrivateKey, request.Passphrase, cancellationToken));
    }

    [HttpPost("{id:guid}/private-key")]
    [RequestSizeLimit(65_536)]
    [ProducesResponseType<FileCredentialResponse>(200, "application/json")]
    public async Task<IActionResult> RotatePrivateKey(Guid id, RotateSftpPrivateKeyRequest request, CancellationToken cancellationToken)
    {
        var actor = FileCredentialPresentation.GetActor(User);
        if (actor is null) { return Failure(FileCredentialResultCode.AuthenticationRequired); }
        return Reply(await service.RotatePrivateKeyAsync(
            actor, id, request.Version, request.PrivateKey, request.Passphrase, cancellationToken));
    }

    private IActionResult Reply(FileCredentialResult<FileCredentialItem> result) =>
        result.Code == FileCredentialResultCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);

    private ObjectResult Failure(FileCredentialResultCode code)
    {
        var error = FileCredentialPresentation.Error(code);
        return StatusCode(error.Status, ApiProblemDetails.Create(HttpContext, error.Status, error.Code, error.Message));
    }

    private static FileCredentialResponse Map(FileCredentialItem item) =>
        new(item.Id, item.Name, item.Username, (FileCredentialKindValue)item.Kind, item.IsEnabled, item.Version,
            item.HasPassphrase);
}
