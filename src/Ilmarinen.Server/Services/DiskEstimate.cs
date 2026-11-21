using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>One finished run of a script, as far as disk goes: what it worked in, how far it took free space down (<see cref="JobDiskUse"/>), and whether it succeeded.</summary>
public sealed record DiskRun(WorkspaceKind Kind, string? WorkspaceName, bool Fresh, long? UsedBytes, bool StoppedForDisk, bool Succeeded);

/// <summary>The disk a script's next run is expected to take, beyond the floor its worker keeps: on a worker that already holds its workspace and reuses it, and on one that makes it afresh. Zero for a script with no measured history.</summary>
public sealed record DiskNeed(long ReuseBytes, long FreshBytes)
{
    public static readonly DiskNeed None = new(0, 0);
}

/// <summary>What the server last heard of one worker's disk: free space (null before any report), and the persistent workspaces it holds with the space deleting each would free (null where unmeasured). Kept while the worker is away, so possibly stale then.</summary>
public sealed record WorkerDisk(Guid WorkerId, DiskSpaceReport? Disk, IReadOnlyDictionary<string, long?> Workspaces)
{
    /// <summary>The most free space the worker could have for a job, beyond its floor, were every workspace it holds deleted.</summary>
    public long? BestRoomBytes
    {
        get { return Disk == null ? null : Disk.FreeBytes + Workspaces.Values.Sum(s => s ?? 0) - Disk.MinFreeBytes; }
    }


}

/// <summary>How a job would fit on a worker's disk, needing <see cref="NeededBytes"/>. <see cref="StartFresh"/>: only if the worker first deletes its own copy of the job's workspace and makes it again. <see cref="EvictBytes"/>: how much of the worker's other workspaces it would delete first. <see cref="LeftoverBytes"/>: free space beyond what the job needs and the floor, after that; negative means eviction makes the difference.</summary>
public sealed record DiskFit(bool Reuses, bool StartFresh, long NeededBytes, long EvictBytes, long LeftoverBytes)
{
    /// <summary>A worker whose free space isn't known yet: assumed to fit without deleting anything, and so placed after every worker known to fit that way, and before any that would have to delete. It is still told what the job needs, and makes room by its own reading.</summary>
    public static DiskFit Unknown(bool holds, DiskNeed need)
    {
        return new DiskFit(holds, false, holds ? need.ReuseBytes : need.FreshBytes, 0, long.MaxValue);
    }
}

/// <summary>What a script's next run will want of a worker's disk, learned from its latest runs (<see cref="JobRepository.GetDiskHistoryAsync"/>), and whether a worker has it.</summary>
public static class DiskEstimate
{
    /// <summary>The persistent workspace the next run will use: the latest run's, newest first. Null for a script whose latest run worked in an ephemeral checkout, or that has no run that got as far as saying.</summary>
    public static string? PredictedWorkspace(IReadOnlyList<DiskRun> newestFirst)
    {
        return newestFirst.FirstOrDefault(r => r.Kind != WorkspaceKind.Unknown) is { Kind: WorkspaceKind.Named } latest ? latest.WorkspaceName : null;
    }

    /// <summary>
    /// The disk the next run will take, from the latest runs in the same kind of workspace (the same named one, or ephemeral checkouts): the most any took, since taking too little fills a disk and taking too much only delays the job. A run its worker stopped for disk needed more than it got, so counts double, and a script that keeps running out asks for more each time until it fits.
    ///
    /// Making the workspace afresh is judged by fresh runs, which <paramref name="newestFirst"/> carries more of than the latest few (<see cref="JobRepository.GetDiskHistoryAsync"/>): a workspace's only fresh run is often its first. Those that went the distance (succeeded, or were stopped for disk having used something) are taken over one that failed early, which took little and says little; with no fresh run at all, a fresh start is taken to cost what reusing does plus the workspace itself (<paramref name="knownWorkspaceBytes"/>, its largest size on any worker). A workspace that has grown past what a fresh run makes is what lets a fresh start fit where reusing doesn't. A fresh need that turns out too small corrects itself: the run is stopped for disk, and counts double next time.
    ///
    /// Both are capped at <paramref name="capBytes"/>, the most room any worker could make: a need inflated past every worker by someone filling a disk mid-run would otherwise never run again, and never correct itself. Capped, it runs on the roomiest worker, and the history moves on.
    /// </summary>
    public static DiskNeed Need(IReadOnlyList<DiskRun> newestFirst, long knownWorkspaceBytes, long capBytes)
    {
        var workspace = PredictedWorkspace(newestFirst);
        var same = newestFirst
            .Where(r => r.UsedBytes != null)
            .Where(r => workspace == null ? r.Kind == WorkspaceKind.Ephemeral : r.Kind == WorkspaceKind.Named && r.WorkspaceName == workspace)
            .ToList();

        static long Weight(DiskRun run)
        {
            return run.StoppedForDisk ? run.UsedBytes!.Value * 2 : run.UsedBytes!.Value;
        }

        var reused = same.Where(r => r.Kind == WorkspaceKind.Named && !r.Fresh).Select(Weight).ToList();
        var fresh = same.Where(r => r.Kind == WorkspaceKind.Ephemeral || r.Fresh).ToList();
        var whole = fresh.Where(r => r.Succeeded || (r.StoppedForDisk && r.UsedBytes > 0)).Select(Weight).ToList();

        var freshMax = fresh.Count > 0 ? fresh.Select(Weight).Max() : 0;
        var reuse = reused.Count > 0 ? reused.Max() : freshMax;
        var afresh = whole.Count > 0 ? whole.Max()
            : fresh.Count > 0 ? freshMax
            : reused.Count > 0 ? reused.Max() + knownWorkspaceBytes
            : 0;
        return new DiskNeed(Math.Min(reuse, capBytes), Math.Min(afresh, capBytes));
    }

    /// <summary>The need of a queued script's next run on this fleet (<see cref="Need"/>), capped at the most room any worker the server knows of could make, by its latest readings.</summary>
    public static DiskNeed NeedOnFleet(IReadOnlyList<DiskRun> newestFirst, IReadOnlyCollection<WorkerDisk> fleet)
    {
        var workspace = PredictedWorkspace(newestFirst);
        var known = workspace == null ? 0 : fleet.Select(w => w.Workspaces.GetValueOrDefault(workspace) ?? 0).DefaultIfEmpty(0).Max();
        var best = fleet.Select(w => w.BestRoomBytes).Where(r => r != null).Select(r => Math.Max(0, r!.Value)).DefaultIfEmpty(long.MaxValue).Max();
        return Need(newestFirst, known, best);
    }

    /// <summary>
    /// Whether a job needing <paramref name="need"/> fits on this worker, and how: reusing its copy of <paramref name="workspace"/> if it holds one and there is room for that, else making the workspace afresh, deleting its own copy first if it holds one. Either way the worker may delete its other workspaces to make room. Null when it doesn't fit even then.
    /// </summary>
    public static DiskFit? Fit(string? workspace, DiskNeed need, WorkerDisk worker)
    {
        var holds = workspace != null && worker.Workspaces.ContainsKey(workspace);
        if (worker.Disk is not { } disk)
        {
            return DiskFit.Unknown(holds, need);
        }

        // Unmeasured workspaces can't be counted on to free anything.
        var others = worker.Workspaces.Where(w => w.Key != workspace).Sum(w => w.Value ?? 0);
        var own = holds ? worker.Workspaces[workspace!] ?? 0 : 0;

        if (holds && disk.FreeBytes + others >= need.ReuseBytes + disk.MinFreeBytes)
        {
            return Fitting(reuses: true, startFresh: false, need.ReuseBytes);
        }
        if (disk.FreeBytes + others + own >= need.FreshBytes + disk.MinFreeBytes)
        {
            return Fitting(reuses: false, startFresh: holds, need.FreshBytes);
        }
        return null;

        DiskFit Fitting(bool reuses, bool startFresh, long needed)
        {
            // Deleting its own copy for a fresh start frees that first; anything more comes out of the others.
            var room = disk.FreeBytes + (startFresh ? own : 0);
            var shortfall = needed + disk.MinFreeBytes - room;
            return new DiskFit(reuses, startFresh, needed, Math.Max(0, shortfall), -shortfall);
        }
    }
}
