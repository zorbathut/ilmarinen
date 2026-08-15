using System;
using System.Collections.Concurrent;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Thread-safe buffer for outgoing messages that failed to send.
/// Messages are retained until successfully replayed on reconnection.
/// </summary>
public class MessageBuffer
{
    private readonly ConcurrentQueue<BufferedMessage> _messages = new();

    public void Enqueue(BufferedMessage message)
    {
        _messages.Enqueue(message);
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
