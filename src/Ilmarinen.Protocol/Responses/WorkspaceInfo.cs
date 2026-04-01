namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Describes a persistent workspace on a worker, including its local path.
/// </summary>
public record WorkspaceInfo
{
    public required string Name { get; init; }
    public required string Path { get; init; }
}
