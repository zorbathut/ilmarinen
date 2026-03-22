namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Server → Client: Version information returned by /api/version endpoint
/// </summary>
public record VersionInfo
{
    public required string ProtocolHash { get; init; }
}
