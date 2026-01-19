namespace Ilmarinen.Models;

/// <summary>
/// Represents a reference to a saved artifact.
/// </summary>
public sealed record ArtifactRef
{
    /// <summary>
    /// Unique identifier for this artifact.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Display name of the artifact.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Size in bytes.
    /// </summary>
    public required long Size { get; init; }
}
