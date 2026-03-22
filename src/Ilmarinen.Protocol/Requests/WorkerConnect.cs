using NUlid;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// Worker → Server: Initiate connection and request an authentication challenge.
/// </summary>
public record WorkerConnect
{
    public required Ulid WorkerId { get; init; }
    public required string ProtocolHash { get; init; }
    public required byte[] Nonce { get; init; }
}
