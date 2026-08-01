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

    /// <summary>
    /// Chunks the log feed reads per request.
    /// </summary>
    public const int FeedPageSize = 200;

    /// <summary>
    /// Ceiling on the characters one page of the log feed sends, whatever the chunk sizes turn out
    /// to be. The read itself is still bounded by the chunk count, not by this.
    /// </summary>
    public const int MaxPageBytes = 1_000_000;

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
        return await WriteWholeLogAsync(id, "attachment");
    }

    /// <summary>
    /// The same text as the download, rendered in the browser instead of saved, so the whole log can
    /// be searched with the browser's own find.
    /// </summary>
    [HttpGet("{id}/logs/raw")]
    public async Task<IActionResult> RawLog(Guid id)
    {
        return await WriteWholeLogAsync(id, "inline");
    }

    private async Task<IActionResult> WriteWholeLogAsync(Guid id, string disposition)
    {
        if (await _jobs.GetAsync(id) == null)
        {
            return NotFound();
        }

        // A running job's most recent chunks are still sitting in the server's persistence buffer.
        await _logStream.FlushJobAsync(id);

        Response.ContentType = "text/plain; charset=utf-8";
        Response.Headers.ContentDisposition = $"{disposition}; filename=\"job-{id}.log\"";

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

    /// <summary>
    /// A window of a job's stored log chunks, served as the NDJSON they are stored as. Bounds are
    /// exclusive; with neither, the newest chunks are returned.
    /// </summary>
    [HttpGet("{id}/logs/chunks")]
    public async Task<IActionResult> GetLogChunks(Guid id, int? before, int? after, int limit = FeedPageSize)
    {
        if (before != null && after != null)
        {
            return BadRequest("Specify at most one of before and after.");
        }

        if (limit < 1 || limit > FeedPageSize)
        {
            return BadRequest($"limit must be between 1 and {FeedPageSize}.");
        }

        var job = await _jobs.GetAsync(id);
        if (job == null)
        {
            return NotFound();
        }

        // The tail is a live URL, and one served from a cache would strand a viewer on stale output.
        Response.Headers.CacheControl = "no-store";

        var chunks = await ReadWindowAsync(id, before, after, limit);

        // A chunk sits in the persistence buffer until the next one arrives, so a quiet build would otherwise never show its last output. Flushing only when a forward window came back empty keeps that guarantee without turning every poll into an INSERT — and a backward window can't be affected by a flush, since what it persists is always newer.
        if (chunks.Count == 0 && before == null)
        {
            await _logStream.FlushJobAsync(id);
            chunks = await ReadWindowAsync(id, before, after, limit);
        }

        // Reading forward, the client already holds everything up to `after`, so a page trimmed for size has to keep its oldest end; reading backward it holds everything from `before` on, so the page has to keep its newest. Trimming the wrong end leaves a hole the client would never know to re-fetch.
        var emitted = TakeWithinBudget(chunks, keepOldest: after != null);

        // Read before the chunks were, so the status can only lag the content, never lead it: a client that saw "finished" alongside a short page would stop polling with output still to come.
        Response.Headers["X-Job-Status"] = job.Status.ToString();

        if (emitted.Count > 0)
        {
            Response.Headers["X-Log-First-Sequence"] = emitted[0].SequenceNumber.ToString();
            Response.Headers["X-Log-Last-Sequence"] = emitted[^1].SequenceNumber.ToString();
        }

        var content = new StringBuilder();

        foreach (var chunk in emitted)
        {
            content.Append(chunk.Content);
        }

        return Content(content.ToString(), "application/x-ndjson");
    }

    /// <summary>
    /// Drops repeated sequences, then trims the page to <see cref="MaxPageBytes"/> from one end. A
    /// chunk is whatever a single flush produced, so a build that writes megabytes in one call makes
    /// one enormous row — bounding a page by bytes as well as by count keeps it, and the window a
    /// client builds out of it, a predictable size whatever the log looks like.
    /// </summary>
    private static List<LogChunkInfo> TakeWithinBudget(IReadOnlyList<LogChunkInfo> chunks, bool keepOldest)
    {
        var kept = new List<LogChunkInfo>();
        var bytes = 0;
        var lastSequence = -1;

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = keepOldest ? chunks[i] : chunks[chunks.Count - 1 - i];

            // The (JobId, SequenceNumber) index doesn't forbid a worker storing the same chunk twice.
            if (chunk.SequenceNumber == lastSequence)
            {
                continue;
            }

            kept.Add(chunk);
            lastSequence = chunk.SequenceNumber;
            bytes += chunk.Content.Length;

            if (bytes >= MaxPageBytes)
            {
                break;
            }
        }

        if (!keepOldest)
        {
            kept.Reverse();
        }

        return kept;
    }

    private async Task<IReadOnlyList<LogChunkInfo>> ReadWindowAsync(Guid id, int? before, int? after, int limit)
    {
        if (after != null)
        {
            // int.MaxValue is a legitimate "nothing newer than everything" probe, and incrementing it would wrap to the start of the log.
            var from = after.Value == int.MaxValue ? int.MaxValue : after.Value + 1;
            return await _logs.GetChunksAsync(id, from, limit);
        }

        return await _logs.GetChunksBeforeAsync(id, before ?? int.MaxValue, limit);
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
