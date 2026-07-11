using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ilmarinen.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JobsController : ControllerBase
{
    private readonly JobScheduler _scheduler;
    private readonly JobRepository _jobs;

    public JobsController(JobScheduler scheduler, JobRepository jobs)
    {
        _scheduler = scheduler;
        _jobs = jobs;
    }

    [HttpPost]
    public async Task<ActionResult<JobSubmissionResult>> SubmitJob([FromBody] JobSubmission submission)
    {
        if (submission.PipelineId == null && string.IsNullOrWhiteSpace(submission.RepoUrl))
            return BadRequest("RepoUrl is required when PipelineId is not set");

        if (submission.PipelineId == null && string.IsNullOrWhiteSpace(submission.Ref))
            return BadRequest("Ref is required when PipelineId is not set");

        if (submission.PipelineId == null && string.IsNullOrWhiteSpace(submission.ScriptPath))
            return BadRequest("ScriptPath is required when PipelineId is not set");

        if (submission.GitTokenMode == Protocol.GitTokenMode.Inherit && submission.PipelineId == null)
            return BadRequest("GitTokenMode.Inherit requires a PipelineId");

        try
        {
            var jobId = await _scheduler.EnqueueJobAsync(submission);
            return Ok(new JobSubmissionResult { Id = jobId });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<JobInfo>> GetJob(Guid id)
    {
        var job = await _jobs.GetAsync(id);
        return job != null ? Ok(job) : NotFound();
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<JobInfo>>> GetAllJobs()
    {
        return Ok(await _jobs.GetAllAsync());
    }

    [HttpPost("{id}/retry")]
    public async Task<ActionResult<JobSubmissionResult>> RetryJob(Guid id)
    {
        JobSubmission? submission;
        try
        {
            submission = await _jobs.BuildRetrySubmissionAsync(id);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

        if (submission == null)
            return NotFound();

        var jobId = await _scheduler.EnqueueJobAsync(submission);
        return Ok(new JobSubmissionResult { Id = jobId });
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> CancelJob(Guid id)
    {
        var cancelled = await _scheduler.CancelJobAsync(id);
        return cancelled ? Ok() : NotFound();
    }
}
