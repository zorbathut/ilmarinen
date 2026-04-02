using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Mvc;
using NUlid;
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
        if (!Ulid.TryParse(id, out var ulid))
            return BadRequest("Invalid repository ID");

        var repo = await _repositories.GetAsync(ulid);
        return repo != null ? Ok(repo) : NotFound();
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<RepositoryInfo>> UpdateRepository(string id, [FromBody] RepositoryUpdate update)
    {
        if (!Ulid.TryParse(id, out var ulid))
            return BadRequest("Invalid repository ID");

        var repo = await _repositories.UpdateAsync(ulid, update);
        return repo != null ? Ok(repo) : NotFound();
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteRepository(string id)
    {
        if (!Ulid.TryParse(id, out var ulid))
            return BadRequest("Invalid repository ID");

        var deleted = await _repositories.DeleteAsync(ulid);
        return deleted ? NoContent() : NotFound();
    }
}
