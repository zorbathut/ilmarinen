using System;

namespace Ilmarinen.Protocol.Requests;

/// <summary>
/// Worker → Server: Initiate connection and request an authentication challenge.
/// </summary>
public record WorkerConnect
{
    public required Guid WorkerId { get; init; }
    public required string ProtocolHash { get; init; }
    public required byte[] Nonce { get; init; }
}
