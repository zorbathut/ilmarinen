namespace Ilmarinen.Models;

/// <summary>
/// Represents a container image reference.
/// </summary>
public sealed record ImageRef
{
    /// <summary>
    /// The full image reference (e.g., "mcr.microsoft.com/dotnet/sdk:8.0").
    /// </summary>
    public required string Reference { get; init; }

    /// <summary>
    /// The image digest, if known (e.g., "sha256:abc123...").
    /// </summary>
    public string? Digest { get; init; }

    /// <summary>
    /// Creates an ImageRef from a string reference.
    /// </summary>
    public static ImageRef From(string reference) => new() { Reference = reference };

    /// <summary>
    /// Creates an ImageRef with a specific digest.
    /// </summary>
    public static ImageRef From(string reference, string digest) => new()
    {
        Reference = reference,
        Digest = digest
    };

    public override string ToString() => Digest is not null
        ? $"{Reference}@{Digest}"
        : Reference;

    /// <summary>
    /// Implicit conversion from string for convenience.
    /// </summary>
    public static implicit operator ImageRef(string reference) => From(reference);
}
