using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Mvc;
using NUlid;

namespace Ilmarinen.Server.Controllers;

[ApiController]
[Route("api/jobs/{jobId}/artifacts")]
public class ArtifactsController : ControllerBase
{
    private readonly ArtifactRepository _artifacts;
    private readonly ILogger<ArtifactsController> _logger;

    public ArtifactsController(ArtifactRepository artifacts, ILogger<ArtifactsController> logger)
    {
        _artifacts = artifacts;
        _logger = logger;
    }

    /// <summary>
    /// List all artifacts for a job.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ArtifactInfo>>> List(string jobId)
    {
        if (!Ulid.TryParse(jobId, out var jobUlid))
            return BadRequest("Invalid job ID");

        var artifacts = await _artifacts.GetByJobIdAsync(jobUlid);
        return Ok(artifacts);
    }

    /// <summary>
    /// Get artifact metadata.
    /// </summary>
    [HttpGet("{artifactId}")]
    public async Task<ActionResult<ArtifactInfo>> GetInfo(string jobId, string artifactId)
    {
        if (!Ulid.TryParse(artifactId, out var artifactUlid))
            return BadRequest("Invalid artifact ID");

        var artifact = await _artifacts.GetAsync(artifactUlid);
        return artifact != null ? Ok(artifact) : NotFound();
    }

    /// <summary>
    /// Download an artifact.
    /// </summary>
    [HttpGet("{artifactId}/download")]
    public async Task<IActionResult> Download(string jobId, string artifactId)
    {
        if (!Ulid.TryParse(artifactId, out var artifactUlid))
            return BadRequest("Invalid artifact ID");

        var (stream, fileName) = await _artifacts.GetContentAsync(artifactUlid);

        if (stream == null)
            return NotFound();

        return File(stream, "application/octet-stream", fileName!);
    }
}
