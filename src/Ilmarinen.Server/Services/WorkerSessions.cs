using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>What the server knows about the connection a worker is talking to it on. Lives exactly as long as that connection is the worker's current one.</summary>
public sealed class WorkerSession
{
    public WorkerSession(Guid workerId, string connectionId, Guid instanceId, long counter)
    {
        WorkerId = workerId;
        ConnectionId = connectionId;
        InstanceId = instanceId;
        Counter = counter;
    }

    public Guid WorkerId { get; }
    public string ConnectionId { get; }
    public Guid InstanceId { get; }
    public long Counter { get; }

    /// <summary>Set once Resume has been applied; until then nothing the connection says about jobs is acted on.</summary>
    public volatile bool Resumed;

    /// <summary>The worker's latest state report on this connection.</summary>
    public volatile WorkerState? State;

    /// <summary>Reported at authentication by launcher-run workers; null for classic workers.</summary>
    public volatile string? BundleHash;

    public ImmutableList<WorkspaceInfo> Workspaces = ImmutableList<WorkspaceInfo>.Empty;
}

/// <summary>Which connection a worker's handshake may take over from, as the protocol model decides it.</summary>
public static class SessionEpoch
{
    /// <summary>
    /// A later connection of the same process supersedes the current one; so does a process instance not yet superseded, which retires the one it replaces. A late handshake can then never take the worker's session from the connection that replaced it, and two processes sharing a key can't take turns forever.
    /// </summary>
    public static bool Supersedes(WorkerSession? current, IReadOnlyCollection<Guid> retired, Guid instanceId, long counter)
    {
        if (retired.Contains(instanceId))
        {
            return false;
        }

        return current == null || current.InstanceId != instanceId || counter > current.Counter;
    }
}

/// <summary>Whether a worker can be handed a job, derived from what it last reported rather than from messages that say so.</summary>
public static class WorkerReadiness
{
    public static bool IsReady(WorkerSession session, bool diagnosticOwed, bool hasRunningJob, string? serverBundleHash)
    {
        var state = session.State;
        return session.Resumed
            && state is { Activity: WorkerActivity.Idle, DiagnosticStatus: { } diagnostic }
            && DiagnosticStatusPolicy.CanAcceptJobs(diagnostic)
            && !HasStaleBundle(session, serverBundleHash)
            && !diagnosticOwed
            && !hasRunningJob;
    }

    /// <summary>A launcher-run worker on a different bundle from the one this server serves is about to exit for an update; it must not be handed work. Classic workers report no bundle and are never stale.</summary>
    public static bool HasStaleBundle(WorkerSession session, string? serverBundleHash)
    {
        return session.BundleHash != null && serverBundleHash != null && session.BundleHash != serverBundleHash;
    }
}

/// <summary>
/// Every worker's current session, and the lock that serializes everything that reads or changes a worker's state: its handshakes, Resume, state reports, outcomes, dispatch to it, cancelling its job and asking it for a diagnostic. Holding the lock makes each of those one step with respect to the others, which is what the protocol model assumes of the server.
/// </summary>
public sealed class WorkerSessions
{
    private const int RetiredInstancesKept = 16;

    private readonly ConcurrentDictionary<Guid, WorkerLine> _lines = new();
    private readonly ConcurrentDictionary<string, WorkerSession> _byConnection = new();
    private readonly ConcurrentDictionary<string, PendingHandshake> _handshakes = new();

    /// <summary>Records the challenge issued to a connection, for its Authenticate to answer.</summary>
    public void BeginHandshake(string connectionId, PendingHandshake handshake)
    {
        _handshakes[connectionId] = handshake;
    }

    /// <summary>Removes and returns the connection's pending challenge; each is answered at most once.</summary>
    public PendingHandshake? TakeHandshake(string connectionId)
    {
        _handshakes.TryRemove(connectionId, out var handshake);
        return handshake;
    }

    public async Task<IDisposable> LockAsync(Guid workerId)
    {
        var line = Line(workerId);
        await line.Lock.WaitAsync();
        return new Release(line.Lock);
    }

    /// <summary>Makes the candidate the worker's current session if its epoch allows. Caller holds the worker's lock.</summary>
    public bool TryStart(WorkerSession candidate, out WorkerSession? superseded)
    {
        var line = Line(candidate.WorkerId);
        superseded = line.Current;
        if (!SessionEpoch.Supersedes(line.Current, line.Retired, candidate.InstanceId, candidate.Counter))
        {
            return false;
        }

        if (superseded != null)
        {
            _byConnection.TryRemove(new KeyValuePair<string, WorkerSession>(superseded.ConnectionId, superseded));
            if (superseded.InstanceId != candidate.InstanceId)
            {
                line.Retired.Add(superseded.InstanceId);
                if (line.Retired.Count > RetiredInstancesKept)
                {
                    line.Retired.RemoveAt(0);
                }
            }
        }

        line.Current = candidate;
        _byConnection[candidate.ConnectionId] = candidate;
        return true;
    }

    /// <summary>Ends the connection's session if it is still the worker's current one, returning it. Caller holds the worker's lock.</summary>
    public WorkerSession? End(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var session))
        {
            return null;
        }

        var line = Line(session.WorkerId);
        if (line.Current == session)
        {
            line.Current = null;
        }
        return session;
    }

    /// <summary>The session of a connection that is its worker's current one; null for a superseded or unknown connection. Lock-free, for the calls that need only to know the connection is current.</summary>
    public WorkerSession? ByConnection(string connectionId)
    {
        _byConnection.TryGetValue(connectionId, out var session);
        return session;
    }

    public WorkerSession? ByWorker(Guid workerId)
    {
        return _lines.TryGetValue(workerId, out var line) ? line.Current : null;
    }

    public IReadOnlyList<WorkerSession> All()
    {
        return _byConnection.Values.ToList();
    }

    /// <summary>Whether the worker owes a diagnostic. Loaded from the database at each authentication, so it is current whenever the worker has a session.</summary>
    public bool DiagnosticOwed(Guid workerId)
    {
        return _lines.TryGetValue(workerId, out var line) && line.DiagnosticOwed;
    }

    /// <summary>Caller holds the worker's lock, and has written the database to match.</summary>
    public void SetDiagnosticOwed(Guid workerId, bool owed)
    {
        Line(workerId).DiagnosticOwed = owed;
    }

    /// <summary>Ends a revoked worker's session. The worker's lock stays, so nothing holding it is left with one nobody else can see. Caller holds the worker's lock.</summary>
    public void Forget(Guid workerId)
    {
        var line = Line(workerId);
        if (line.Current is { } current)
        {
            _byConnection.TryRemove(new KeyValuePair<string, WorkerSession>(current.ConnectionId, current));
            line.Current = null;
        }
        line.DiagnosticOwed = false;
    }

    private WorkerLine Line(Guid workerId)
    {
        return _lines.GetOrAdd(workerId, _ => new WorkerLine());
    }

    private sealed class WorkerLine
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public readonly List<Guid> Retired = [];
        public volatile WorkerSession? Current;
        public volatile bool DiagnosticOwed;
    }

    private sealed class Release : IDisposable
    {
        private SemaphoreSlim? _lock;

        public Release(SemaphoreSlim held)
        {
            _lock = held;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _lock, null)?.Release();
        }
    }
}

/// <summary>A challenge issued by Connect and not yet answered by Authenticate.</summary>
public sealed class PendingHandshake
{
    public required Guid WorkerId { get; init; }
    public required Guid InstanceId { get; init; }
    public required long ConnectionCounter { get; init; }
    public required byte[] WorkerNonce { get; init; }
    public required byte[] ServerNonce { get; init; }
    public required DateTime CreatedAt { get; init; }
    public string? BundleHash { get; init; }
}
