using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System;

namespace Ilmarinen.Server.Services.Wake;

/// <summary>A queued job, as waking sees it, with the rank of its minimum worker priority; null for no minimum.</summary>
public sealed record WakeJob(Guid Id, int? MinWorkerRank, DateTimeOffset QueuedSince)
{
    public bool Fits(WakeWorker worker)
    {
        return MinWorkerRank == null || worker.Rank >= MinWorkerRank;
    }
}

/// <summary>A registered worker, as waking sees it, with the rank of its priority. <see cref="HasRunningJob"/>: the server has a job Running for it, which for a woken worker means it is back and has taken one.</summary>
public sealed record WakeWorker(Guid Id, string Name, int Rank, bool Paused, string? Mac, DateTimeOffset LastSeen, bool Connected, bool Ready, bool HasRunningJob);

/// <summary>A wake in progress: the worker's host was sent a packet and hasn't yet come back ready.</summary>
public sealed record WakeAttempt(Guid WorkerId, DateTimeOffset StartedAt, DateTimeOffset Deadline);

/// <summary>
/// What waking remembers between rounds: the wakes in progress, and when each worker whose last wake failed failed, until it is next seen. A failed worker is tried again once the backoff has passed, but isn't counted on until it has come back.
/// </summary>
public sealed record WakeState(ImmutableDictionary<Guid, WakeAttempt> Attempts, ImmutableDictionary<Guid, DateTimeOffset> FailedAt)
{
    public static readonly WakeState Empty = new(ImmutableDictionary<Guid, WakeAttempt>.Empty, ImmutableDictionary<Guid, DateTimeOffset>.Empty);
}

public sealed record WakeSettings(TimeSpan Timeout, TimeSpan Backoff, TimeSpan QueuedDelay, TimeSpan DisconnectedDelay);

public enum WakeEnd
{
    /// <summary>The worker came back and is ready, or has a job.</summary>
    Arrived,

    /// <summary>The worker didn't come back in time: it is left alone for the backoff.</summary>
    Unreachable,

    /// <summary>The worker came back but isn't ready (someone is using it, say): nothing more for waking to do.</summary>
    AwakeButNotReady
}

/// <summary>One round's decisions: the new state, the workers to send a packet to, which worker each waiting job is waiting on, and the wakes that ended.</summary>
public sealed record WakeRound(WakeState State, IReadOnlyList<Guid> Send, IReadOnlyDictionary<Guid, Guid> WaitingFor, IReadOnlyList<(Guid WorkerId, WakeEnd End)> Ended);

/// <summary>
/// Which sleeping workers to wake, decided afresh each round from the queue and the fleet. A queued job that has waited a while gets one sleeping worker woken for it; that is the job's reservation, and it reserves nothing in the queue: whichever worker is ready first takes the job, and the wake then serves the next job that needs one, or none. A wake that doesn't bring the worker back in time is given up on, and the worker left alone for a while.
/// </summary>
public static class WakePlan
{
    public static WakeRound Plan(IReadOnlyList<WakeJob> queued, IReadOnlyList<WakeWorker> workers, WakeState state, WakeSettings settings, DateTimeOffset now)
    {
        var byId = workers.ToDictionary(w => w.Id);
        var attempts = state.Attempts.ToBuilder();
        var failed = state.FailedAt.ToBuilder();
        var ended = new List<(Guid, WakeEnd)>();

        // A worker that is connected has come back, however it did.
        foreach (var worker in workers.Where(w => w.Connected))
        {
            failed.Remove(worker.Id);
        }

        // End the wakes that have come to something; one for a worker gone, or whose address has been cleared, just stops
        foreach (var attempt in state.Attempts.Values)
        {
            if (!byId.TryGetValue(attempt.WorkerId, out var worker) || worker.Mac == null)
            {
                attempts.Remove(attempt.WorkerId);
                continue;
            }

            WakeEnd? end = worker.Connected && (worker.Ready || worker.HasRunningJob) ? WakeEnd.Arrived
                : now < attempt.Deadline ? null
                : worker.Connected ? WakeEnd.AwakeButNotReady
                : WakeEnd.Unreachable;
            if (end is not { } how)
            {
                continue;
            }

            attempts.Remove(attempt.WorkerId);
            ended.Add((attempt.WorkerId, how));
            if (how == WakeEnd.Unreachable)
            {
                failed[attempt.WorkerId] = now;
            }
        }

        // Match the jobs that have waited long enough to the wakes in progress, then wake a sleeper for each left over
        var waitingFor = new Dictionary<Guid, Guid>();
        var unmatched = attempts.Values.Select(a => byId[a.WorkerId]).OrderByDescending(w => w.Rank).ThenBy(w => w.Id).ToList();
        var candidates = workers
            .Where(w => IsCandidate(w, attempts, failed, settings, now))
            .OrderByDescending(w => w.Rank).ThenByDescending(w => w.LastSeen).ThenBy(w => w.Id)
            .ToList();
        foreach (var job in queued.Where(j => now - j.QueuedSince >= settings.QueuedDelay))
        {
            var waking = unmatched.FirstOrDefault(job.Fits);
            if (waking != null)
            {
                unmatched.Remove(waking);
                waitingFor[job.Id] = waking.Id;
                continue;
            }

            var sleeper = candidates.FirstOrDefault(job.Fits);
            if (sleeper != null)
            {
                candidates.Remove(sleeper);
                attempts[sleeper.Id] = new WakeAttempt(sleeper.Id, now, now + settings.Timeout);
                waitingFor[job.Id] = sleeper.Id;
            }
        }

        // Every wake still in progress gets another packet: they are easily lost.
        var send = attempts.Keys.OrderBy(id => id).ToList();
        return new WakeRound(new WakeState(attempts.ToImmutable(), failed.ToImmutable()), send, waitingFor, ended);
    }

    /// <summary>Whether a sleeping worker may count on being woken: it has an address, isn't paused, and its last wake didn't fail.</summary>
    public static bool IsWakeable(WakeWorker worker, WakeState state)
    {
        return !worker.Connected && worker.Mac != null && !worker.Paused && !state.FailedAt.ContainsKey(worker.Id);
    }

    // Disconnected for longer than the server takes to notice a connection has died, so a worker that is only reconnecting (or a server restarting) isn't mistaken for one asleep.
    private static bool IsCandidate(WakeWorker worker, IDictionary<Guid, WakeAttempt> attempts, IDictionary<Guid, DateTimeOffset> failed, WakeSettings settings, DateTimeOffset now)
    {
        return !worker.Connected
            && worker.Mac != null
            && !worker.Paused
            && !attempts.ContainsKey(worker.Id)
            && !(failed.TryGetValue(worker.Id, out var at) && now - at < settings.Backoff)
            && now - worker.LastSeen > settings.DisconnectedDelay;
    }
}
