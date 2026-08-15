using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Channels;
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

    /// <summary>
    /// Events held for a live tail whose client isn't draining them. Past this they are dropped, and
    /// the client refills the gap from the chunks feed — the same recovery a dropped connection gets.
    /// Bounded by count, so the worst case is this many <see cref="LiveTailMaxChunkChars"/> chunks.
    /// </summary>
    private const int LiveTailBacklog = 64;

    /// <summary>
    /// Ceiling on one write to a live tail. A backlog that has built up goes out in several writes
    /// rather than one string holding all of it.
    /// </summary>
    private const int LiveTailBatchChars = 256 * 1024;

    /// <summary>
    /// How long a live tail may sit silent before it emits a comment. A quiet build step can take
    /// minutes, and to anything between here and the browser that is indistinguishable from a dead
    /// connection.
    /// </summary>
    private const int LiveTailKeepAliveMs = 25_000;

    /// <summary>
    /// Past this a chunk is announced by sequence alone and fetched from the feed instead. See
    /// <see cref="FormatChunkEvent"/>.
    /// </summary>
    private const int LiveTailMaxChunkChars = 64 * 1024;

    private readonly JobScheduler _scheduler;
    private readonly JobRepository _jobs;
    private readonly JobLogRepository _logs;
    private readonly LogStreamService _logStream;
    private readonly LogSubscriptionService _subscriptions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<JobsController> _logger;

    public JobsController(
        JobScheduler scheduler,
        JobRepository jobs,
        JobLogRepository logs,
        LogStreamService logStream,
        LogSubscriptionService subscriptions,
        IHostApplicationLifetime lifetime,
        ILogger<JobsController> logger)
    {
        _scheduler = scheduler;
        _jobs = jobs;
        _logs = logs;
        _logStream = logStream;
        _subscriptions = subscriptions;
        _lifetime = lifetime;
        _logger = logger;
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

        // A chunk sits in the persistence buffer until the next one arrives, so a quiet build would otherwise never show its last output. An unbounded read is a client establishing the window it will follow from, and it only gets one shot at being right — a hole here is one nothing later re-requests.
        if (before == null && after == null)
        {
            await _logStream.FlushJobAsync(id);
        }

        var chunks = await ReadWindowAsync(id, before, after, limit);

        // The other way that buffered chunk hides is behind a forward poll that found nothing. Flushing only on the empty ones keeps the guarantee without turning every poll into an INSERT — and a backward window can't be affected by a flush, since what it persists is always newer.
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
    /// A job's live log tail, as Server-Sent Events: one event per chunk, sent the moment the server
    /// receives it from the worker rather than when it reaches the database — which is the whole
    /// point, since persistence is batched and a quiet build can leave a chunk buffered indefinitely.
    ///
    /// It deliberately carries no history. The chunks feed above is the only reader of stored log, so
    /// a client that misses events — a full backlog, a dropped connection, a chunk too big to carry —
    /// closes the gap from there by sequence number, which is also why Last-Event-ID is ignored.
    ///
    /// An endpoint rather than a push down the Blazor circuit, so that reading a log doesn't depend on
    /// having a circuit: the full-screen viewer survives a dead circuit, and this stays testable at
    /// the HTTP seam. It costs one of the browser's ~6 connections per origin for as long as the page
    /// is open, so a reader with many job pages open at once will queue requests behind them. The
    /// action's DI scope lives as long as the connection does, so nothing below the initial read may
    /// touch the database.
    /// </summary>
    [HttpGet("{id}/logs/stream")]
    public async Task<IActionResult> StreamLog(Guid id)
    {
        // Wait rather than one of the Drop modes: those discard silently and still report success, which would leave the client short of a chunk with nothing said about it. TryWrite on a full Wait channel refuses the write and says so.
        var events = Channel.CreateBounded<LiveTailEvent>(new BoundedChannelOptions(LiveTailBacklog)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

        // A live tail ends when its reader leaves or when the server goes down. Without the second, every open log page would hold up shutdown until the host's timeout expired — on every deploy.
        using var streaming = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted, _lifetime.ApplicationStopping);

        var ended = false;

        // Subscribing before the job is read, so a completion landing between the two ends this stream rather than stranding it open. These callbacks run on the thread receiving the worker's log, so they do nothing but hand the chunk over — the framing happens in the pump.
        var subscriberId = _subscriptions.Subscribe(
            id,
            chunk =>
            {
                if (!ended && !events.Writer.TryWrite(new LiveTailEvent(chunk, null)))
                {
                    _logger.LogWarning("Live log tail for job {JobId} is not keeping up; dropped chunk {Sequence}. The client will refetch it from the feed.", id, chunk.SequenceNumber);
                }
            },
            status =>
            {
                if (!events.Writer.TryWrite(new LiveTailEvent(null, status)))
                {
                    _logger.LogWarning("Live log tail for job {JobId} could not be told the job finished; its client will find out when it reconnects.", id);
                }

                ended = true;
                events.Writer.TryComplete();
            });

        try
        {
            var job = await _jobs.GetAsync(id);
            if (job == null)
            {
                return NotFound();
            }

            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-store";
            // A reverse proxy that buffers the response would defeat the point of a live tail.
            Response.Headers["X-Accel-Buffering"] = "no";

            // Headers and a first frame together: until bytes arrive the client can't know the subscription above is live, and an intermediary that buffers bodies rather than headers would sit on an empty stream until the first keepalive.
            await WriteFrameAsync(": open\n\n", streaming.Token);

            if (job.Status is Protocol.JobStatus.Success or Protocol.JobStatus.Failed or Protocol.JobStatus.Cancelled)
            {
                await WriteFrameAsync(FormatEvent(new LiveTailEvent(null, job.Status)), streaming.Token);
                return new EmptyResult();
            }

            await PumpAsync(events.Reader, streaming.Token);
        }
        catch (OperationCanceledException) when (streaming.IsCancellationRequested)
        {
            // The reader closed the page, or the server is stopping. Both are how a live tail ends.
        }
        finally
        {
            _subscriptions.Unsubscribe(id, subscriberId);
        }

        return new EmptyResult();
    }

    /// <summary>
    /// Writes events until the job finishes or <paramref name="streaming"/> ends, breaking the wait
    /// often enough to keep the connection warm.
    /// </summary>
    private async Task PumpAsync(ChannelReader<LiveTailEvent> events, CancellationToken streaming)
    {
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(streaming);
            idle.CancelAfter(LiveTailKeepAliveMs);

            bool more;
            try
            {
                more = await events.WaitToReadAsync(idle.Token);
            }
            catch (OperationCanceledException) when (!streaming.IsCancellationRequested)
            {
                await WriteFrameAsync(": keepalive\n\n", streaming);
                continue;
            }

            if (!more)
            {
                return;
            }

            var frames = new StringBuilder();

            // Batched, so a burst is one write — but only up to a point: draining a full backlog into one string would hold the whole of it twice over.
            while (frames.Length < LiveTailBatchChars && events.TryRead(out var pending))
            {
                frames.Append(FormatEvent(pending));
            }

            await WriteFrameAsync(frames.ToString(), streaming);
        }
    }

    private async Task WriteFrameAsync(string frame, CancellationToken streaming)
    {
        await Response.WriteAsync(frame, streaming);
        await Response.Body.FlushAsync(streaming);
    }

    /// <summary>Either a chunk to send or the job's final status — the two things a live tail carries.</summary>
    private readonly record struct LiveTailEvent(LogBroadcast? Chunk, Protocol.JobStatus? Status);

    private static string FormatEvent(LiveTailEvent pending)
    {
        return pending.Chunk != null
            ? FormatChunkEvent(pending.Chunk)
            : $"event: status\ndata: {pending.Status}\n\n";
    }

    /// <summary>
    /// One chunk as an SSE frame. The sequence number is the event id, because that is what the
    /// client checks for gaps; the chunk's stored NDJSON goes out one line per data field, which the
    /// browser rejoins with newlines into exactly the body the feed would have served.
    ///
    /// A chunk is whatever one worker-side flush produced, so it can be arbitrarily large — past
    /// <see cref="LiveTailMaxChunkChars"/> the frame carries an empty data field instead, and the
    /// client fetches the content from the feed, where the page budget applies. The field still has to
    /// be there: a frame whose data is absent entirely sets the last event id and is then discarded
    /// without ever reaching a listener.
    /// </summary>
    private static string FormatChunkEvent(LogBroadcast chunk)
    {
        var frame = new StringBuilder();

        frame.Append("id: ").Append(chunk.SequenceNumber).Append('\n');
        frame.Append("event: chunk\n");

        if (chunk.Content.Length > LiveTailMaxChunkChars)
        {
            return frame.Append("data: \n\n").ToString();
        }

        foreach (var line in chunk.Content.Split('\n'))
        {
            if (line.Length > 0)
            {
                frame.Append("data: ").Append(line).Append('\n');
            }
        }

        return frame.Append('\n').ToString();
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
