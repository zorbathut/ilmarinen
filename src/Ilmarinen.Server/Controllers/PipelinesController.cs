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
public class PipelinesController : ControllerBase
{
    private readonly PipelineRepository _pipelines;
    private readonly JobScheduler _scheduler;

    public PipelinesController(PipelineRepository pipelines, JobScheduler scheduler)
    {
        _pipelines = pipelines;
        _scheduler = scheduler;
    }

    [HttpPost]
    public async Task<ActionResult<PipelineInfo>> CreatePipeline([FromBody] PipelineSubmission submission)
    {
        if (submission.Schedule != null && !CronValidator.TryParse(submission.Schedule, out _))
            return BadRequest("Invalid cron expression for schedule");

        var pipeline = await _pipelines.CreateAsync(submission);
        return CreatedAtAction(nameof(GetPipeline), new { id = pipeline.Id.ToString() }, pipeline);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PipelineInfo>>> GetAllPipelines()
    {
        return Ok(await _pipelines.GetAllAsync());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<PipelineInfo>> GetPipeline(string id)
    {
        if (!Ulid.TryParse(id, out var ulid))
            return BadRequest("Invalid pipeline ID");

        var pipeline = await _pipelines.GetAsync(ulid);
        return pipeline != null ? Ok(pipeline) : NotFound();
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<PipelineInfo>> UpdatePipeline(string id, [FromBody] PipelineUpdate update)
    {
        if (!Ulid.TryParse(id, out var ulid))
            return BadRequest("Invalid pipeline ID");

        if (update.Schedule != null && !CronValidator.TryParse(update.Schedule, out _))
            return BadRequest("Invalid cron expression for schedule");

        var pipeline = await _pipelines.UpdateAsync(ulid, update);
        return pipeline != null ? Ok(pipeline) : NotFound();
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeletePipeline(string id)
    {
        if (!Ulid.TryParse(id, out var ulid))
            return BadRequest("Invalid pipeline ID");

        var deleted = await _pipelines.DeleteAsync(ulid);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("{id}/trigger")]
    public async Task<ActionResult<JobSubmissionResult>> TriggerPipeline(string id, [FromBody] PipelineTrigger? trigger)
    {
        if (!Ulid.TryParse(id, out var ulid))
            return BadRequest("Invalid pipeline ID");

        var submission = await _pipelines.BuildSubmissionAsync(ulid, trigger?.Ref);
        if (submission == null)
            return NotFound();

        var jobId = await _scheduler.EnqueueJobAsync(submission, ulid);
        return Ok(new JobSubmissionResult { Id = jobId });
    }
}
