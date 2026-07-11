using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

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
    public async Task<ActionResult<IReadOnlyList<ArtifactInfo>>> List(Guid jobId)
    {
        var artifacts = await _artifacts.GetByJobIdAsync(jobId);
        return Ok(artifacts);
    }

    /// <summary>
    /// Get artifact metadata.
    /// </summary>
    [HttpGet("{artifactId}")]
    public async Task<ActionResult<ArtifactInfo>> GetInfo(Guid jobId, Guid artifactId)
    {
        var artifact = await _artifacts.GetAsync(artifactId);
        return artifact != null ? Ok(artifact) : NotFound();
    }

    /// <summary>
    /// Download an artifact.
    /// </summary>
    [HttpGet("{artifactId}/download")]
    public async Task<IActionResult> Download(Guid jobId, Guid artifactId)
    {
        var (stream, fileName) = await _artifacts.GetContentAsync(artifactId);

        if (stream == null)
            return NotFound();

        return File(stream, "application/octet-stream", fileName!);
    }
}
