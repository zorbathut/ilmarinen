using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Sends messages to the server with implicit acknowledgment via InvokeCoreAsync. A message that can't be sent — the connection is down, or the send fails — is buffered instead, and replayed when the worker next re-syncs after a reconnect.
/// </summary>
public class BufferedHubSender
{
    private readonly HubConnection _connection;
    private readonly MessageBuffer _buffer;
    private readonly ILogger _logger;

    public BufferedHubSender(HubConnection connection, MessageBuffer buffer, ILogger logger)
    {
        _connection = connection;
        _buffer = buffer;
        _logger = logger;
    }

    /// <summary>
    /// Returns whether the message was actually delivered, as opposed to buffered. Never throws.
    /// </summary>
    public async Task<bool> SendOrBufferAsync(string method, params object[] args)
    {
        if (_connection.State != HubConnectionState.Connected)
        {
            _buffer.Enqueue(new BufferedMessage { Method = method, Args = args });
            return false;
        }

        try
        {
            await _connection.InvokeCoreAsync(method, args);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send {Method}, buffering for replay", method);
            _buffer.Enqueue(new BufferedMessage { Method = method, Args = args });
            return false;
        }
    }
}
