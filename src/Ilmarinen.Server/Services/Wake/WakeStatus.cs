using System.Collections.Generic;
using System;

namespace Ilmarinen.Server.Services.Wake;

/// <summary>A worker being woken, by id and name, for the job waiting on it.</summary>
public sealed record WakingWorker(Guid Id, string Name);

/// <summary>Where waking stands, for the pages and the stuck-job report to read: the latest round's state, and which worker each waiting job is waiting on. Kept apart from <see cref="WorkerWaker"/>, which reads the workers to decide it, so the worker views can read it back.</summary>
public sealed class WakeStatus
{
    private volatile Snapshot _current = new(WakeState.Empty, new Dictionary<Guid, WakingWorker>());

    private sealed record Snapshot(WakeState State, IReadOnlyDictionary<Guid, WakingWorker> WaitingFor);

    public WakeState State
    {
        get { return _current.State; }
    }

    public bool IsWaking(Guid workerId)
    {
        return _current.State.Attempts.ContainsKey(workerId);
    }

    /// <summary>When the worker's last wake failed, if it did and it hasn't been seen since.</summary>
    public DateTimeOffset? FailedAt(Guid workerId)
    {
        return _current.State.FailedAt.TryGetValue(workerId, out var at) ? at : null;
    }

    /// <summary>The worker being woken for a queued job, if one is.</summary>
    public WakingWorker? WaitingFor(Guid jobId)
    {
        return _current.WaitingFor.TryGetValue(jobId, out var worker) ? worker : null;
    }

    internal IReadOnlyDictionary<Guid, WakingWorker> WaitingForAll
    {
        get { return _current.WaitingFor; }
    }

    internal void Set(WakeState state, IReadOnlyDictionary<Guid, WakingWorker> waitingFor)
    {
        _current = new Snapshot(state, waitingFor);
    }
}
