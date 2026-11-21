using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>A worker free to take a job, as dispatch sees it: its priority's rank.</summary>
public sealed record DispatchWorker(Guid WorkerId, int Rank);

/// <summary>
/// Which free worker takes a queued job. Jobs are offered one at a time in dispatch order (highest job priority, then oldest), and each goes to the highest-priority free worker that qualifies for it.
///
/// Qualifying is nested by rank — a worker qualifies for everything a lower-priority one does — so this is the same matching as letting the highest-priority free worker take the first job it qualifies for, then the next worker, and so on.
/// </summary>
public static class DispatchPlan
{
    public static bool Qualifies(int workerRank, int? minWorkerRank)
    {
        return minWorkerRank == null || minWorkerRank <= workerRank;
    }

    /// <summary>The worker that takes a job with this minimum, or null if no free worker qualifies. Ties go to the lowest worker ID, so a plan is reproducible.</summary>
    public static DispatchWorker? Pick(int? minWorkerRank, IEnumerable<DispatchWorker> free)
    {
        return free
            .Where(w => Qualifies(w.Rank, minWorkerRank))
            .OrderByDescending(w => w.Rank)
            .ThenBy(w => w.WorkerId)
            .FirstOrDefault();
    }

    /// <summary>The whole matching of these jobs, given in dispatch order, to these free workers: each worker takes at most one job, and a job no free worker qualifies for is left out.</summary>
    public static IReadOnlyList<(T Job, DispatchWorker Worker)> Match<T>(IEnumerable<T> jobsInDispatchOrder, Func<T, int?> minWorkerRank, IEnumerable<DispatchWorker> free)
    {
        var pool = free.ToList();
        var matched = new List<(T, DispatchWorker)>();
        foreach (var job in jobsInDispatchOrder)
        {
            if (pool.Count == 0)
            {
                break;
            }

            if (Pick(minWorkerRank(job), pool) is { } worker)
            {
                pool.Remove(worker);
                matched.Add((job, worker));
            }
        }
        return matched;
    }
}
