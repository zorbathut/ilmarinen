using System;
using Ilmarinen.Protocol.Requests;
using System;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ilmarinen.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RepositoriesController : ControllerBase
{
    private readonly RepositoryRepository _repositories;

    public RepositoriesController(RepositoryRepository repositories)
    {
        _repositories = repositories;
    }

    [HttpPost]
    public async Task<ActionResult<RepositoryInfo>> CreateRepository([FromBody] RepositorySubmission submission)
    {
        var repo = await _repositories.CreateAsync(submission);
        return CreatedAtAction(nameof(GetRepository), new { id = repo.Id.ToString() }, repo);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RepositoryInfo>>> GetAllRepositories()
    {
        return Ok(await _repositories.GetAllAsync());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<RepositoryInfo>> GetRepository(string id)
    {
        if (!Guid.TryParse(id, out var parsed))
            return BadRequest("Invalid repository ID");

        var repo = await _repositories.GetAsync(parsed);
        return repo != null ? Ok(repo) : NotFound();
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<RepositoryInfo>> UpdateRepository(string id, [FromBody] RepositoryUpdate update)
    {
        if (!Guid.TryParse(id, out var parsed))
            return BadRequest("Invalid repository ID");

        var repo = await _repositories.UpdateAsync(parsed, update);
        return repo != null ? Ok(repo) : NotFound();
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteRepository(string id)
    {
        if (!Guid.TryParse(id, out var parsed))
            return BadRequest("Invalid repository ID");

        var result = await _repositories.DeleteAsync(parsed);
        return result switch
        {
            null => NotFound(),
            > 0 => BadRequest($"Cannot delete: {result} pipeline(s) still reference this repository"),
            _ => NoContent()
        };
    }
}
