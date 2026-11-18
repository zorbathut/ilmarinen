using System;

namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Describes a persistent workspace on a worker, including its local path.
/// </summary>
public record WorkspaceInfo
{
    public required string Name { get; init; }
    public required string Path { get; init; }

    /// <summary>
    /// When the last job using this workspace finished with it (or, while one is still using it, when it last changed the workspace's top level).
    /// </summary>
    public required DateTimeOffset LastUsed { get; init; }
}
