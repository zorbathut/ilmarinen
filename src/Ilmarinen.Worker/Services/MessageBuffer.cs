using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Buffer for outgoing messages that failed to send, replayed when the worker next re-syncs. Lossy at the edges by
/// design: a message is dropped when the buffer is full (oldest first) or when it has failed too many syncs, because
/// the alternatives are an unbounded buffer and a worker that can never finish a sync.
///
/// Enqueue is safe from any thread. ReplayAsync is not: it walks the queue and keeps a retry count for the head, so it
/// assumes one caller at a time — WorkerService holds its sync lock across it.
/// </summary>
public class MessageBuffer
{
    /// <summary>
    /// How many messages a disconnected worker may hold. A chatty pipeline produces a log chunk every 100ms, so an hour
    /// offline is tens of thousands of them — unbounded, that is the worker's memory. Past the cap the oldest go first,
    /// which sheds log output and keeps the job result that was queued behind it.
    /// </summary>
    public const int MaxMessages = 2000;

    /// <summary>
    /// How many reconnects a single message may fail on before it is discarded. A message the server refuses every time
    /// would otherwise block every message behind it, and with them every sync — leaving a worker that is never Ready
    /// and never draining, forever.
    /// </summary>
    private const int MaxReplayAttempts = 5;

    private readonly ConcurrentQueue<BufferedMessage> _messages = new();
    private int _headAttempts;

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

    /// <summary>
    /// Sends everything the buffer holds, oldest first, and stops at the first message that fails so the rest keep both
    /// their order and their place in the queue. A message that has failed <see cref="MaxReplayAttempts"/> syncs is
    /// dropped rather than retried forever.
    ///
    /// Deliberately not decided by exception type: SignalR delivers a hub's considered refusal and an incidental
    /// server-side fault as the same HubException, so "the server will never accept this" can't be told from "the
    /// server's database blipped" — and discarding a job result over the latter would lose the job.
    /// </summary>
    public async Task ReplayAsync(Func<BufferedMessage, Task> send, ILogger logger)
    {
        while (_messages.TryPeek(out var message))
        {
            try
            {
                await send(message);
                _messages.TryDequeue(out _);
                _headAttempts = 0;
                continue;
            }
            catch (Exception ex)
            {
                _headAttempts++;

                if (_headAttempts < MaxReplayAttempts)
                {
                    // Throw rather than return: a quiet return would leave a connected-but-unsynced worker (never Ready, never draining) that nothing retries, and would reset the handshake-failure counter as if this sync had succeeded.
                    logger.LogWarning(ex, "Failed to replay {Method}, will retry on next reconnect", message.Method);
                    throw;
                }

                logger.LogError(ex, "Dropping {Method} after {Attempts} failed replays; it is holding up everything queued behind it", message.Method, _headAttempts);
            }

            _messages.TryDequeue(out _);
            _headAttempts = 0;
        }
    }

    /// <summary>Takes the next message without sending it. Nothing in the worker needs this — it is how tests read what was buffered.</summary>
    public bool TryDequeue(out BufferedMessage? message)
    {
        return _messages.TryDequeue(out message);
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
