using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

public class LogStreamService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<JobLogsHub, IJobLogsClient> _hubContext;
    private readonly LogSubscriptionService _subscriptionService;
    private readonly ILogger<LogStreamService> _logger;
    private readonly ConcurrentDictionary<Guid, LogBuffer> _buffers = new();

    private const int BufferFlushBytes = 8192;
    private const int BufferFlushMs = 500;

    public LogStreamService(
        IServiceScopeFactory scopeFactory,
        IHubContext<JobLogsHub, IJobLogsClient> hubContext,
        LogSubscriptionService subscriptionService,
        ILogger<LogStreamService> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _subscriptionService = subscriptionService;
        _logger = logger;
    }

    /// <summary>
    /// Process incoming log chunk from worker.
    /// 1. Broadcast to subscribed UI clients immediately (low latency)
    /// 2. Buffer for batched persistence (efficiency)
    /// </summary>
    public async Task ProcessChunkAsync(LogChunk chunk)
    {
        // 1. Immediate broadcast to UI
        var broadcast = new LogBroadcast
        {
            JobId = chunk.JobId,
            SequenceNumber = chunk.SequenceNumber,
            Content = chunk.Content,
            Timestamp = chunk.Timestamp
        };

        // Notify SignalR clients (external)
        var groupName = $"job-logs-{chunk.JobId}";
        _ = _hubContext.Clients.Group(groupName).ReceiveLogChunk(broadcast);

        // Notify Blazor components (in-process)
        _subscriptionService.NotifyLogChunk(broadcast);

        // 2. Buffer for persistence
        var buffer = _buffers.GetOrAdd(chunk.JobId, _ => new LogBuffer());
        buffer.Add(chunk);

        if (buffer.ShouldFlush(BufferFlushBytes, BufferFlushMs))
        {
            await FlushBufferAsync(chunk.JobId, buffer);
        }
    }

    /// <summary>
    /// Flush all pending logs for a job (called when job completes).
    /// Removes the buffer since no more chunks are expected.
    /// </summary>
    public async Task FlushAsync(Guid jobId)
    {
        if (_buffers.TryRemove(jobId, out var buffer))
        {
            await FlushBufferAsync(jobId, buffer);
        }
    }

    /// <summary>
    /// Flush pending logs for a job without removing the buffer.
    /// Used when a worker disconnects (job still running, worker may reconnect).
    /// </summary>
    public async Task FlushJobAsync(Guid jobId)
    {
        if (_buffers.TryGetValue(jobId, out var buffer))
        {
            await FlushBufferAsync(jobId, buffer);
        }
    }

    /// <summary>
    /// Notify UI clients that a job has completed.
    /// </summary>
    public async Task NotifyJobCompletedAsync(Guid jobId, Protocol.JobStatus status)
    {
        await FlushAsync(jobId);

        // Notify SignalR clients (external)
        var groupName = $"job-logs-{jobId}";
        await _hubContext.Clients.Group(groupName).JobCompleted(jobId, status);

        // Notify Blazor components (in-process)
        _subscriptionService.NotifyJobCompleted(jobId, status);
    }

    private async Task FlushBufferAsync(Guid jobId, LogBuffer buffer)
    {
        var chunks = buffer.Drain();
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

    private class LogBuffer
    {
        private readonly List<LogChunk> _chunks = [];
        private int _totalBytes;
        private DateTime _firstChunkTime = DateTime.MaxValue;
        private readonly object _lock = new();

        public void Add(LogChunk chunk)
        {
            lock (_lock)
            {
                _chunks.Add(chunk);
                _totalBytes += chunk.Content.Length;
                if (_firstChunkTime == DateTime.MaxValue)
                    _firstChunkTime = DateTime.UtcNow;
            }
        }

        public bool ShouldFlush(int maxBytes, int maxMs)
        {
            lock (_lock)
            {
                if (_totalBytes >= maxBytes) return true;
                if (_chunks.Count > 0 && (DateTime.UtcNow - _firstChunkTime).TotalMilliseconds >= maxMs)
                    return true;
                return false;
            }
        }

        public List<LogChunk> Drain()
        {
            lock (_lock)
            {
                var result = new List<LogChunk>(_chunks);
                _chunks.Clear();
                _totalBytes = 0;
                _firstChunkTime = DateTime.MaxValue;
                return result;
            }
        }
    }
}
