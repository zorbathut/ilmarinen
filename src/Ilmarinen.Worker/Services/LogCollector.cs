using Ilmarinen.Protocol.Requests;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Collects log output from pipeline execution and streams to server via SignalR.
/// Buffers small chunks to reduce network overhead. Failed sends are stored in a
/// MessageBuffer for replay on reconnection.
/// </summary>
public class LogCollector
{
    private readonly Guid _jobId;
    private readonly HubConnection _connection;
    private readonly MessageBuffer _messageBuffer;
    private readonly ILogger _logger;
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();
    private readonly ConcurrentQueue<Task> _pendingFlushes = new();
    private int _sequenceNumber;
    private DateTime _lastFlush = DateTime.UtcNow;
    private volatile bool _draining;

    private const int FlushBytes = 4096;
    private const int FlushMs = 100;

    public LogCollector(Guid jobId, HubConnection connection, MessageBuffer messageBuffer, ILogger logger)
    {
        _jobId = jobId;
        _connection = connection;
        _messageBuffer = messageBuffer;
        _logger = logger;
    }

    /// <summary>
    /// Write a log entry. Type is "o" for stdout, "e" for stderr, "m" for metadata.
    /// </summary>
    public void Write(string type, string data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return;
        }

        var entry = JsonSerializer.Serialize(new
        {
            t = type,
            d = data,
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        // The draining check has to happen under the lock: a writer that passed an unlocked check while FlushAsync drains would leave bytes in _buffer that nothing ever sends.
        bool dropped;
        lock (_lock)
        {
            dropped = _draining;
            if (!dropped)
            {
                _buffer.AppendLine(entry);
            }
        }

        if (dropped)
        {
            _logger.LogWarning("Log write for job {JobId} arrived after the final flush and was dropped: {Data}", _jobId, data);
            return;
        }

        // Track pending flush task to ensure we can drain before shutdown
        var flushTask = TryFlushAsync();
        _pendingFlushes.Enqueue(flushTask);

        // Periodically clean up completed tasks to prevent unbounded growth
        CleanupCompletedFlushes();
    }

    private void CleanupCompletedFlushes()
    {
        // Only clean up occasionally to avoid overhead
        if (_pendingFlushes.Count < 100) return;

        var remaining = new List<Task>();
        while (_pendingFlushes.TryDequeue(out var task))
        {
            if (!task.IsCompleted)
            {
                remaining.Add(task);
            }
        }

        foreach (var task in remaining)
        {
            _pendingFlushes.Enqueue(task);
        }
    }

    /// <summary>
    /// Action callback that can be passed to PipelineRunner.
    /// </summary>
    public Action<string, string> AsCallback() => Write;

    private async Task TryFlushAsync()
    {
        // Don't flush if we're draining - FlushAsync will handle it
        if (_draining) return;

        string? chunk = null;
        int seq = 0;

        // Taking the whole buffer keeps every chunk a whole number of NDJSON lines; don't trim it to a byte budget, since readers parse chunks independently and a split line is unrecoverable.
        lock (_lock)
        {
            var elapsed = (DateTime.UtcNow - _lastFlush).TotalMilliseconds;
            if (_buffer.Length >= FlushBytes || elapsed >= FlushMs)
            {
                chunk = _buffer.ToString();
                _buffer.Clear();
                _lastFlush = DateTime.UtcNow;
                seq = ++_sequenceNumber;
            }
        }

        if (chunk != null)
        {
            await SendChunkAsync(seq, chunk);
        }
    }

    /// <summary>
    /// Flush any remaining buffered output and wait for all pending flushes to complete.
    /// After calling this method, no new writes will be accepted.
    /// </summary>
    public async Task FlushAsync()
    {
        // Prevent new writes from queuing more flushes
        _draining = true;

        // Flush the current buffer
        string chunk;
        int seq;

        lock (_lock)
        {
            chunk = _buffer.ToString();
            _buffer.Clear();
            seq = ++_sequenceNumber;
        }

        if (!string.IsNullOrEmpty(chunk))
        {
            await SendChunkAsync(seq, chunk);
        }

        // Wait for all pending flush tasks to complete
        var pendingTasks = new List<Task>();
        while (_pendingFlushes.TryDequeue(out var task))
        {
            pendingTasks.Add(task);
        }

        if (pendingTasks.Count > 0)
        {
            await Task.WhenAll(pendingTasks);
        }
    }

    private async Task SendChunkAsync(int sequence, string content)
    {
        var chunk = new LogChunk
        {
            JobId = _jobId,
            SequenceNumber = sequence,
            Content = content,
            Timestamp = DateTime.UtcNow
        };

        if (_connection.State != HubConnectionState.Connected)
        {
            _messageBuffer.Enqueue(new BufferedMessage
            {
                Method = "StreamLogs",
                Args = new object[] { chunk }
            });
            return;
        }

        try
        {
            await _connection.InvokeAsync("StreamLogs", chunk);
        }
        catch
        {
            // Connection lost — buffer for replay
            _messageBuffer.Enqueue(new BufferedMessage
            {
                Method = "StreamLogs",
                Args = new object[] { chunk }
            });
        }
    }
}
