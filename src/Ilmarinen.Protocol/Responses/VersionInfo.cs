namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Server → Client: Version information returned by /api/version endpoint
/// </summary>
public record VersionInfo
{
    public required int WorkerProtocolVersion { get; init; }
    public required int MinSupportedWorkerProtocolVersion { get; init; }
    public required string WorkerProtocolHash { get; init; }
}
