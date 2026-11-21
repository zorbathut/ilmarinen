using Ilmarinen.Protocol;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>
/// How backed up the fleet is, via GET /api/jobs/queue/estimate. Server-side only: a type in the protocol namespaces would change the protocol hash for a display feature.
/// </summary>
public record QueueEstimate
{
    public required int RunningJobs { get; init; }
    public required int QueuedJobs { get; init; }
    public required int ConnectedWorkers { get; init; }

    /// <summary>Connected, not paused, and either ready or running a job — the workers the queue can go to (as UnsatisfiableJobsReport defines them).</summary>
    public required int AvailableWorkers { get; init; }

    /// <summary>
    /// Seconds until everything running or queued now has finished, if the available workers take the queue as dispatch would. Optimistic: jobs with no history (<see cref="UnestimatedJobs"/>) count as no time, a worker is assumed free the moment its job ends, and <see cref="UnsatisfiableJobs"/> are left out. 0 when there is nothing to do.
    /// </summary>
    public required long EstimatedDrainSeconds { get; init; }

    /// <summary>Running or queued jobs with no estimate: neither the worker that runs them nor any other has succeeded at the script often enough to say how long it takes.</summary>
    public required int UnestimatedJobs { get; init; }

    /// <summary>Queued jobs no available worker's priority qualifies for; they wait for one, and the drain estimate leaves them out.</summary>
    public required int UnsatisfiableJobs { get; init; }
}

/// <summary>
/// Simulates the server's dispatch over what is running and queued now: whenever workers free up, the queue is matched to them as JobScheduler.DispatchAsync matches it (<see cref="DispatchPlan"/>), and each holds its job for that job's estimated duration.
/// </summary>
public static class QueueForecast
{
    /// <summary>A Running job: on which worker (absent from the available ones if that worker isn't there to take more), and how long it has been running.</summary>
    public sealed record RunningJob(Guid? WorkerId, string RepoUrl, string ScriptPath, TimeSpan Elapsed);

    /// <summary>A Queued job, listed in dispatch order (JobRepository.GetUnfinishedJobsAsync), with the rank of its minimum worker priority; null for no minimum.</summary>
    public sealed record QueuedJob(int? MinWorkerRank, string RepoUrl, string ScriptPath);

    /// <param name="estimates">A job's duration is its worker's own estimate for the script, else the estimate across all workers, else unknown (JobRepository.GetScriptEstimatesAsync).</param>
    public static QueueEstimate Estimate(
        IReadOnlyList<AvailableWorker> available,
        int connectedWorkers,
        IReadOnlyList<RunningJob> running,
        IReadOnlyList<QueuedJob> queued,
        ScriptEstimates estimates)
    {
        var unestimated = 0;
        var drain = TimeSpan.Zero;
        var freeAt = available.ToDictionary(w => w.WorkerId, _ => TimeSpan.Zero);

        TimeSpan DurationOf(Guid? workerId, string repoUrl, string scriptPath)
        {
            if (workerId is { } id && estimates.PerWorker.TryGetValue((id, repoUrl, scriptPath), out var own))
            {
                return own;
            }
            if (estimates.AcrossWorkers.TryGetValue((repoUrl, scriptPath), out var any))
            {
                return any;
            }
            unestimated++;
            return TimeSpan.Zero;
        }

        // What is running now finishes first, and holds its worker until then; one past its estimate is about to finish.
        foreach (var job in running)
        {
            var left = DurationOf(job.WorkerId, job.RepoUrl, job.ScriptPath) - job.Elapsed;
            var remaining = left > TimeSpan.Zero ? left : TimeSpan.Zero;
            drain = Max(drain, remaining);
            if (job.WorkerId is { } workerId && freeAt.TryGetValue(workerId, out var free))
            {
                freeAt[workerId] = Max(free, remaining);
            }
        }

        // The same rule as JobRepository.GetUnsatisfiableJobsAsync, over the same workers, so the two agree.
        var highest = available.Count > 0 ? available.Max(w => w.Rank) : (int?)null;
        var queue = queued.Where(j => highest is { } rank && DispatchPlan.Qualifies(rank, j.MinWorkerRank)).ToList();
        var unsatisfiable = queued.Count - queue.Count;

        // Each round hands every worker free at that moment a job, or drops one that qualifies for nothing left (the queue only shrinks, so it never will), so it ends; a job some worker qualifies for always has that worker still in the running.
        // Workspaces are left out: which worker of a priority takes a job hardly changes how long the queue takes.
        var active = available.Select(w => new DispatchWorker(w.WorkerId, w.Rank, new HashSet<string>())).ToList();
        while (queue.Count > 0 && active.Count > 0)
        {
            var now = active.Min(w => freeAt[w.WorkerId]);
            var freeNow = active.Where(w => freeAt[w.WorkerId] == now).ToList();
            var matched = DispatchPlan.Match(queue, j => new DispatchJob(j.MinWorkerRank, null), freeNow);
            foreach (var (job, worker) in matched)
            {
                queue.Remove(job);
                var duration = DurationOf(worker.WorkerId, job.RepoUrl, job.ScriptPath);
                freeAt[worker.WorkerId] = now + duration;
                drain = Max(drain, now + duration);
            }
            foreach (var idle in freeNow.Where(w => !matched.Any(m => m.Worker == w)))
            {
                active.Remove(idle);
            }
        }

        return new QueueEstimate
        {
            RunningJobs = running.Count,
            QueuedJobs = queued.Count,
            ConnectedWorkers = connectedWorkers,
            AvailableWorkers = available.Count,
            EstimatedDrainSeconds = (long)Math.Ceiling(drain.TotalSeconds),
            UnestimatedJobs = unestimated,
            UnsatisfiableJobs = unsatisfiable
        };
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b)
    {
        return a > b ? a : b;
    }
}
