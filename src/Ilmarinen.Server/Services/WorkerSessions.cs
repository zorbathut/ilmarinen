using Ilmarinen.Protocol.Responses;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>What the server knows about one authenticated connection of a worker. Lives exactly as long as the connection is the worker's.</summary>
public sealed class WorkerSession
{
    public WorkerSession(Guid workerId, string connectionId)
    {
        WorkerId = workerId;
        ConnectionId = connectionId;
    }

    public Guid WorkerId { get; }
    public string ConnectionId { get; }

    public volatile bool Ready;

    /// <summary>Reported at authentication by launcher-run workers; null for classic workers.</summary>
    public volatile string? BundleHash;

    public ImmutableList<WorkspaceInfo> Workspaces = ImmutableList<WorkspaceInfo>.Empty;
}

/// <summary>
/// The live session of every connected worker, and the rule that a worker has exactly one: a session started on a new connection supersedes whatever the worker held before.
/// </summary>
public sealed class WorkerSessions
{
    private readonly ConcurrentDictionary<string, WorkerSession> _byConnection = new();
    private readonly ConcurrentDictionary<Guid, WorkerSession> _byWorker = new();

    /// <summary>
    /// Starts a session for an authenticated connection, superseding any the worker still held. A connection can be half-open for as long as the SignalR client timeout: left in place, it stays a dispatch candidate, and a job handed to it is never delivered and never reconciled — the worker is connected, so nothing reconnects to correct it.
    /// </summary>
    public WorkerSession Start(string connectionId, Guid workerId)
    {
        var session = new WorkerSession(workerId, connectionId);
        _byConnection[connectionId] = session;

        // One atomic swap that hands back what it replaced, so exactly one session ends up current. The new one goes in before the old one comes out, so the worker is never momentarily without one. Two connections authenticating at once resolve to whichever lands second rather than to the newer of the two — self-correcting, since the loser's next hub call fails authentication and it re-syncs.
        WorkerSession? superseded = null;
        _byWorker.AddOrUpdate(workerId, session, (_, existing) =>
        {
            superseded = existing;
            return session;
        });

        if (superseded != null && superseded.ConnectionId != connectionId)
        {
            _byConnection.TryRemove(new KeyValuePair<string, WorkerSession>(superseded.ConnectionId, superseded));
        }

        return session;
    }

    /// <summary>Ends the connection's session, returning it if there was one.</summary>
    public WorkerSession? End(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var session))
        {
            return null;
        }

        // Only if this session is still the worker's. A superseded connection disconnects after the one that replaced it has already started, and ending the worker's session there would read as offline while it is connected and working.
        _byWorker.TryRemove(new KeyValuePair<Guid, WorkerSession>(session.WorkerId, session));
        return session;
    }

    public WorkerSession? ByConnection(string connectionId)
    {
        _byConnection.TryGetValue(connectionId, out var session);
        return session;
    }

    public WorkerSession? ByWorker(Guid workerId)
    {
        _byWorker.TryGetValue(workerId, out var session);
        return session;
    }

    public IReadOnlyList<WorkerSession> All()
    {
        return _byWorker.Values.ToList();
    }
}
