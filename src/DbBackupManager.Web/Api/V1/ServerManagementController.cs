using DbBackupManager.Application.Servers;
using DbBackupManager.Contracts.Api.V1;
using DbBackupManager.Web.Authentication;
using DbBackupManager.Web.Servers;
using DbBackupManager.Web.SqlCredentials;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbBackupManager.Web.Api.V1;

[ApiController]
[Route("api/v1")]
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
public sealed class ServerManagementController(IServerManagementService service) : ControllerBase
{
    [HttpGet("server-inventory")]
    [ProducesResponseType<ServerInventoryResponse>(200, "application/json")]
    public Task<IActionResult> List(CancellationToken token) => Execute(async actor =>
    {
        var r = await service.ListAsync(actor, token);
        return r.Code == ManagementCode.Succeeded ? Ok(new ServerInventoryResponse(
            r.Value!.Servers.Select(Map).ToArray(), r.Value.Instances.Select(Map).ToArray(),
            r.Value.Databases.Select(Map).ToArray())) : Failure(r.Code);
    });

    [HttpPost("servers")]
    [ProducesResponseType<ServerResponse>(200, "application/json")]
    public Task<IActionResult> CreateServer(SaveServerRequest request, CancellationToken token) => SaveServer(null, request, token);
    [HttpPut("servers/{id:guid}")]
    [ProducesResponseType<ServerResponse>(200, "application/json")]
    public Task<IActionResult> UpdateServer(Guid id, SaveServerRequest request, CancellationToken token) => SaveServer(id, request, token);
    private Task<IActionResult> SaveServer(Guid? id, SaveServerRequest r, CancellationToken token) => Execute(async actor =>
    {
        var result = await service.SaveServerAsync(actor, id, r.Version,
            new(r.Name, r.LocalBackupRootPath, r.Protocol, r.Host, r.Port, r.BasePath, r.CredentialId, r.Fingerprint, r.Description, r.IsEnabled), token);
        return result.Code == ManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
    });

    [HttpPost("instances")]
    [ProducesResponseType<InstanceResponse>(200, "application/json")]
    public Task<IActionResult> CreateInstance(SaveInstanceRequest request, CancellationToken token) => SaveInstance(null, request, token);
    [HttpPut("instances/{id:guid}")]
    [ProducesResponseType<InstanceResponse>(200, "application/json")]
    public Task<IActionResult> UpdateInstance(Guid id, SaveInstanceRequest request, CancellationToken token) => SaveInstance(id, request, token);
    private Task<IActionResult> SaveInstance(Guid? id, SaveInstanceRequest r, CancellationToken token) => Execute(async actor =>
    {
        var result = await service.SaveInstanceAsync(actor, id, r.Version,
            new(r.ServerId, r.Name, r.ConnectionAddress, r.SqlCredentialId, r.TrustServerCertificate,
                r.CertificateTrustReason, r.ConnectionTimeoutSeconds, r.IsEnabled, r.AllowLegacyTls, r.LegacyTlsReason), token);
        return result.Code == ManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
    });

    [HttpPost("instances/{id:guid}/probe")]
    [ProducesResponseType<InstanceResponse>(200, "application/json")]
    public Task<IActionResult> Probe(Guid id, InstanceProbeRequest request, CancellationToken token) => ProbeCore(id, request, false, token);
    [HttpPost("instances/{id:guid}/discover")]
    [ProducesResponseType<InstanceResponse>(200, "application/json")]
    public Task<IActionResult> Discover(Guid id, InstanceProbeRequest request, CancellationToken token) => ProbeCore(id, request, true, token);
    private Task<IActionResult> ProbeCore(Guid id, InstanceProbeRequest r, bool discover, CancellationToken token) => Execute(async actor =>
    {
        var result = await service.ProbeAsync(actor, id, r.Version, discover, token);
        return result.Code == ManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
    });

    [HttpPost("databases/{id:guid}/managed")]
    [ProducesResponseType<DatabaseResponse>(200, "application/json")]
    public Task<IActionResult> SetManaged(Guid id, SetDatabaseManagedRequest request, CancellationToken token) => Execute(async actor =>
    {
        var result = await service.SetManagedAsync(actor, id, request.Version, request.IsManaged, token);
        return result.Code == ManagementCode.Succeeded ? Ok(Map(result.Value!)) : Failure(result.Code);
    });
    private Task<IActionResult> Execute(Func<DbBackupManager.Application.Identity.AdminSession, Task<IActionResult>> action)
    {
        var actor = SqlCredentialPresentation.GetActor(User);
        return actor is null ? Task.FromResult<IActionResult>(Failure(ManagementCode.AuthenticationRequired)) : action(actor);
    }
    private ObjectResult Failure(ManagementCode code)
    {
        var e = ServerManagementPresentation.Error(code);
        return StatusCode(e.Status, ApiProblemDetails.Create(HttpContext, e.Status, e.Code, e.Message));
    }
    private static ServerResponse Map(ServerItem x) => new(x.Id,
        new(x.Settings.Name, x.Settings.LocalBackupRootPath, x.Settings.Protocol, x.Settings.Host, x.Settings.Port,
            x.Settings.BasePath, x.Settings.CredentialId, x.Settings.Fingerprint, x.Settings.Description, x.Settings.IsEnabled), x.Version);
    private static InstanceResponse Map(InstanceItem x) => new(x.Id,
        new(x.Settings.ServerId, x.Settings.Name, x.Settings.ConnectionAddress, x.Settings.SqlCredentialId,
            x.Settings.TrustServerCertificate, x.Settings.CertificateTrustReason, x.Settings.ConnectionTimeoutSeconds, x.Settings.IsEnabled, x.Settings.AllowLegacyTls, x.Settings.LegacyTlsReason),
        x.Version, x.ConnectionStatus, x.ProductVersion, x.Edition, x.LastCheckedAtUtc, x.ErrorCode);
    private static DatabaseResponse Map(DatabaseItem x) => new(x.Id, x.InstanceId, x.Name, x.IsSystemDatabase,
        x.IsAvailable, x.IsManaged, x.State, x.RecoveryModel, x.LastDiscoveredAtUtc, x.Version);
}
