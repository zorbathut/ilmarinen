using Ilmarinen.Protocol.Requests;
using Microsoft.AspNetCore.SignalR.Client;
using NUlid;
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
    private readonly Ulid _jobId;
    private readonly HubConnection _connection;
    private readonly MessageBuffer _messageBuffer;
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();
    private readonly ConcurrentQueue<Task> _pendingFlushes = new();
    private int _sequenceNumber;
    private long _totalBytes;
    private DateTime _lastFlush = DateTime.UtcNow;
    private volatile bool _draining;

    private const int FlushBytes = 4096;
    private const int FlushMs = 100;

    public LogCollector(Ulid jobId, HubConnection connection, MessageBuffer messageBuffer)
    {
        _jobId = jobId;
        _connection = connection;
        _messageBuffer = messageBuffer;
    }

    public long TotalBytes => _totalBytes;
    public int TotalChunks => _sequenceNumber;

    /// <summary>
    /// Write a log entry. Type is "o" for stdout, "e" for stderr.
    /// </summary>
    public void Write(string type, string data)
    {
        if (string.IsNullOrEmpty(data) || _draining) return;

        var entry = JsonSerializer.Serialize(new
        {
            t = type,
            d = data,
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        lock (_lock)
        {
            _buffer.AppendLine(entry);
            _totalBytes += data.Length;
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

    public void WriteStdout(string data) => Write("o", data);
    public void WriteStderr(string data) => Write("e", data);

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
