using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Ilmarinen.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JobsController : ControllerBase
{
    /// <summary>
    /// Chunks decoded per response flush when downloading a log.
    /// </summary>
    public const int DownloadPageSize = 200;

    private readonly JobScheduler _scheduler;
    private readonly JobRepository _jobs;
    private readonly JobLogRepository _logs;
    private readonly LogStreamService _logStream;

    public JobsController(JobScheduler scheduler, JobRepository jobs, JobLogRepository logs, LogStreamService logStream)
    {
        _scheduler = scheduler;
        _jobs = jobs;
        _logs = logs;
        _logStream = logStream;
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

        var jobId = await _scheduler.EnqueueJobAsync(submission);
        return Ok(new JobSubmissionResult { Id = jobId });
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

    /// <summary>
    /// Download a job's whole stored log as plain text. For a running job that means everything
    /// persisted so far — a snapshot, not a live stream.
    /// </summary>
    [HttpGet("{id}/logs/download")]
    public async Task<IActionResult> DownloadLog(Guid id)
    {
        if (await _jobs.GetAsync(id) == null)
        {
            return NotFound();
        }

        // A running job's most recent chunks are still sitting in the server's persistence buffer.
        await _logStream.FlushJobAsync(id);

        Response.ContentType = "text/plain; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=\"job-{id}.log\"";

        var nextSequence = 0;

        // Page through the chunks and flush each page as it is decoded, so a log that is too big to render on the page is also never held in server memory in full.
        while (true)
        {
            var page = await _logs.GetChunksAsync(id, nextSequence, DownloadPageSize);
            if (page.Count == 0)
            {
                break;
            }

            var text = new StringBuilder();

            foreach (var chunk in page)
            {
                // A worker re-sends a chunk whose send only appeared to fail, and the (JobId, SequenceNumber) index doesn't forbid it, so the same sequence can be stored twice.
                if (chunk.SequenceNumber < nextSequence)
                {
                    continue;
                }

                foreach (var entry in LogChunkParser.ParseEntries(chunk.Content))
                {
                    text.Append(entry.Data);
                }

                nextSequence = chunk.SequenceNumber + 1;
            }

            await Response.WriteAsync(text.ToString(), HttpContext.RequestAborted);
        }

        return new EmptyResult();
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
