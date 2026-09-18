using System;
using System.Collections.Concurrent;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Thread-safe buffer for outgoing messages that failed to send.
/// Messages are retained until successfully replayed on reconnection, or dropped to keep the buffer bounded.
/// </summary>
public class MessageBuffer
{
    /// <summary>
    /// How many messages a disconnected worker may hold. A chatty pipeline produces a log chunk every 100ms, so an hour
    /// offline is tens of thousands of them — unbounded, that is the worker's memory. Past the cap the oldest go first,
    /// which sheds log output and keeps the job result that was queued behind it.
    /// </summary>
    public const int MaxMessages = 2000;

    private readonly ConcurrentQueue<BufferedMessage> _messages = new();

    /// <summary>Returns false if the buffer was full and the oldest message had to be dropped to make room.</summary>
    public bool Enqueue(BufferedMessage message)
    {
        _messages.Enqueue(message);

        var dropped = false;
        while (_messages.Count > MaxMessages && _messages.TryDequeue(out _))
        {
            dropped = true;
        }

        return !dropped;
    }

    public bool TryPeek(out BufferedMessage? message)
    {
        return _messages.TryPeek(out message);
    }

    public bool TryDequeue(out BufferedMessage? message)
    {
        return _messages.TryDequeue(out message);
    }

    public void Clear()
    {
        while (_messages.TryDequeue(out _)) { }
    }

    public bool IsEmpty
    {
        get { return _messages.IsEmpty; }
    }
}

public record BufferedMessage
{
    public required string Method { get; init; }
    public required object[] Args { get; init; }
}
