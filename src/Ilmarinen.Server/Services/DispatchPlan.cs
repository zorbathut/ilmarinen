using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>A worker free to take a job, as dispatch sees it: its priority's rank, and its disk with the persistent workspaces it holds.</summary>
public sealed record DispatchWorker(int Rank, WorkerDisk Disk)
{
    public Guid WorkerId
    {
        get { return Disk.WorkerId; }
    }
}

/// <summary>A queued job, as dispatch sees it: the rank of its minimum worker priority (null for none), and the persistent workspace it is expected to use and the disk it is expected to take, from its script's latest runs (<see cref="DiskEstimate"/>).</summary>
public sealed record DispatchJob(int? MinWorkerRank, string? Workspace, DiskNeed Need);

/// <summary>A worker that would take a job but for its disk: how many queued jobs it qualifies for, none of which it has room for (some may not fit however its host is cleared); how far it is from the nearest of them; and how much of its disk is neither free, workspaces nor git mirrors, which freeing would let that one in.</summary>
public sealed record NoRoomWorker(int Jobs, DiskGap Gap, long OtherUseBytes);

/// <summary>The worker a job goes to, and how it fits on that worker's disk.</summary>
public sealed record DispatchPick(DispatchWorker Worker, DiskFit Fit);

/// <summary>
/// Which free worker takes a queued job. Jobs are offered one at a time in dispatch order (highest job priority, then oldest), and each goes to the highest-priority free worker that qualifies for it and has room for it, or can make room (<see cref="DiskEstimate.Fit"/>). Among workers of that priority:
/// 1. one that already holds the job's workspace and can reuse it, which saves both the disk a second copy would take and the time making it;
/// 2. then one with room without deleting anything, then one that deletes least;
/// 3. then the tightest fit, which keeps the roomiest disks for the jobs that need them.
///
/// A job no free worker has room for is passed over, and a later job may take the worker it would have had.
///
/// Qualifying is nested by rank — a worker qualifies for everything a lower-priority one does — so this is the same matching as letting the highest-priority free worker take the first job it qualifies for, then the next worker, and so on.
/// </summary>
public static class DispatchPlan
{
    public static bool Qualifies(int workerRank, int? minWorkerRank)
    {
        return minWorkerRank == null || minWorkerRank <= workerRank;
    }

    /// <summary>The worker that takes this job, or null if no free worker qualifies and has room. Remaining ties go to the lowest worker ID, so a plan is reproducible.</summary>
    public static DispatchPick? Pick(DispatchJob job, IEnumerable<DispatchWorker> free)
    {
        return free
            .Where(w => Qualifies(w.Rank, job.MinWorkerRank))
            .Select(w => DiskEstimate.Fit(job.Workspace, job.Need, w.Disk) is { } fit ? new DispatchPick(w, fit) : null)
            .OfType<DispatchPick>()
            .OrderByDescending(p => p.Worker.Rank)
            .ThenByDescending(p => p.Fit.Reuses)
            .ThenByDescending(p => p.Fit.EvictBytes == 0)
            .ThenBy(p => p.Fit.EvictBytes)
            .ThenBy(p => p.Fit.LeftoverBytes)
            .ThenBy(p => p.Worker.WorkerId)
            .FirstOrDefault();
    }

    /// <summary>
    /// Whether a worker of this rank is kept from every queued job it qualifies for by its disk (<see cref="DiskEstimate.Fit"/>), when freeing what isn't Ilmarinen's on it would let it take one: something to fix on its host. Its other use is what its disk report doesn't put down to free space, its workspaces or its git mirrors (<paramref name="mirrorsBytes"/>). A disk merely too small for the jobs waiting isn't flagged, nor one whose free space, workspaces or mirrors aren't all measured. Null unless it is.
    /// </summary>
    public static NoRoomWorker? RoomShortfall(int rank, WorkerDisk disk, long? mirrorsBytes, IReadOnlyList<DispatchJob> queued)
    {
        if (disk.Disk is not { } report || mirrorsBytes is not { } mirrors || disk.Workspaces.Values.Any(size => size == null))
        {
            return null;
        }
        var other = report.TotalBytes - report.FreeBytes - disk.Workspaces.Values.Sum(size => size!.Value) - mirrors;

        // Freeing the other use adds to the room on either way in alike, so the nearest job is the one it would let in first.
        var qualifying = queued.Where(j => Qualifies(rank, j.MinWorkerRank)).ToList();
        var nearest = qualifying.Select(j => DiskEstimate.Gap(j.Workspace, j.Need, disk)!).MinBy(g => g.ShortBytes);
        if (nearest == null || nearest.ShortBytes <= 0 || nearest.ShortBytes > other)
        {
            return null;
        }
        return new NoRoomWorker(qualifying.Count, nearest, other);
    }

    /// <summary>The whole matching of these jobs, given in dispatch order, to these free workers: each worker takes at most one job, and a job no free worker qualifies for and has room for is left out.</summary>
    public static IReadOnlyList<(T Job, DispatchWorker Worker)> Match<T>(IEnumerable<T> jobsInDispatchOrder, Func<T, DispatchJob> describe, IEnumerable<DispatchWorker> free)
    {
        var pool = free.ToList();
        var matched = new List<(T, DispatchWorker)>();
        foreach (var job in jobsInDispatchOrder)
        {
            if (pool.Count == 0)
            {
                break;
            }

            if (Pick(describe(job), pool) is { } pick)
            {
                pool.Remove(pick.Worker);
                matched.Add((job, pick.Worker));
            }
        }
        return matched;
    }
}
