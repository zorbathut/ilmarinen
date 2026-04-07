using System;

namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Artifact metadata for API responses.
/// </summary>
public record ArtifactInfo
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public DateTime CreatedAt { get; init; }
}
