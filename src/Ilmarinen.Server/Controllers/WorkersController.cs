using Ilmarinen.Models;
using Ilmarinen.Protocol;
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
    private readonly UIEventService _uiEvents;

    public WorkersController(
        WorkerRepository workers,
        WorkerRegistrationService registration,
        WorkspaceDeletionService deletionService,
        IHubContext<WorkerHub, IWorkerClient> hubContext,
        UIEventService uiEvents)
    {
        _workers = workers;
        _registration = registration;
        _deletionService = deletionService;
        _hubContext = hubContext;
        _uiEvents = uiEvents;
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

    [HttpPut("{workerId}")]
    public async Task<ActionResult> UpdateWorker(Guid workerId, [FromBody] WorkerUpdate update)
    {
        // System.Text.Json does not range-check enums, so an out-of-range number deserializes cleanly and would sort above High.
        if (!Enum.IsDefined(update.Priority))
        {
            return BadRequest(new { error = "Priority must be Low, Medium, or High." });
        }

        if (!await _workers.SetPriorityAsync(workerId, update.Priority))
        {
            return NotFound(new { error = "Worker not found." });
        }

        _uiEvents.NotifyWorkersChanged();
        return NoContent();
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

        return result.Status switch
        {
            DeleteWorkspaceStatus.Success => Ok(result),
            DeleteWorkspaceStatus.InUse => Conflict(new { error = result.Error }),
            DeleteWorkspaceStatus.NotFound => NotFound(new { error = result.Error }),
            DeleteWorkspaceStatus.Timeout => StatusCode(504, new { error = result.Error }),
            _ => BadRequest(new { error = result.Error })
        };
    }
}

public record RegisterWorkerRequest
{
    public string Name { get; init; } = "";
}

public record WorkerUpdate
{
    public required WorkerPriority Priority { get; init; }
}
