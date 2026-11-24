using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>
/// A job's log as it arrives: handed to live tails at once, batched into the database, and numbered so every run of the job reads on from the one before. Everything about one job's log happens under that job's lock, so a server-written line, a change of run and the worker's chunks can't interleave.
/// </summary>
public class LogStreamService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LogSubscriptionService _subscriptionService;
    private readonly ILogger<LogStreamService> _logger;
    private readonly ConcurrentDictionary<Guid, JobLog> _logs = new();

    private const int BufferFlushBytes = 8192;
    private const int BufferFlushMs = 500;

    public LogStreamService(
        IServiceScopeFactory scopeFactory,
        LogSubscriptionService subscriptionService,
        ILogger<LogStreamService> logger)
    {
        _scopeFactory = scopeFactory;
        _subscriptionService = subscriptionService;
        _logger = logger;
    }

    /// <summary>
    /// A chunk from a worker. Output of a run that is no longer the job's current one (a punted run's, replayed late) is dropped; the rest is numbered after the job's earlier runs, handed to the live tails, and buffered for the database.
    /// </summary>
    public async Task ProcessChunkAsync(LogChunk chunk)
    {
        await WithJobLogAsync(chunk.JobId, async log =>
        {
            if (!log.Loaded || (chunk.AssignmentId is { } named && named != log.Assignment))
            {
                await LoadAsync(chunk.JobId, log);
            }

            // A worker before protocol v2 names no assignment; its output counts as the current run's.
            if (chunk.AssignmentId is { } assignment && assignment != log.Assignment)
            {
                _logger.LogWarning("Dropping log chunk {Sequence} of job {JobId} from assignment {AssignmentId}, which is no longer the job's run", chunk.SequenceNumber, chunk.JobId, assignment);
                return 0;
            }

            log.LastWorkerSequence = Math.Max(log.LastWorkerSequence, chunk.SequenceNumber);
            var numbered = chunk with { SequenceNumber = chunk.SequenceNumber + log.Base };
            Broadcast(numbered);

            // A chunk for a finished job is one a worker replays after reconnecting, once completion has already flushed the job: nothing would flush a buffer for it again.
            if (log.Finished)
            {
                await PersistAsync(chunk.JobId, [numbered]);
                log.Forget = true;
                return 0;
            }

            log.Buffer.Add(numbered);
            if (log.Buffer.ShouldFlush(BufferFlushBytes, BufferFlushMs))
            {
                await PersistAsync(chunk.JobId, log.Buffer.Drain());
            }
            return 0;
        });
    }

    /// <summary>
    /// Writes a line of the server's own into the job's log, after everything already there, and hands it to the live tails. Returns its sequence number.
    /// </summary>
    public async Task<int> AppendLineAsync(Guid jobId, string text)
    {
        return await WithServerLogAsync(jobId, async log =>
        {
            var sequence = await log.NextSequenceAsync();
            await log.WriteLineAsync(sequence, text);
            return sequence;
        });
    }

    /// <summary>
    /// Runs <paramref name="work"/> under the job's log lock, with what it needs to write the server's lines and to start the job's next run: nothing the worker sends for the job lands meanwhile. The work must not take the job's log lock again (no other method of this service).
    /// </summary>
    public async Task<T> WithServerLogAsync<T>(Guid jobId, Func<ServerLog, Task<T>> work)
    {
        return await WithJobLogAsync(jobId, async log =>
        {
            if (!log.Loaded)
            {
                await LoadAsync(jobId, log);
            }
            return await work(new ServerLog(this, jobId, log));
        });
    }

    /// <summary>
    /// Flush all pending logs for a job (called when job completes).
    /// Forgets the job, since no more chunks are expected.
    /// </summary>
    public async Task FlushAsync(Guid jobId)
    {
        await WithJobLogAsync(jobId, async log =>
        {
            await PersistAsync(jobId, log.Buffer.Drain());
            log.Forget = true;
            return 0;
        });
    }

    /// <summary>
    /// Flush pending logs for a job, still expecting more.
    /// Used when a worker disconnects (job still running, worker may reconnect).
    /// </summary>
    public async Task FlushJobAsync(Guid jobId)
    {
        // Called on every read of a log: a job nothing is writing to has no state to flush, and gets none.
        if (!_logs.ContainsKey(jobId))
        {
            return;
        }
        await WithJobLogAsync(jobId, async log =>
        {
            await PersistAsync(jobId, log.Buffer.Drain());
            return 0;
        });
    }

    /// <summary>
    /// Flush what the job wrote last, then tell the live tails it is over — in that order, so a
    /// viewer that stops on the status has already been able to read everything the job produced.
    /// </summary>
    public async Task NotifyJobCompletedAsync(Guid jobId, Protocol.JobStatus status)
    {
        await FlushAsync(jobId);
        _subscriptionService.NotifyJobCompleted(jobId, status);
    }

    /// <summary>
    /// The one way in to a job's log state: under its lock, retried if the state was being forgotten meanwhile. Holding the lock across the insert, not just the drain, also keeps two flushes from committing out of order, where a reader paging forward from what it has seen would never go back for the earlier one.
    /// </summary>
    private async Task<T> WithJobLogAsync<T>(Guid jobId, Func<JobLog, Task<T>> work)
    {
        while (true)
        {
            var log = _logs.GetOrAdd(jobId, _ => new JobLog());
            await log.Lock.WaitAsync();
            try
            {
                if (log.Gone)
                {
                    continue;
                }

                var result = await work(log);
                if (log.Forget)
                {
                    log.Gone = true;
                    _logs.TryRemove(new KeyValuePair<Guid, JobLog>(jobId, log));
                }
                return result;
            }
            finally
            {
                log.Lock.Release();
            }
        }
    }

    private async Task LoadAsync(Guid jobId, JobLog log)
    {
        using var scope = _scopeFactory.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<JobRepository>().GetLogRunAsync(jobId);
        var stored = await scope.ServiceProvider.GetRequiredService<JobLogRepository>().GetMaxSequenceAsync(jobId);
        log.Loaded = true;
        log.Assignment = run?.AssignmentId;
        log.Base = run?.LogSequenceBase ?? 0;
        // What the run had sent, as far as the log shows; a server line last makes it a little high, which only leaves a gap.
        log.LastWorkerSequence = Math.Max(0, stored - log.Base);
        log.Finished = run != null && run.Status is JobStatus.Success or JobStatus.Failed or JobStatus.Cancelled;
    }

    private void Broadcast(LogChunk chunk)
    {
        _subscriptionService.NotifyLogChunk(new LogBroadcast
        {
            JobId = chunk.JobId,
            SequenceNumber = chunk.SequenceNumber,
            Content = chunk.Content,
            Timestamp = chunk.Timestamp
        });
    }

    private async Task PersistAsync(Guid jobId, List<LogChunk> chunks)
    {
        if (chunks.Count == 0) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var logRepo = scope.ServiceProvider.GetRequiredService<JobLogRepository>();
            await logRepo.SaveChunksAsync(chunks);

            _logger.LogDebug("Flushed {Count} log chunks for job {JobId}", chunks.Count, jobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist log chunks for job {JobId}", jobId);
        }
    }

    /// <summary>The server's side of a job's log, for work done under the job's log lock (<see cref="WithServerLogAsync"/>).</summary>
    public sealed class ServerLog
    {
        private readonly LogStreamService _service;
        private readonly Guid _jobId;
        private readonly JobLog _log;

        internal ServerLog(LogStreamService service, Guid jobId, JobLog log)
        {
            _service = service;
            _jobId = jobId;
            _log = log;
        }

        /// <summary>The number after everything in the job's log, buffered output included.</summary>
        public async Task<int> NextSequenceAsync()
        {
            await _service.PersistAsync(_jobId, _log.Buffer.Drain());
            using var scope = _service._scopeFactory.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<JobLogRepository>().GetMaxSequenceAsync(_jobId) + 1;
        }

        /// <summary>
        /// A metadata line, which every log reader shows, written straight to the database and to the live tails. The run carries on after it: its worker's next chunk, numbered by the worker from where it had got to, is moved past the line, so the line takes nobody's number.
        /// </summary>
        public async Task WriteLineAsync(int sequence, string text)
        {
            var now = DateTime.UtcNow;
            var entry = JsonSerializer.Serialize(new { t = "m", d = $"[ilmarinen] {text}\n", ts = new DateTimeOffset(now).ToUnixTimeMilliseconds() });
            var chunk = new LogChunk { JobId = _jobId, SequenceNumber = sequence, Content = entry + "\n", Timestamp = now };
            await _service.PersistAsync(_jobId, [chunk]);
            _service.Broadcast(chunk);

            _log.Base = sequence - _log.LastWorkerSequence;
            using var scope = _service._scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<JobRepository>().SetLogSequenceBaseAsync(_jobId, _log.Base);
        }

        /// <summary>The job's current run is over; the next, whoever runs it, numbers its output from 1 again, after whatever is written next. Until it starts, a chunk naming any assignment is a stale run's.</summary>
        public void StartNextRun()
        {
            _log.Assignment = null;
            _log.LastWorkerSequence = 0;
            _log.Finished = false;
        }
    }

    internal sealed class JobLog
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public LogBuffer Buffer { get; } = new();

        /// <summary>Whether the job's run below has been read from the database since this state was made.</summary>
        public bool Loaded { get; set; }
        public Guid? Assignment { get; set; }
        public int Base { get; set; }

        /// <summary>The highest sequence number the current run's worker has sent, as it numbered it.</summary>
        public int LastWorkerSequence { get; set; }
        public bool Finished { get; set; }

        /// <summary>Set by the work under the lock when nothing more is expected; the state is then dropped.</summary>
        public bool Forget { get; set; }

        /// <summary>Dropped from the service: a caller that waited on its lock meanwhile starts again with fresh state.</summary>
        public bool Gone { get; set; }
    }

    internal sealed class LogBuffer
    {
        private readonly List<LogChunk> _chunks = [];
        private int _totalBytes;
        private DateTime _firstChunkTime = DateTime.MaxValue;

        public void Add(LogChunk chunk)
        {
            _chunks.Add(chunk);
            _totalBytes += chunk.Content.Length;
            if (_firstChunkTime == DateTime.MaxValue)
            {
                _firstChunkTime = DateTime.UtcNow;
            }
        }

        public bool ShouldFlush(int maxBytes, int maxMs)
        {
            if (_totalBytes >= maxBytes)
            {
                return true;
            }
            return _chunks.Count > 0 && (DateTime.UtcNow - _firstChunkTime).TotalMilliseconds >= maxMs;
        }

        public List<LogChunk> Drain()
        {
            var result = new List<LogChunk>(_chunks);
            _chunks.Clear();
            _totalBytes = 0;
            _firstChunkTime = DateTime.MaxValue;
            return result;
        }
    }
}
