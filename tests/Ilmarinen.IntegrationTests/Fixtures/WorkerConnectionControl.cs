using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>
/// A hub filter on the test server that can cut a worker's transport while the server keeps all its state — what a dropped or silently dead connection looks like from the server — and hold a reconnecting worker's handshake, so things the worker does in the meantime land on a connection that hasn't authenticated yet.
/// </summary>
public sealed class WorkerConnectionControl : IHubFilter
{
    private readonly ConcurrentDictionary<string, HubCallerContext> _connections = new();
    private volatile TaskCompletionSource? _handshakeHold;

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        _connections[context.Context.ConnectionId] = context.Context;
        await next(context);
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        _connections.TryRemove(context.Context.ConnectionId, out _);
        await next(context, exception);
    }

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (invocationContext.HubMethodName == "Connect" && _handshakeHold is { } hold)
        {
            await hold.Task;
        }
        return await next(invocationContext);
    }

    /// <summary>Drops the connection's transport. The client sees it close; the server runs its disconnect handling as it would for a real drop.</summary>
    public void Abort(string connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var context))
        {
            throw new InvalidOperationException($"No open connection {connectionId}; open: [{string.Join(", ", _connections.Keys)}]");
        }
        context.Abort();
    }

    /// <summary>Holds every Connect call until the returned handle is disposed.</summary>
    public IDisposable HoldHandshakes()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handshakeHold = hold;
        return new Release(() =>
        {
            _handshakeHold = null;
            hold.SetResult();
        });
    }

    private sealed class Release : IDisposable
    {
        private readonly Action _release;

        public Release(Action release)
        {
            _release = release;
        }

        public void Dispose()
        {
            _release();
        }
    }
}
