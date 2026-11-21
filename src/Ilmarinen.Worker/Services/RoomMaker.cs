using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Frees the disk the server expects a job to take (<see cref="JobAssignment.DiskNeededBytes"/>) plus the worker's floor, before anything else runs: the mirror fetch and the checkout take space too.
///
/// If the server found the job fits only by making its workspace afresh, the worker first checks that against its own, fresher, reading — deleting a workspace can't be undone — and deletes its copy only if reusing it really doesn't fit. Then it deletes least-recently-used workspaces, never the job's own, as many as their measured sizes say it takes (<see cref="RoomPlan"/>). If that still isn't enough, the job runs anyway, and the disk watch stops it if it does run out.
/// </summary>
public sealed class RoomMaker
{
    private readonly WorkerConfig _config;
    private readonly WorkspaceManager _workspaces;
    private readonly IDiskSpace _disk;

    public RoomMaker(WorkerConfig config, WorkspaceManager workspaces, IDiskSpace disk)
    {
        _config = config;
        _workspaces = workspaces;
        _disk = disk;
    }

    /// <summary>Makes room for the job, writing each deletion and its reason to <paramref name="jobLogger"/>. Returns every workspace it tried to delete with how that went, for the server to hear of.</summary>
    public IReadOnlyList<(string Name, DeleteWorkspaceResult Result)> MakeRoom(JobAssignment job, ILogger jobLogger)
    {
        var deleted = new List<(string, DeleteWorkspaceResult)>();
        var floor = _config.MinFreeBytes;
        var needed = job.DiskNeededBytes + floor;

        if (job.StartFresh && job.PredictedWorkspace is { } own)
        {
            var others = _workspaces.RoomCandidates(own).Sum(c => c.FreedBytes ?? 0);
            if (Free() is { } free && free + others >= job.DiskNeededToReuseBytes + floor)
            {
                jobLogger.LogInformation("Reusing workspace {WorkspaceName} after all: there is room for that now", own);
                needed = job.DiskNeededToReuseBytes + floor;
            }
            else
            {
                jobLogger.LogInformation("Deleting workspace {WorkspaceName} to make it afresh: there isn't room to reuse it", own);
                deleted.Add((own, Delete(own, jobLogger)));
            }
        }

        if (Free() is not { } start || start >= needed)
        {
            return deleted;
        }

        foreach (var candidate in RoomPlan.Choose(_workspaces.RoomCandidates(job.PredictedWorkspace), needed - start))
        {
            if (Free() >= needed)
            {
                break;
            }
            jobLogger.LogInformation("Deleting workspace {WorkspaceName} (last used {LastUsed:u}, {Size} bytes) to make room: this job is expected to need {Needed:N0} bytes free", candidate.Name, candidate.LastUsed, candidate.FreedBytes?.ToString("N0") ?? "unmeasured", needed);
            deleted.Add((candidate.Name, Delete(candidate.Name, jobLogger)));
        }

        if (Free() is { } after && after < needed)
        {
            jobLogger.LogWarning("Only {Free:N0} bytes are free of the {Needed:N0} this job is expected to need, and no more workspaces can be deleted; running it anyway", after, needed);
        }
        return deleted;
    }

    private long? Free()
    {
        return _disk.Read(_config.WorkspacePath)?.FreeBytes;
    }

    private DeleteWorkspaceResult Delete(string name, ILogger jobLogger)
    {
        var result = _workspaces.TryDelete(name);
        if (result.Status != DeleteWorkspaceStatus.Success)
        {
            jobLogger.LogWarning("Couldn't delete workspace {WorkspaceName}: {Error}", name, result.Error);
        }
        return result;
    }
}
