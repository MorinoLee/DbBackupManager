using DbBackupManager.Contracts.Api.V1;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace DbBackupManager.Web.Api.V1;

[ApiController]
[AllowAnonymous]
[ApiExplorerSettings(GroupName = "v1")]
[Route("api/v1/system")]
[Produces("application/json")]
public sealed class SystemController : ControllerBase
{
    private static readonly string ApplicationVersion = GetApplicationVersion();

    [HttpGet("version", Name = "GetSystemVersion")]
    [EndpointSummary("获取系统与 API 版本")]
    [EndpointDescription("返回当前 Web 主机的产品名、API 版本和程序集版本，不读取业务数据。")]
    [ProducesResponseType<SystemVersionResponse>(StatusCodes.Status200OK, Description = "成功返回技术版本信息。")]
    public ActionResult<SystemVersionResponse> GetVersion()
    {
        return Ok(new SystemVersionResponse(
            "DbBackupManager",
            "v1",
            ApplicationVersion));
    }

    private static string GetApplicationVersion()
    {
        var version = typeof(SystemController).Assembly.GetName().Version;

        return version is null
            ? "unknown"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
