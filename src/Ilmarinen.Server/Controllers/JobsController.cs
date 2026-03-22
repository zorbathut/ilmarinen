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
        var jobId = await _scheduler.EnqueueJobAsync(submission);
        return Ok(new JobSubmissionResult { Id = jobId });
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<JobInfo>> GetJob(string id)
    {
        if (!Ulid.TryParse(id, out var ulid))
            return BadRequest("Invalid job ID");

        var job = await _jobs.GetAsync(ulid);
        return job != null ? Ok(job) : NotFound();
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<JobInfo>>> GetAllJobs()
    {
        return Ok(await _jobs.GetAllAsync());
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> CancelJob(string id)
    {
        if (!Ulid.TryParse(id, out var ulid))
            return BadRequest("Invalid job ID");

        var cancelled = await _scheduler.CancelJobAsync(ulid);
        return cancelled ? Ok() : NotFound();
    }
}

public record JobSubmissionResult
{
    public required Ulid Id { get; init; }
}
