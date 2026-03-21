namespace Ilmarinen.Protocol.Responses;

/// <summary>
/// Server → Worker: Challenge for ECDSA authentication.
/// </summary>
public record AuthChallenge
{
    public required byte[] Nonce { get; init; }
    public required byte[] ServerSignature { get; init; }
}
