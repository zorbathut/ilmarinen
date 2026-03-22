using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Microsoft.AspNetCore.Mvc;

namespace Ilmarinen.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VersionController : ControllerBase
{
    [HttpGet]
    public ActionResult<VersionInfo> Get()
    {
        return Ok(new VersionInfo { ProtocolHash = ProtocolVersion.Hash });
    }
}
