using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

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
        return CreatedAtAction(nameof(GetPipeline), new { id = pipeline.Id }, pipeline);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PipelineInfo>>> GetAllPipelines()
    {
        return Ok(await _pipelines.GetAllAsync());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<PipelineInfo>> GetPipeline(Guid id)
    {
        var pipeline = await _pipelines.GetAsync(id);
        return pipeline != null ? Ok(pipeline) : NotFound();
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<PipelineInfo>> UpdatePipeline(Guid id, [FromBody] PipelineUpdate update)
    {
        if (update.Schedule != null && !CronValidator.TryParse(update.Schedule, out _))
            return BadRequest("Invalid cron expression for schedule");

        var pipeline = await _pipelines.UpdateAsync(id, update);
        return pipeline != null ? Ok(pipeline) : NotFound();
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeletePipeline(Guid id)
    {
        var deleted = await _pipelines.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("{id}/trigger")]
    public async Task<ActionResult<JobSubmissionResult>> TriggerPipeline(Guid id, [FromBody] PipelineTrigger? trigger)
    {
        if (!await _pipelines.ExistsAsync(id))
            return NotFound();

        var submission = new JobSubmission
        {
            PipelineId = id,
            Ref = trigger?.Ref,
            GitTokenMode = GitTokenMode.Inherit
        };

        var jobId = await _scheduler.EnqueueJobAsync(submission);
        return Ok(new JobSubmissionResult { Id = jobId });
    }
}
