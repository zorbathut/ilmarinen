using Ilmarinen.Protocol;
using System.Collections.Generic;
using System.Linq;

namespace Ilmarinen.Server.Services;

/// <summary>One finished run of a script, as far as disk goes: what it worked in and how far it took free space down (<see cref="JobDiskUse"/>).</summary>
public sealed record DiskRun(WorkspaceKind Kind, string? WorkspaceName, bool Fresh, long? UsedBytes, bool StoppedForDisk);

/// <summary>What a script's next run will want of a worker's disk, learned from its latest runs (<see cref="JobRepository.GetDiskHistoryAsync"/>).</summary>
public static class DiskEstimate
{
    /// <summary>The persistent workspace the next run will use: the latest run's, newest first. Null for a script whose latest run worked in an ephemeral checkout, or that has no run that got as far as saying.</summary>
    public static string? PredictedWorkspace(IReadOnlyList<DiskRun> newestFirst)
    {
        return newestFirst.FirstOrDefault(r => r.Kind != WorkspaceKind.Unknown) is { Kind: WorkspaceKind.Named } latest ? latest.WorkspaceName : null;
    }
}
