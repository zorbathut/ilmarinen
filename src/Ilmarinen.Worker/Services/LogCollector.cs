using System.Text;
using System.Text.Json;
using Ilmarinen.Protocol.Requests;
using Microsoft.AspNetCore.SignalR.Client;
using NUlid;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Collects log output from pipeline execution and streams to server via SignalR.
/// Buffers small chunks to reduce network overhead.
/// </summary>
public class LogCollector
{
    private readonly Ulid _jobId;
    private readonly HubConnection _connection;
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();
    private int _sequenceNumber;
    private long _totalBytes;
    private DateTime _lastFlush = DateTime.UtcNow;

    private const int FlushBytes = 4096;
    private const int FlushMs = 100;

    public LogCollector(Ulid jobId, HubConnection connection)
    {
        _jobId = jobId;
        _connection = connection;
    }

    public long TotalBytes => _totalBytes;
    public int TotalChunks => _sequenceNumber;

    /// <summary>
    /// Write a log entry. Type is "o" for stdout, "e" for stderr.
    /// </summary>
    public void Write(string type, string data)
    {
        if (string.IsNullOrEmpty(data)) return;

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

        _ = TryFlushAsync();
    }

    public void WriteStdout(string data) => Write("o", data);
    public void WriteStderr(string data) => Write("e", data);

    /// <summary>
    /// Action callback that can be passed to PipelineRunner.
    /// </summary>
    public Action<string, string> AsCallback() => Write;

    private async Task TryFlushAsync()
    {
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
    /// Flush any remaining buffered output.
    /// </summary>
    public async Task FlushAsync()
    {
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
    }

    private async Task SendChunkAsync(int sequence, string content)
    {
        try
        {
            await _connection.SendAsync("StreamLogs", new LogChunk
            {
                JobId = _jobId,
                SequenceNumber = sequence,
                Content = content,
                Timestamp = DateTime.UtcNow
            });
        }
        catch
        {
            // Best effort - don't fail the job if log streaming fails
        }
    }
}
