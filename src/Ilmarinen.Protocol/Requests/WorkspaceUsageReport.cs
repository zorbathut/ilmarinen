using System.Collections.Generic;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// Worker → Server: disk usage of the worker's persistent workspaces and git mirrors, measured in the background and sent whenever a measurement finishes.
/// </summary>
public record WorkspaceUsageReport
{
    /// <summary>
    /// Per workspace name: the disk space deleting that workspace would free, i.e. blocks no file outside it links to. Null when it couldn't be measured.
    /// </summary>
    public required IReadOnlyDictionary<string, long?> WorkspaceSizes { get; init; }

    /// <summary>
    /// Disk space the git mirrors take. Null when it couldn't be measured.
    /// </summary>
    public long? MirrorsSizeBytes { get; init; }
}
