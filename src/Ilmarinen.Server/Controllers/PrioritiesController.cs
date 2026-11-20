using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Controllers;

/// <summary>The operator's lists of worker and job priorities. {kind} is "worker" or "job".</summary>
[ApiController]
[Route("api/[controller]")]
public class PrioritiesController : ControllerBase
{
    private readonly PriorityRepository _priorities;
    private readonly JobScheduler _scheduler;

    public PrioritiesController(PriorityRepository priorities, JobScheduler scheduler)
    {
        _priorities = priorities;
        _scheduler = scheduler;
    }

    [HttpGet("{kind}")]
    public async Task<ActionResult<IReadOnlyList<PriorityInfo>>> List(string kind)
    {
        if (ParseKind(kind) is not { } parsed)
        {
            return UnknownKind(kind);
        }
        var usage = await _priorities.GetUsageAsync();
        return Ok((await _priorities.GetAllAsync(parsed)).Select(p => new PriorityInfo { Id = p.Id, Name = p.Name, IsDefault = p.IsDefault, Usage = usage.GetValueOrDefault(p.Id) ?? new PriorityUsage() }).ToList());
    }

    [HttpPost("{kind}")]
    public async Task<ActionResult<PriorityInfo>> Create(string kind, [FromBody] PriorityName request)
    {
        if (ParseKind(kind) is not { } parsed)
        {
            return UnknownKind(kind);
        }
        var created = await _scheduler.ChangePrioritiesAsync(p => p.CreateAsync(parsed, request.Name));
        return StatusCode(StatusCodes.Status201Created, new PriorityInfo { Id = created.Id, Name = created.Name, IsDefault = created.IsDefault, Usage = new PriorityUsage() });
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult> Rename(Guid id, [FromBody] PriorityName request)
    {
        return await _scheduler.ChangePrioritiesAsync(p => p.RenameAsync(id, request.Name)) ? NoContent() : NotFound(new { error = "No such priority." });
    }

    [HttpPost("{id:guid}/default")]
    public async Task<ActionResult> MakeDefault(Guid id)
    {
        return await _scheduler.ChangePrioritiesAsync(p => p.SetDefaultAsync(id)) ? NoContent() : NotFound(new { error = "No such priority." });
    }

    /// <summary>Takes every entry of the kind, highest first.</summary>
    [HttpPut("{kind}/order")]
    public async Task<ActionResult> Reorder(string kind, [FromBody] List<Guid> highestFirst)
    {
        if (ParseKind(kind) is not { } parsed)
        {
            return UnknownKind(kind);
        }
        await _scheduler.ChangePrioritiesAsync(async p =>
        {
            await p.ReorderAsync(parsed, highestFirst);
            return true;
        });
        return NoContent();
    }

    /// <summary>Moves everything using the priority onto the replacement, which must be another of the same kind.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Delete(Guid id, [FromQuery] Guid? replacement)
    {
        if (replacement == null)
        {
            return BadRequest(new { error = "Deleting a priority needs a replacement: ?replacement=<id of another priority of the same kind>." });
        }
        return await _scheduler.ChangePrioritiesAsync(p => p.DeleteAsync(id, replacement.Value)) ? NoContent() : NotFound(new { error = "No such priority." });
    }

    private static PriorityKind? ParseKind(string kind)
    {
        return kind.ToLowerInvariant() switch
        {
            "worker" => PriorityKind.Worker,
            "job" => PriorityKind.Job,
            _ => null
        };
    }

    private BadRequestObjectResult UnknownKind(string kind)
    {
        return BadRequest(new { error = $"\"{kind}\" isn't a kind of priority; the kinds are \"worker\" and \"job\"." });
    }
}
