using Ilmarinen.Protocol;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Sends log output and commit reports on the worker's current, authenticated connection, acknowledged by the server's answer. A message that can't be sent — no such connection, a failed send, or an answer other than Ok — is buffered instead, and replayed when the worker next re-syncs.
///
/// Give it a plain logger, never a job's LoggerTee: a failed StreamLogs would otherwise log into the job's own log, which sends another chunk that fails the same way.
/// </summary>
public class BufferedHubSender
{
    private readonly Func<HubConnection?> _connection;
    private readonly MessageBuffer _buffer;
    private readonly ILogger _logger;
    private int _failing;
    private int _overflowing;

    /// <param name="connection">The connection to send on right now, or null while there is none the server would take these from.</param>
    public BufferedHubSender(Func<HubConnection?> connection, MessageBuffer buffer, ILogger logger)
    {
        _connection = connection;
        _buffer = buffer;
        _logger = logger;
    }

    /// <summary>
    /// Returns whether the message was actually delivered, as opposed to buffered. Never throws: callers flush job logs from inside their own catch blocks, where an escaping exception would leave the job Running on the server.
    /// </summary>
    public async Task<bool> SendOrBufferAsync(string method, params object[] args)
    {
        var connection = _connection();
        if (connection == null)
        {
            Buffer(method, args);
            return false;
        }

        try
        {
            var answer = await connection.InvokeCoreAsync<SessionAnswer>(method, args);
            if (answer != SessionAnswer.Ok)
            {
                _logger.LogDebug("Server answered {Answer} to {Method}, buffering for replay", answer, method);
                Buffer(method, args);
                return false;
            }

            Interlocked.Exchange(ref _failing, 0);
            Interlocked.Exchange(ref _overflowing, 0);
            return true;
        }
        catch (Exception ex)
        {
            // When one send fails, every send in flight on the same dead connection fails with it; on a half-open connection that is every log chunk from the last minute. Warn for the first and keep the rest at Debug until a send gets through again.
            if (Interlocked.Exchange(ref _failing, 1) == 0)
            {
                _logger.LogWarning(ex, "Failed to send {Method}, buffering for replay", method);
            }
            else
            {
                _logger.LogDebug(ex, "Failed to send {Method}, buffering for replay", method);
            }

            Buffer(method, args);
            return false;
        }
    }

    private void Buffer(string method, object[] args)
    {
        if (_buffer.Enqueue(new BufferedMessage { Method = method, Args = args }))
        {
            return;
        }

        // Once per outage, not once per dropped message: the latch clears on the next send that gets through.
        if (Interlocked.Exchange(ref _overflowing, 1) == 0)
        {
            _logger.LogWarning("Outgoing message buffer is full at {Max} messages; dropping the oldest, so some of this outage's log output will be missing", MessageBuffer.MaxMessages);
        }
    }
}
