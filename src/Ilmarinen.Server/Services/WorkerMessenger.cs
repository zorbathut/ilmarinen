using Ilmarinen.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>The connections a <see cref="WorkerMessenger"/> sends to; a seam so tests can watch what reaches each one.</summary>
public interface IWorkerClients
{
    IWorkerClient Client(string connectionId);
}

public sealed class HubWorkerClients : IWorkerClients
{
    private readonly IHubContext<WorkerHub, IWorkerClient> _hubContext;

    public HubWorkerClients(IHubContext<WorkerHub, IWorkerClient> hubContext)
    {
        _hubContext = hubContext;
    }

    public IWorkerClient Client(string connectionId)
    {
        return _hubContext.Clients.Client(connectionId);
    }
}

/// <summary>
/// Every message the server sends a worker goes through here: one queue per connection, drained by one task, so messages reach a worker in the order they were queued, and a caller holding a lock never waits on a slow or stalled connection.
/// </summary>
public sealed class WorkerMessenger
{
    private readonly IWorkerClients _clients;
    private readonly ILogger<WorkerMessenger> _logger;
    // Lazy, so two first sends racing for a connection can't each start a drain loop.
    private readonly ConcurrentDictionary<string, Lazy<Channel<Func<IWorkerClient, Task>>>> _queues = new();

    public WorkerMessenger(IWorkerClients clients, ILogger<WorkerMessenger> logger)
    {
        _clients = clients;
        _logger = logger;
    }

    /// <summary>Queues a message for a connection. A send that fails is logged; nothing is retried, since a worker reconciles whatever it missed when it reconnects.</summary>
    public void Send(string connectionId, string description, Func<IWorkerClient, Task> send)
    {
        var queue = _queues.GetOrAdd(connectionId, id => new Lazy<Channel<Func<IWorkerClient, Task>>>(() => StartQueue(id))).Value;
        if (!queue.Writer.TryWrite(async client =>
            {
                try
                {
                    await send(client);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send {Message} to worker connection {ConnectionId}", description, connectionId);
                }
            }))
        {
            _logger.LogWarning("Not sending {Message} to worker connection {ConnectionId}: it has disconnected", description, connectionId);
        }
    }

    /// <summary>Completes once everything queued for the connection so far has been sent.</summary>
    public Task DrainedAsync(string connectionId)
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queues.TryGetValue(connectionId, out var queue) || !queue.Value.Writer.TryWrite(_ =>
            {
                drained.SetResult();
                return Task.CompletedTask;
            }))
        {
            drained.SetResult();
        }
        return drained.Task;
    }

    /// <summary>Drops a disconnected connection's queue once what's already in it has been sent.</summary>
    public void Forget(string connectionId)
    {
        if (_queues.TryRemove(connectionId, out var queue))
        {
            queue.Value.Writer.TryComplete();
        }
    }

    private Channel<Func<IWorkerClient, Task>> StartQueue(string connectionId)
    {
        var queue = Channel.CreateUnbounded<Func<IWorkerClient, Task>>(new UnboundedChannelOptions { SingleReader = true });
        var client = _clients.Client(connectionId);
        _ = Task.Run(async () =>
        {
            await foreach (var send in queue.Reader.ReadAllAsync())
            {
                await send(client);
            }
        });
        return queue;
    }
}
