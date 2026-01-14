using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Ilmarinen.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WorkersController : ControllerBase
{
    private readonly WorkerRepository _workers;

    public WorkersController(WorkerRepository workers)
    {
        _workers = workers;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkerView>>> GetWorkers()
    {
        return Ok(await _workers.GetAllAsync());
    }
}
