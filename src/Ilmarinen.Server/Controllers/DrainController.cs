using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace Ilmarinen.Server.Controllers;

/// <summary>Draining the fleet before a deploy: holding dispatch, and seeing what is still running (<see cref="DispatchHold"/>).</summary>
[ApiController]
[Route("api/[controller]")]
public class DrainController : ControllerBase
{
    private readonly JobScheduler _scheduler;

    public DrainController(JobScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    [HttpGet]
    public async Task<ActionResult<DrainStatus>> Get()
    {
        return Ok(await _scheduler.GetDrainStatusAsync());
    }

    [HttpPut]
    public async Task<ActionResult<DrainStatus>> Put([FromBody] DrainUpdate update)
    {
        await _scheduler.SetDrainingAsync(update.Draining);
        return Ok(await _scheduler.GetDrainStatusAsync());
    }
}
