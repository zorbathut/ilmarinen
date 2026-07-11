using System;
using Ilmarinen.Protocol;
using System;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Mvc;
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
        if (!Guid.TryParse(id, out var parsed))
            return BadRequest("Invalid pipeline ID");

        var pipeline = await _pipelines.GetAsync(parsed);
        return pipeline != null ? Ok(pipeline) : NotFound();
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<PipelineInfo>> UpdatePipeline(string id, [FromBody] PipelineUpdate update)
    {
        if (!Guid.TryParse(id, out var parsed))
            return BadRequest("Invalid pipeline ID");

        if (update.Schedule != null && !CronValidator.TryParse(update.Schedule, out _))
            return BadRequest("Invalid cron expression for schedule");

        var pipeline = await _pipelines.UpdateAsync(parsed, update);
        return pipeline != null ? Ok(pipeline) : NotFound();
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeletePipeline(string id)
    {
        if (!Guid.TryParse(id, out var parsed))
            return BadRequest("Invalid pipeline ID");

        var deleted = await _pipelines.DeleteAsync(parsed);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("{id}/trigger")]
    public async Task<ActionResult<JobSubmissionResult>> TriggerPipeline(string id, [FromBody] PipelineTrigger? trigger)
    {
        if (!Guid.TryParse(id, out var parsed))
            return BadRequest("Invalid pipeline ID");

        if (!await _pipelines.ExistsAsync(parsed))
            return NotFound();

        var submission = new JobSubmission
        {
            PipelineId = parsed,
            Ref = trigger?.Ref,
            GitTokenMode = GitTokenMode.Inherit
        };

        try
        {
            var jobId = await _scheduler.EnqueueJobAsync(submission);
            return Ok(new JobSubmissionResult { Id = jobId });
        }
        catch (ArgumentException ex)
        {
            // The ExistsAsync guard above can race with a concurrent pipeline deletion.
            return BadRequest(ex.Message);
        }
    }
}
