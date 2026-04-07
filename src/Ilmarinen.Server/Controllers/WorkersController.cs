using Ilmarinen.Models;
using Ilmarinen.Server.Hubs;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WorkersController : ControllerBase
{
    private readonly WorkerRepository _workers;
    private readonly WorkerRegistrationService _registration;
    private readonly WorkspaceDeletionService _deletionService;
    private readonly IHubContext<WorkerHub, IWorkerClient> _hubContext;

    public WorkersController(
        WorkerRepository workers,
        WorkerRegistrationService registration,
        WorkspaceDeletionService deletionService,
        IHubContext<WorkerHub, IWorkerClient> hubContext)
    {
        _workers = workers;
        _registration = registration;
        _deletionService = deletionService;
        _hubContext = hubContext;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkerView>>> GetWorkers()
    {
        return Ok(await _workers.GetAllAsync());
    }

    [HttpPost]
    public async Task<ActionResult<WorkerRegistrationResult>> RegisterWorker([FromBody] RegisterWorkerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Worker name is required." });

        try
        {
            var result = await _registration.RegisterWorkerAsync(request.Name);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{workerId}")]
    public async Task<ActionResult> RevokeWorker(Guid workerId)
    {
        try
        {
            // Disconnect if currently connected
            var connectionId = _workers.FindConnectionIdByWorkerId(workerId);
            if (connectionId != null)
            {
                await _workers.SetDisconnectedAsync(connectionId);
            }

            await _registration.RevokeWorkerAsync(workerId);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Worker not found." });
        }
    }

    [HttpDelete("{workerId}/workspaces/{workspaceName}")]
    public async Task<ActionResult> DeleteWorkspace(Guid workerId, string workspaceName)
    {
        if (!WorkspaceConfig.IsValidName(workspaceName, out var validationError))
            return BadRequest(new { error = validationError });

        var connectionId = _workers.FindConnectionIdByWorkerId(workerId);
        if (connectionId == null)
            return NotFound(new { error = "Worker not found or not connected." });

        var key = _deletionService.CreatePending(connectionId, workspaceName);

        await _hubContext.Clients.Client(connectionId).DeleteWorkspace(workspaceName);

        var result = await _deletionService.WaitForResultAsync(key, TimeSpan.FromSeconds(30));

        if (result.Success)
            return Ok(result);

        if (result.Error?.Contains("in use") == true)
            return Conflict(new { error = result.Error });

        if (result.Error?.Contains("does not exist") == true)
            return NotFound(new { error = result.Error });

        if (result.Error?.Contains("did not respond") == true)
            return StatusCode(504, new { error = result.Error });

        return BadRequest(new { error = result.Error });
    }
}

public record RegisterWorkerRequest
{
    public string Name { get; init; } = "";
}
